using System.Security.Cryptography;
using NaniKore.Translation;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NaniKore.Phonetics;

/// <summary>
/// CMUdict ARPAbet → 词典式 IPA（美音口径的单读音近似；多音词取主读音，生成期定音）。
/// 音节切分、重音归位与长音符补全全部委托 <see cref="IpaTranscription"/>，与运行期读取历史条目同一套口径。
/// </summary>
internal static class ArpabetIpaConverter
{
    private static readonly Dictionary<string, (string Symbol, bool IsVowel)> PhoneToIpa = new(StringComparer.Ordinal)
    {
        // 元音：双元音与长元音直接记到位（iː/uː/ɑː/ɔː/ɜːr），重读 AH 另按 STRUT 记 ʌ
        ["AA"] = ("ɑː", true), ["AE"] = ("æ", true), ["AH"] = ("ə", true), ["AO"] = ("ɔː", true),
        ["AW"] = ("aʊ", true), ["AY"] = ("aɪ", true), ["EH"] = ("e", true), ["ER"] = ("ɜːr", true),
        ["EY"] = ("eɪ", true), ["IH"] = ("ɪ", true), ["IY"] = ("iː", true), ["OW"] = ("oʊ", true),
        ["OY"] = ("ɔɪ", true), ["UH"] = ("ʊ", true), ["UW"] = ("uː", true),
        ["AX"] = ("ə", true), ["IX"] = ("ə", true), ["UX"] = ("ʊ", true),
        // 辅音
        ["B"] = ("b", false), ["CH"] = ("tʃ", false), ["D"] = ("d", false), ["DH"] = ("ð", false), ["DX"] = ("ɾ", false),
        ["F"] = ("f", false), ["G"] = ("ɡ", false), ["HH"] = ("h", false), ["JH"] = ("dʒ", false),
        ["K"] = ("k", false), ["L"] = ("l", false), ["M"] = ("m", false), ["N"] = ("n", false), ["NG"] = ("ŋ", false),
        ["P"] = ("p", false), ["Q"] = ("ʔ", false), ["R"] = ("r", false), ["S"] = ("s", false), ["SH"] = ("ʃ", false),
        ["T"] = ("t", false), ["TH"] = ("θ", false), ["V"] = ("v", false), ["W"] = ("w", false),
        ["Y"] = ("j", false), ["Z"] = ("z", false), ["ZH"] = ("ʒ", false)
    };

    /// <summary>把 "Y UW0 B IH1 K W IH0 T AH0 S" 转成 "juː.ˈbɪk.wɪ.təs"。</summary>
    public static string Convert(string arpabet)
    {
        var phones = new List<IpaPhone>();
        foreach (var raw in arpabet.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var digit = raw[^1] - '0';
            var hasStress = digit is >= 0 and <= 2;
            var phone = hasStress ? raw[..^1] : raw; // 无重音位的条目（极少数语气词）按无重音处理
            if (!PhoneToIpa.TryGetValue(phone, out var entry))
            {
                continue;
            }

            var stress = hasStress ? digit : 0;
            var symbol = entry.Symbol;
            if (phone == "AH" && stress is 1 or 2)
            {
                symbol = "ʌ"; // 美音 STRUT：重读 AH 记 ʌ，非重读 AH/AX/IX 一律 ə
            }

            phones.Add(new IpaPhone(symbol, entry.IsVowel, entry.IsVowel ? stress : 0));
        }

        return IpaTranscription.Format(phones);
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
///   中文：pypinyin 词组/单字 + CC-CEDICT + Unihan → 进程内拼音数据 zh_pinyin.txt.gz（见 <see cref="ChinesePinyinDataBuilder"/>，无需 mecab-dict-index）；
///   日语：直接采用 IPAdic 编译态二进制（无需编译，见 codestable/notes/phonetics-pipeline.md）。
/// 生成后按控制台输出的 SHA-256 回填 PhoneticDependencyManifest（哈希即版本）。
/// 单独重建中文数据可用 --build-zh-data（只依赖 resources/phonetics 源料）。
/// </summary>
internal static class PhoneticDictionaryBuilder
{
    internal static int Run(string repoRoot, string? dictIndexPath)
    {
        var sourceDirectory = Path.Combine(repoRoot, "deps", "phonetics", "_source");
        BuildEnglishDatabase(sourceDirectory, repoRoot);
        ChinesePinyinDataBuilder.Build(repoRoot);
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
                foreach (var entry in CmuDictionarySourceParser.Parse(File.ReadLines(cmudictPath)))
                {
                    // P0 先保留解析层的变体编号，但生产单读音数据库仍只生成默认项；
                    // 候选存储与正式哈希迁移由英语候选 issue 单独推进，避免半成品覆盖现有产物。
                    if (!entry.IsDefault)
                    {
                        continue;
                    }

                    var ipa = ArpabetIpaConverter.Convert(entry.Arpabet);
                    if (ipa.Length == 0)
                    {
                        continue;
                    }

                    wordParameter.Value = entry.Word;
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

    private static void Guard(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
