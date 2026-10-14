using NaniKore.Translation;

namespace NaniKore.App.Controls;

/// <summary>
/// 注音种类：决定控件与提示里的「假名 / 拼音 / 音标 / 读音」措辞。
/// 按注音文本自身判定而不是按语种下发：混排句子里每个词元都能说对话（日语句子里的英语词说「音标」）。
/// </summary>
internal enum RubyReadingKind
{
    /// <summary>日语振假名。</summary>
    Kana,

    /// <summary>简体中文拼音（带声调符号）。</summary>
    Pinyin,

    /// <summary>英语国际音标。</summary>
    Ipa,

    /// <summary>判不出（空读音或混合形态）：用中性的「读音」，不冤枉任何一种语言。</summary>
    Unknown,
}

/// <summary>注音种类的判定与措辞表。</summary>
internal static class RubyReadingTerms
{
    /// <summary>IPA 特征字符：重音号、长音符与英语特有元音/辅音（ASCII 字母不算——拼音同样是 ASCII）。</summary>
    private static readonly HashSet<char> IpaMarkers =
        ['ˈ', 'ˌ', 'ː', 'æ', 'ɑ', 'ɔ', 'ə', 'ɜ', 'ɪ', 'ʊ', 'ʌ', 'θ', 'ð', 'ʃ', 'ʒ', 'ŋ', 'ɡ', 'ɾ', 'ʔ'];

    /// <summary>拼音声调符号（含 ü 四声与 ńňǹ）。</summary>
    private static readonly HashSet<char> PinyinTones =
        ['ā', 'á', 'ǎ', 'à', 'ē', 'é', 'ě', 'è', 'ī', 'í', 'ǐ', 'ì', 'ō', 'ó', 'ǒ', 'ò',
         'ū', 'ú', 'ǔ', 'ù', 'ǖ', 'ǘ', 'ǚ', 'ǜ', 'ń', 'ň', 'ǹ'];

    /// <summary>读音名词：假名 / 拼音 / 音标 / 读音。</summary>
    public static string Reading(RubyReadingKind kind) => kind switch
    {
        RubyReadingKind.Kana => "假名",
        RubyReadingKind.Pinyin => "拼音",
        RubyReadingKind.Ipa => "音标",
        _ => "读音"
    };

    /// <summary>底字名词：英语是「单词」，日语/中文是「汉字」。</summary>
    public static string Base(RubyReadingKind kind) => kind == RubyReadingKind.Ipa ? "单词" : "汉字";

    /// <summary>按注音文本判定种类。</summary>
    public static RubyReadingKind Detect(string? rubyText)
    {
        if (string.IsNullOrWhiteSpace(rubyText))
        {
            return RubyReadingKind.Unknown;
        }

        if (rubyText.Any(IpaMarkers.Contains))
        {
            return RubyReadingKind.Ipa;
        }

        if (rubyText.Any(PinyinTones.Contains))
        {
            return RubyReadingKind.Pinyin;
        }

        // 纯假名（含长音符、中黑与空白）才算振假名；其余形态（如无声调拼音）退回中性「读音」
        return rubyText.All(ch => RubyTextParser.IsKana(ch) || ch is 'ー' or '・' or ' ' or '\u3000')
            ? RubyReadingKind.Kana
            : RubyReadingKind.Unknown;
    }
}
