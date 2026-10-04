using System.Security.Cryptography;
using NaniKore.Translation;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NaniKore.Phonetics;

/// <summary>CMUdict ARPAbet → IPA 转换（美音口径的单读音近似；多音词取主读音，生成期定音）。</summary>
internal static class ArpabetIpaConverter
{
    private static readonly Dictionary<string, string> PhoneToIpa = new(StringComparer.Ordinal)
    {
        // 元音：应激位（1 主重音 / 2 次重音）在拼装时前置于音节首元音
        ["AA"] = "ɑː", ["AE"] = "æ", ["AH"] = "ə", ["AO"] = "ɔː",
        ["AW"] = "aʊ", ["AY"] = "aɪ", ["EH"] = "e", ["ER"] = "ɜːr",
        ["EY"] = "eɪ", ["IH"] = "ɪ", ["IY"] = "i", ["OW"] = "oʊ",
        ["OY"] = "ɔɪ", ["UH"] = "ʊ", ["UW"] = "uː",
        ["AX"] = "ə", ["IX"] = "ə", ["UX"] = "ʊ",
        // 辅音
        ["B"] = "b", ["CH"] = "tʃ", ["D"] = "d", ["DH"] = "ð", ["DX"] = "ɾ",
        ["F"] = "f", ["G"] = "ɡ", ["HH"] = "h", ["JH"] = "dʒ",
        ["K"] = "k", ["L"] = "l", ["M"] = "m", ["N"] = "n", ["NG"] = "ŋ",
        ["P"] = "p", ["Q"] = "ʔ", ["R"] = "r", ["S"] = "s", ["SH"] = "ʃ",
        ["T"] = "t", ["TH"] = "θ", ["V"] = "v", ["W"] = "w",
        ["Y"] = "j", ["Z"] = "z", ["ZH"] = "ʒ"
    };

    /// <summary>把 "Y UW0 B IH1 K W IH0 T AH0 S" 转成 "juːbɪˈkwɪtəs"。</summary>
    public static string Convert(string arpabet)
    {
        var phones = arpabet.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();
        foreach (var raw in phones)
        {
            var separator = raw[^1] - '0';
            var phone = separator is >= 0 and <= 2 ? raw[..^1] : raw;
            if (!PhoneToIpa.TryGetValue(phone, out var ipa))
            {
                continue;
            }

            if (separator == 1)
            {
                builder.Append('ˈ');
            }
            else if (separator == 2)
            {
                builder.Append('ˌ');
            }

            builder.Append(ipa);
        }

        return builder.ToString();
    }
}

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
/// 词典生成管线（开发期 CLI：--build-phonetics）：把公开数据源编译为 deps/phonetics/ 下的运行期产物。
///   英语：CMUdict（ARPAbet）→ ipa_dict.db（SQLite，word 小写主键 + IPA）；
///   中文：CC-CEDICT + Unihan kMandarin → MeCab CSV → mecab-dict-index → sys.dic 等二进制；
///   日语：直接采用 IPAdic 编译态二进制（无需编译，见 tools/phonetics/README.md）。
/// 生成后按控制台输出的 SHA-256 回填 PhoneticDependencyManifest（哈希即版本）。
/// </summary>
internal static class PhoneticDictionaryBuilder
{
    internal static int Run(string repoRoot, string? dictIndexPath)
    {
        var sourceDirectory = Path.Combine(repoRoot, "deps", "phonetics", "_source");
        BuildEnglishDatabase(sourceDirectory, repoRoot);
        BuildChineseDictionary(sourceDirectory, repoRoot, dictIndexPath);
        PrintManifest(repoRoot);
        return 0;
    }

    private static void BuildEnglishDatabase(string sourceDirectory, string repoRoot)
    {
        var cmudictPath = Path.Combine(sourceDirectory, "cmudict.dict");
        Guard(File.Exists(cmudictPath), $"缺少 {cmudictPath}");
        var target = PhoneticDependencyPaths.EnglishDictionaryFile;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        DeleteWithRetry(target);

        using (var connection = new SqliteConnection($"Data Source={target}"))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE words (word TEXT PRIMARY KEY, ipa TEXT NOT NULL) WITHOUT ROWID";
                command.ExecuteNonQuery();
            }

            var inserted = 0;
            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO words (word, ipa) VALUES ($word, $ipa)";
                var wordParameter = command.Parameters.Add("$word", SqliteType.Text);
                var ipaParameter = command.Parameters.Add("$ipa", SqliteType.Text);
                foreach (var line in File.ReadLines(cmudictPath))
                {
                    if (line.Length == 0 || line.StartsWith(";;;", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var space = line.IndexOf(' ', StringComparison.Ordinal);
                    if (space <= 0)
                    {
                        continue;
                    }

                    var head = line[..space];
                    if (head.Contains('(', StringComparison.Ordinal))
                    {
                        continue; // WORD(2) 变音只保留主音
                    }

                    if (head.Equals("#", StringComparison.Ordinal) || head.Length < 2)
                    {
                        continue; // 跳过单字母缩写项，主流程只处理常用词
                    }

                    var ipa = ArpabetIpaConverter.Convert(line[(space + 1)..]);
                    if (ipa.Length == 0)
                    {
                        continue;
                    }

                    wordParameter.Value = head.ToLowerInvariant();
                    ipaParameter.Value = ipa;
                    command.ExecuteNonQuery();
                    inserted++;
                }

                transaction.Commit();
            }

            Console.WriteLine($"[en] ipa_dict.db: {inserted} 词条, {new FileInfo(target).Length / 1024.0 / 1024.0:F2} MB");
        }
    }

    /// <summary>删除同样可能被实时扫描占用，重试兜底。</summary>
    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>杀毒软件实时扫描会短暂占住新写入文件，移动/覆盖均需重试兜底。</summary>
    private static void MoveWithRetry(string source, string target)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (IOException exception)
            {
                last = exception;
                Thread.Sleep(500);
            }
        }

        throw new IOException($"移动失败：{source} -> {target}（{last?.GetType().Name}: {last?.Message}, HResult=0x{last?.HResult:X8}）", last);
    }

    private static void BuildChineseDictionary(string sourceDirectory, string repoRoot, string? dictIndexPath)
    {
        var cedictPath = Path.Combine(sourceDirectory, "cedict.u8");
        var unihanPath = Path.Combine(sourceDirectory, "unihan", "Unihan_Readings.txt");
        Guard(File.Exists(cedictPath), $"缺少 {cedictPath}");
        Guard(File.Exists(unihanPath), $"缺少 {unihanPath}");
        var dictIndex = ResolveDictIndex(dictIndexPath);
        Guard(dictIndex is not null, "找不到 mecab-dict-index（--dict-index 指定或加入 PATH），见 tools/phonetics/README.md");

        var buildDirectory = Path.Combine(repoRoot, "deps", "phonetics", "_build", "zh");
        if (Directory.Exists(buildDirectory))
        {
            Directory.Delete(buildDirectory, recursive: true);
        }

        Directory.CreateDirectory(buildDirectory);
        WriteMeCabSkeleton(buildDirectory);

        // 同一词面保留多个读音词条（MeCab CSV 允许重复词面）：多音字 行[háng]/行[xíng] 各自成条，
        // 分词时按上下文自选，与「异读补标」去重规则天然衔接
        var entries = new HashSet<(string Surface, string Pinyin)>();
        foreach (var line in File.ReadLines(cedictPath))
        {
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(line, @"^(\S+)\s+(\S+)\s+\[([^\]]+)\]\s+/(.*)/$");
            if (!match.Success)
            {
                continue;
            }

            var surface = match.Groups[2].Value;
            if (surface.Length == 0 || !surface.All(IsHanzi))
            {
                continue; // 简体中文只收汉字词面；含拉丁/符号的词面留给英语标注器与未登录处理
            }

            var pinyin = PinyinToneConverter.ConvertPhrase(match.Groups[3].Value);
            if (pinyin.Length == 0)
            {
                continue;
            }

            entries.Add((surface, pinyin));
        }

        var singleCharCovered = entries.Where(entry => entry.Surface.Length == 1).Select(entry => entry.Surface[0]).ToHashSet();
        var unihanAdded = 0;
        foreach (var line in File.ReadLines(unihanPath))
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal) || !line.Contains("\tkMandarin\t", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 3 || !int.TryParse(fields[0][2..], System.Globalization.NumberStyles.HexNumber, null, out var codePoint) || codePoint > 0xFFFF)
            {
                continue;
            }

            var ch = (char)codePoint;
            if (singleCharCovered.Contains(ch) || !IsHanzi(ch))
            {
                continue;
            }

            var reading = PinyinToneConverter.ConvertPhrase(fields[2]);
            if (reading.Length == 0)
            {
                continue;
            }

            entries.Add((ch.ToString(), reading));
            singleCharCovered.Add(ch);
            unihanAdded++;
        }

        // 多字词低价、单字高价、未登录组更高：让 Viterbi 自然偏向词典词面，未登录汉字串回退单字默认读音
        var csvPath = Path.Combine(buildDirectory, "main.csv");
        using (var writer = new StreamWriter(csvPath, append: false, Utf8NoBom))
        {
            foreach (var (surface, pinyin) in entries)
            {
                var cost = surface.Length == 1 ? 3000 : 100;
                writer.WriteLine($"{surface},0,0,{cost},{pinyin}");
            }
        }

        Console.WriteLine($"[zh] 词条 {entries.Count}（其中 CC-CEDICT 单字 {singleCharCovered.Count - unihanAdded}，Unihan 补字 {unihanAdded}）");
        Console.WriteLine("[zh] 调用 mecab-dict-index 编译中…");
        var startInfo = new ProcessStartInfo(dictIndex!)
        {
            WorkingDirectory = buildDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(buildDirectory);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(buildDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("utf-8");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("utf-8");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("mecab-dict-index 启动失败");
        process.WaitForExit();
        Guard(process.ExitCode == 0, $"mecab-dict-index 退出码 {process.ExitCode}，编译失败");

        foreach (var file in new[] { "sys.dic", "unk.dic", "matrix.bin", "char.bin", "dicrc" })
        {
            var source = Path.Combine(buildDirectory, file);
            if (File.Exists(source))
            {
                MoveWithRetry(source, Path.Combine(PhoneticDependencyPaths.ChineseDictionaryDirectory, file));
            }
        }

        Console.WriteLine("[zh] sys.dic 等已落盘 deps/phonetics/zh/");
    }

    /// <summary>MeCab 管道全部要求无 BOM 的 UTF-8（BOM 会让 dicrc/CSV 首行解析失败）。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static void WriteMeCabSkeleton(string directory)
    {
        // 单一上下文类别 + 低成本连接：没有语料统计矩阵可用时，词面成本本身即「最长词面优先」的偏好信号（matrix.def 只列非零项：left right cost）
        File.WriteAllText(Path.Combine(directory, "matrix.def"), "1 1\n0 0 1\n", Utf8NoBom);
        File.WriteAllText(Path.Combine(directory, "char.def"), """
            DEFAULT 0 1 0
            SPACE 0 1 0
            CJK 0 1 0
            0x0001..0x0008 DEFAULT
            0x0009..0x000D SPACE
            0x0020 SPACE
            0x0021..0x00FF DEFAULT
            0x3040..0x30FF DEFAULT
            0x3400..0x4DBF CJK
            0x4E00..0x9FFF CJK
            0xF900..0xFAFF CJK
            """, Utf8NoBom);
        File.WriteAllText(Path.Combine(directory, "unk.def"), """
            DEFAULT,0,0,20000,*,*,*,*,*,*,*,*
            SPACE,0,0,20000,*,*,*,*,*,*,*,*
            CJK,0,0,20000,*,*,*,*,*,*,*,*
            """, Utf8NoBom);
        File.WriteAllText(Path.Combine(directory, "dicrc"), """
            ; NaniKore zh 注音词典（CC-CEDICT + Unihan 转译）
            dicdir = .
            userdic =
            output-format-type =
            input-buffer-size = 8192
            node-format = %m\t%H\n
            unk-feature =
            bos-feature =
            eos-feature =
            cost-factor = 800
            """, Utf8NoBom);
    }

    private static string? ResolveDictIndex(string? dictIndexPath)
    {
        if (!string.IsNullOrWhiteSpace(dictIndexPath) && File.Exists(dictIndexPath))
        {
            return dictIndexPath;
        }

        foreach (var name in new[] { "mecab-dict-index.exe", "mecab-dict-index" })
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            foreach (var directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void PrintManifest(string repoRoot)
    {
        Console.WriteLine("---- SHA-256 清单（回填到 PhoneticDependencyManifest） ----");
        Console.WriteLine($"[定位] 词典根目录：{PhoneticDependencyPaths.PhoneticsRootDirectory}");
        foreach (var language in PhoneticDependencyManifest.SupportedLanguages)
        {
            foreach (var file in PhoneticDependencyManifest.GetFiles(language))
            {
                var path = PhoneticDependencyManifest.GetApplicationPath(file.RelativePath);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Console.WriteLine($"{file.RelativePath}=\"{Convert.ToHexString(SHA256.HashData(stream))}\"");
                }
                catch (IOException exception)
                {
                    Console.WriteLine($"{file.RelativePath}=读取失败（{exception.Message}），请单独重跑本步骤");
                }
            }
        }
    }

    private static bool IsHanzi(char ch) => RubyTextParser.IsCjkKanji(ch);

    private static void Guard(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
