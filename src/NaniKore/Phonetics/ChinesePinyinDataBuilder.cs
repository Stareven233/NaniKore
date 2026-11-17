using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaniKore.Phonetics;

/// <summary>CC-CEDICT 声调数字 → 拼音声调符号（ri4 → rì，lü3 → lǚ，轻声去掉数字）。</summary>
internal static class PinyinToneConverter
{
    private static readonly Dictionary<char, string[]> ToneMarks = new()
    {
        ['a'] = ["ā", "á", "ǎ", "à"],
        ['e'] = ["ē", "é", "ě", "è"],
        ['i'] = ["ī", "í", "ǐ", "ì"],
        ['o'] = ["ō", "ó", "ǒ", "ò"],
        ['u'] = ["ū", "ú", "ǔ", "ù"],
        ['ü'] = ["ǖ", "ǘ", "ǚ", "ǜ"]
    };

    /// <summary>把 "Ri4 Ben3" 形式的音节转成 "rì běn"。</summary>
    public static string ConvertSyllable(string syllable)
    {
        var match = Regex.Match(syllable, @"^([A-Za-züv]+)([0-5])$");
        if (!match.Success)
        {
            return NormalizeLetters(syllable);
        }

        var letters = NormalizeLetters(match.Groups[1].Value);
        var tone = match.Groups[2].Value[0] - '0';
        if (tone is 0 or 5)
        {
            return letters;
        }

        var vowelIndex = ChooseVowelIndex(letters);
        return vowelIndex < 0
            ? letters
            : letters[..vowelIndex] + ToneMarks[letters[vowelIndex]][tone - 1] + letters[(vowelIndex + 1)..];
    }

    /// <summary>把整串拼音（可能多音节空格分隔）统一转成声调符号形态。</summary>
    public static string ConvertPhrase(string pinyin) =>
        string.Join(' ', pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ConvertSyllable));

    private static string NormalizeLetters(string syllable) =>
        syllable.ToLowerInvariant().Replace('v', 'ü');

    /// <summary>标调位选择：有 a/e 优先，其次 ou 标 o，否则标最后一个元音。</summary>
    private static int ChooseVowelIndex(string letters)
    {
        for (var i = 0; i < letters.Length; i++)
        {
            if (letters[i] is 'a' or 'e')
            {
                return i;
            }
        }

        var ou = letters.IndexOf("ou", StringComparison.Ordinal);
        if (ou >= 0)
        {
            return ou;
        }

        for (var i = letters.Length - 1; i >= 0; i--)
        {
            if (ToneMarks.ContainsKey(letters[i]))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// 中文拼音运行期数据的构建器：把 `resources/phonetics/` 下的公开源料合并为一张紧凑表
/// （`deps/phonetics/zh/zh_pinyin.txt`，设置页按需下载）。合并口径：pypinyin 词组 + 单字候选为基座，
/// CC-CEDICT 补稀有字词与整词覆盖、并提供词级对齐的繁->简映射，Unihan kMandarin 兜底单字。
/// 产物确定性（全量排序）；本地明文由统一打包脚本压缩后上传。
/// </summary>
internal static class ChinesePinyinDataBuilder
{
    /// <summary>数据格式版本；运行期装载不认则视为不可用，避免旧产物误用。</summary>
    public const string FormatVersion = "NaniKore.zh.pinyin.v1";

    private static readonly Regex CedictLine = new(@"^(\S+)\s+(\S+)\s+\[([^\]]+)\]\s+/", RegexOptions.Compiled);

    /// <summary>由仓库根读取源料并写出运行期数据文件；返回写出路径。</summary>
    public static string Build(string repoRoot)
    {
        var resourceRoot = Path.Combine(repoRoot, "resources", "phonetics");
        var phrasesJson = Path.Combine(resourceRoot, "pypinyin", "phrases_dict.json");
        var pinyinJson = Path.Combine(resourceRoot, "pypinyin", "pinyin_dict.json");
        var cedict = Path.Combine(resourceRoot, "cc-cedict", "cedict_1_0_ts_utf-8_mdbg.txt");
        var unihan = Path.Combine(resourceRoot, "unihan", "Unihan_Readings.txt");
        Guard(File.Exists(phrasesJson), $"缺少 {phrasesJson}");
        Guard(File.Exists(pinyinJson), $"缺少 {pinyinJson}");
        Guard(File.Exists(cedict), $"缺少 {cedict}");

        var single = LoadPypinyinSingles(pinyinJson);
        var phrase = LoadPypinyinPhrases(phrasesJson);

        var fallback = new Dictionary<int, string[]>();
        var t2s = new Dictionary<char, char>();
        var cedictPhrases = new Dictionary<string, string[]>(StringComparer.Ordinal);
        LoadCedict(cedict, single, fallback, phrase, cedictPhrases, t2s);

        // CC-CEDICT 词条只补 pypinyin 未覆盖的词面（pypinyin 优先）
        var ccAdded = 0;
        foreach (var (surface, readings) in cedictPhrases)
        {
            if (!phrase.ContainsKey(surface))
            {
                phrase[surface] = readings;
                ccAdded++;
            }
        }

        var unihanAdded = 0;
        if (File.Exists(unihan))
        {
            unihanAdded = LoadUnihanFallback(unihan, single, fallback);
        }

        var target = PhoneticDataPaths.ChinesePinyinDataFile;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WriteDataFile(target, single, fallback, phrase, t2s);

        // 本地保存明文；统一打包脚本再生成 ModelScope 使用的 gzip 文件。
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
        Console.WriteLine($"[zh] 单字 {single.Count}（兜底 {fallback.Count}，Unihan 新增 {unihanAdded}），"
            + $"词组 {phrase.Count}（CC 新增 {ccAdded}），繁简映射 {t2s.Count} → {target}");
        Console.WriteLine($"[zh] 拼音数据 {new FileInfo(target).Length} 字节，SHA-256={hash}");
        return target;
    }

    private static Dictionary<int, string[]> LoadPypinyinSingles(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new Dictionary<int, string[]>(document.RootElement.GetPropertyCount());
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var codepoint))
            {
                continue;
            }

            var candidates = property.Value.GetString()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (candidates is { Length: > 0 })
            {
                result[codepoint] = candidates;
            }
        }

        return result;
    }

    private static Dictionary<string, string[]> LoadPypinyinPhrases(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var surface = property.Name;
            if (surface.Length < 1)
            {
                continue;
            }

            var readings = new List<string>(surface.Length);
            var valid = true;
            foreach (var perChar in property.Value.EnumerateArray())
            {
                var first = perChar.ValueKind == JsonValueKind.Array && perChar.GetArrayLength() > 0
                    ? perChar[0].GetString()
                    : null;
                if (string.IsNullOrEmpty(first))
                {
                    valid = false;
                    break;
                }

                readings.Add(first);
            }

            if (valid && readings.Count == surface.Length)
            {
                result[surface] = readings.ToArray();
            }
        }

        return result;
    }

    private static void LoadCedict(
        string path,
        Dictionary<int, string[]> single,
        Dictionary<int, string[]> fallback,
        Dictionary<string, string[]> pypinyinPhrase,
        Dictionary<string, string[]> cedictPhrase,
        Dictionary<char, char> t2s)
    {
        foreach (var raw in File.ReadLines(path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            var match = CedictLine.Match(raw);
            if (!match.Success)
            {
                continue;
            }

            var traditional = match.Groups[1].Value;
            var simplified = match.Groups[2].Value;

            // 词级繁->简字对齐映射（等长且逐字不同才收），供查音前归一
            if (traditional.Length == simplified.Length)
            {
                for (var i = 0; i < traditional.Length; i++)
                {
                    var t = traditional[i];
                    var s = simplified[i];
                    if (t != s && Translation.RubyTextParser.IsCjkKanji(t) && Translation.RubyTextParser.IsCjkKanji(s))
                    {
                        t2s.TryAdd(t, s);
                    }
                }
            }

            if (!IsAllHanzi(simplified))
            {
                continue;
            }

            var readings = new string[simplified.Length];
            var syllables = match.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (syllables.Length != simplified.Length)
            {
                continue;
            }

            for (var i = 0; i < syllables.Length; i++)
            {
                readings[i] = PinyinToneConverter.ConvertSyllable(syllables[i]);
            }

            if (simplified.Length == 1)
            {
                if (!single.ContainsKey(simplified[0]))
                {
                    fallback.TryAdd(simplified[0], [readings[0]]);
                }
            }
            else
            {
                cedictPhrase.TryAdd(simplified, readings);
            }
        }
    }

    private static int LoadUnihanFallback(string path, Dictionary<int, string[]> single, Dictionary<int, string[]> fallback)
    {
        var added = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal) || !line.Contains("\tkMandarin\t", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 3 ||
                !int.TryParse(fields[0][2..], System.Globalization.NumberStyles.HexNumber, null, out var codepoint) ||
                codepoint > 0xFFFF)
            {
                continue;
            }

            var ch = (char)codepoint;
            if (!Translation.RubyTextParser.IsCjkKanji(ch) || single.ContainsKey(codepoint) || fallback.ContainsKey(codepoint))
            {
                continue;
            }

            var reading = PinyinToneConverter.ConvertPhrase(fields[2].Trim());
            if (reading.Length == 0)
            {
                continue;
            }

            fallback[codepoint] = [reading];
            added++;
        }

        return added;
    }

    private static void WriteDataFile(
        string target,
        Dictionary<int, string[]> single,
        Dictionary<int, string[]> fallback,
        Dictionary<string, string[]> phrase,
        Dictionary<char, char> t2s)
    {
        using var file = File.Create(target);
        using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(FormatVersion);
        writer.Write('\n');

        foreach (var codepoint in single.Keys.Order())
        {
            writer.Write('S');
            writer.Write('\t');
            writer.Write(codepoint);
            writer.Write('\t');
            writer.Write(string.Join(',', single[codepoint]));
            writer.Write('\n');
        }

        foreach (var codepoint in fallback.Keys.Order())
        {
            writer.Write('C');
            writer.Write('\t');
            writer.Write(codepoint);
            writer.Write('\t');
            writer.Write(string.Join(',', fallback[codepoint]));
            writer.Write('\n');
        }

        foreach (var surface in phrase.Keys.Order(StringComparer.Ordinal))
        {
            writer.Write('P');
            writer.Write('\t');
            writer.Write(surface);
            foreach (var reading in phrase[surface])
            {
                writer.Write('\t');
                writer.Write(reading);
            }

            writer.Write('\n');
        }

        foreach (var from in t2s.Keys.Order())
        {
            writer.Write('T');
            writer.Write('\t');
            writer.Write(from);
            writer.Write('\t');
            writer.Write(t2s[from]);
            writer.Write('\n');
        }
    }

    private static bool IsAllHanzi(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!Translation.RubyTextParser.IsCjkKanji(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static void Guard(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
