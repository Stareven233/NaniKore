using System.Text;

namespace NaniKore.Phonetics;

/// <summary>
/// 单个 IPA 音素：符号 + 是否音节核（元音/双元音）+ 重音位（0 无 / 1 主重音 / 2 次重音）。
/// 音节核是切分锚点；重音位只挂核（ARPAbet 的重音位本就标在元音后，历史 wire format 的 ˈ/ˌ 也紧贴元音）。
/// </summary>
internal readonly record struct IpaPhone(string Symbol, bool IsVowel, int Stress);

/// <summary>
/// 词典式 IPA 的唯一规范化实现：音节切分、重音归位到音节首、长音符与 ʌ/ə 补全。
/// 两侧共用同一套格式逻辑：
///  - 生成期 <see cref="ArpabetIpaConverter"/>：CMUdict ARPAbet → 音素序列 → <see cref="Format"/>；
///  - 运行期 <see cref="EnglishIpaDictionary"/>：库里历史 wire format（重音贴在元音前、无音节点、IY 不带 ː）
///    读出来先过 <see cref="Normalize"/> 再交给界面。
/// 运行期必须再归一的原因：ipa_dict.db 是「哈希即版本」的分布式产物，已发布词条无法随代码一起重生，
/// 只能读时规范化；对按新口径生成的条目，本转换是幂等的。
/// </summary>
internal static class IpaTranscription
{
    /// <summary>
    /// 英语起首辅音簇白名单（最大 onset 原则用）：
    /// 双辅音 = 阻塞音/摩擦音 + 流音或滑音、s + 阻塞音/鼻音/摩擦音；三辅音 = s + 阻塞音 + 流音滑音。
    /// </summary>
    private static readonly HashSet<string> OnsetClusters = new(StringComparer.Ordinal)
    {
        "pr", "br", "tr", "dr", "kr", "ɡr", "fr", "θr", "ʃr",
        "pl", "bl", "kl", "ɡl", "fl", "sl",
        "tw", "kw", "dw", "ɡw", "sw", "θw",
        "hj", "vj", "pj", "bj", "fj", "kj", "ɡj", "mj", "nj", "sj", "tj",
        "sp", "st", "sk", "sf", "sm", "sn", "sv",
        "spl", "spr", "str", "skr", "skw", "skj", "stj", "spj"
    };

    /// <summary>不能独立充当起首的辅音：ŋ 只出现在韵尾，ʔ/ɾ 非英语音位。</summary>
    private static readonly HashSet<string> NonOnsetConsonants = new(StringComparer.Ordinal) { "ŋ", "ʔ", "ɾ" };

    /// <summary>短/松元音：重读时其后辅音倾向收进前一音节韵尾（sister /ˈsɪs.tɜːr/、apple /ˈæp.əl/）。</summary>
    private static readonly HashSet<string> LaxVowels = new(StringComparer.Ordinal) { "æ", "e", "ɪ", "ʊ", "ʌ", "ə" };

    /// <summary>英语起首辅音簇的最大长度（spl / str / skw 之类）。</summary>
    private const int MaxOnsetLength = 3;

    /// <summary>
    /// IPA 符号表（含多码位符号），必须按符号长度降序书写：解析时贪婪匹配最长符号，
    /// 保证 eɪ 先于 e、tʃ 先于 t、iː 先于 i 命中。
    /// </summary>
    private static readonly (string Symbol, bool IsVowel)[] SymbolTable =
    [
        ("ɜːr", true),
        ("ɑː", true), ("ɔː", true), ("iː", true), ("uː", true),
        ("aʊ", true), ("aɪ", true), ("eɪ", true), ("ɔɪ", true), ("oʊ", true),
        ("tʃ", false), ("dʒ", false),
        ("æ", true), ("e", true), ("ɪ", true), ("ʊ", true), ("ʌ", true), ("ə", true), ("i", true), ("u", true),
        ("b", false), ("d", false), ("f", false), ("ɡ", false), ("h", false), ("j", false),
        ("k", false), ("l", false), ("m", false), ("n", false), ("ŋ", false), ("p", false),
        ("r", false), ("s", false), ("ʃ", false), ("t", false), ("θ", false), ("v", false),
        ("w", false), ("z", false), ("ʒ", false), ("ð", false), ("ɾ", false), ("ʔ", false)
    ];

    /// <summary>词典里读出的历史 wire format → 词典式 IPA；对新口径生成的条目幂等。</summary>
    public static string Normalize(string stored) => Format(Parse(stored));

    /// <summary>IPA 文本 → 音素序列；音节点、连字符与空白视为噪点丢弃（音节切分由算法重算）。</summary>
    public static IReadOnlyList<IpaPhone> Parse(string ipa)
    {
        var phones = new List<IpaPhone>();
        var pendingStress = 0;
        var index = 0;
        while (index < ipa.Length)
        {
            var ch = ipa[index];
            if (ch == 'ˈ')
            {
                pendingStress = 1;
                index++;
                continue;
            }

            if (ch == 'ˌ')
            {
                pendingStress = 2;
                index++;
                continue;
            }

            if (ch is '.' or '-' or ' ' or '\u3000')
            {
                index++;
                continue;
            }

            var (symbol, isVowel, length) = MatchSymbol(ipa, index);
            if (length == 0)
            {
                index++; // 未知字符：条目由自家管线生成，理论不可达；丢弃保稳，绝不让注音链路炸掉
                continue;
            }

            if (isVowel)
            {
                // 重音位可能落在辅音上（已规范化的条目把 ˈ 放在音节首），此时继续向后捎到该音节的元音核
                phones.Add(new IpaPhone(CanonicalSymbol(symbol, pendingStress), true, pendingStress));
                pendingStress = 0;
            }
            else
            {
                phones.Add(new IpaPhone(symbol, false, 0));
            }

            index += length;
        }

        return phones;
    }

    /// <summary>音素序列 → 词典式 IPA：音节切分 + 重音位移到音节首 + 音节点。</summary>
    public static string Format(IReadOnlyList<IpaPhone> phones)
    {
        if (phones.Count == 0)
        {
            return string.Empty;
        }

        var starts = SyllableStarts(phones);
        var builder = new StringBuilder(phones.Count * 3 + starts.Count);
        for (var syllable = 0; syllable < starts.Count; syllable++)
        {
            if (syllable > 0)
            {
                builder.Append('.');
            }

            var from = starts[syllable];
            var to = syllable + 1 < starts.Count ? starts[syllable + 1] : phones.Count;
            var stress = 0;
            for (var i = from; i < to; i++)
            {
                if (phones[i].Stress > 0)
                {
                    stress = phones[i].Stress;
                    break;
                }
            }

            if (stress == 1)
            {
                builder.Append('ˈ');
            }
            else if (stress == 2)
            {
                builder.Append('ˌ');
            }

            for (var i = from; i < to; i++)
            {
                builder.Append(phones[i].Symbol);
            }
        }

        return builder.ToString();
    }

    /// <summary>音节起始下标序列（末音节一直延伸到音素末尾）；无元音核时整体算一个音节。</summary>
    private static List<int> SyllableStarts(IReadOnlyList<IpaPhone> phones)
    {
        var nuclei = new List<int>();
        for (var index = 0; index < phones.Count; index++)
        {
            if (phones[index].IsVowel)
            {
                nuclei.Add(index);
            }
        }

        if (nuclei.Count == 0)
        {
            return [0];
        }

        var starts = new List<int>(nuclei.Count) { 0 };
        for (var n = 0; n + 1 < nuclei.Count; n++)
        {
            var nucleus = nuclei[n];
            starts.Add(nucleus + 1 + CodaCount(phones, nucleus + 1, nuclei[n + 1], phones[nucleus]));
        }

        return starts;
    }

    /// <summary>
    /// 相邻两个音节核之间的辅音串里，归前一音节韵尾的个数（其余归后一音节起首）。
    /// 口径取词典式 IPA 的通行做法：
    ///  - 重读短元音后首辅音收尾（sister /ˈsɪs.tɜːr/、apple /ˈæp.əl/、banana /bəˈnæn.ə/）；
    ///  - 其余（长元音/双元音或非重读音节后）按最大 onset 原则尽量多划给后一音节
    ///    （treason /ˈtriː.zən/、photograph /ˈfoʊ.tə.ɡræf/），但起首簇必须合法，不合法就逐个回退。
    /// </summary>
    private static int CodaCount(IReadOnlyList<IpaPhone> phones, int from, int to, IpaPhone nucleus)
    {
        var count = to - from;
        if (count == 0)
        {
            return 0;
        }

        if (nucleus.Stress > 0 && LaxVowels.Contains(nucleus.Symbol))
        {
            // 短元音后首辅音收尾；但若剩余起辅音非法（如 ŋ），整串都留作韵尾
            return count > 1 && !IsOnsetConsonant(phones[to - 1].Symbol) ? count : 1;
        }

        for (var onset = Math.Min(count, MaxOnsetLength); onset >= 2; onset--)
        {
            if (IsOnsetCluster(phones, to - onset, to))
            {
                return count - onset;
            }
        }

        return IsOnsetConsonant(phones[to - 1].Symbol) ? count - 1 : count;
    }

    /// <summary>单个辅音能否充当起首。</summary>
    private static bool IsOnsetConsonant(string symbol) => !NonOnsetConsonants.Contains(symbol);

    /// <summary>辅音簇能否充当英语起首：单辅音查非法表，多辅音查白名单。</summary>
    private static bool IsOnsetCluster(IReadOnlyList<IpaPhone> phones, int from, int to)
    {
        if (to - from == 1)
        {
            return IsOnsetConsonant(phones[from].Symbol);
        }

        var builder = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            builder.Append(phones[i].Symbol);
        }

        return OnsetClusters.Contains(builder.ToString());
    }

    /// <summary>历史 wire format 的符号归一：IY/UW 补长音符；重读的 AH（ə）按 STRUT 记 ʌ。</summary>
    private static string CanonicalSymbol(string symbol, int stress) => symbol switch
    {
        "i" => "iː",
        "u" => "uː",
        "ə" when stress > 0 => "ʌ",
        _ => symbol
    };

    /// <summary>在 index 处贪婪匹配最长符号；未命中返回长度 0。</summary>
    private static (string Symbol, bool IsVowel, int Length) MatchSymbol(string text, int index)
    {
        foreach (var (symbol, isVowel) in SymbolTable)
        {
            if (index + symbol.Length <= text.Length && text.AsSpan(index, symbol.Length).SequenceEqual(symbol))
            {
                return (symbol, isVowel, symbol.Length);
            }
        }

        return (string.Empty, false, 0);
    }
}
