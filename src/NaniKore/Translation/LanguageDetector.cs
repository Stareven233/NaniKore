using System.Buffers;

namespace NaniKore.Translation;

/// <summary>
/// 探测到的语言特征位标志。
/// </summary>
[Flags]
internal enum LanguageFeatures
{
    None = 0,
    Japanese = 1 << 0,
    English = 1 << 1,
    SimplifiedChinese = 1 << 2,
}

/// <summary>
/// 字符集与语言探测结果。
/// </summary>
internal sealed record LanguageDetectionResult(
    LanguageFeatures Features,
    bool NeedsJapaneseRuby,
    bool NeedsEnglishIpa,
    bool IsPureHanziJapaneseCandidate)
{
    public bool HasJapanese => (Features & LanguageFeatures.Japanese) != 0;
    public bool HasEnglish => (Features & LanguageFeatures.English) != 0;
    public bool HasSimplifiedChinese => (Features & LanguageFeatures.SimplifiedChinese) != 0;
}

/// <summary>
/// 客户端极速字符集探测器。
/// 基于纯内存字符扫描与启发式简体字排除算法，实现 &lt;1ms 的语言特征探测。
/// 核心解决「進入禁止」「案内図」「攻撃力」等纯汉字日文词汇在无假名时的注音识别问题。
/// </summary>
internal static class LanguageDetector
{
    // 超高频且绝对不在日本常用汉字表（Jōyō Kanji）及新字体中的简体中文独占汉字。
    // 涵盖代词、虚词、常见动词与常用独立简化字。
    private const string ExplicitSimplifiedChars =
        "这们个时经让从说为长头书电动两单变总质现专网报还运远选边处较办怎样哪谁什么进关开问题风鸟鱼车马门贝页" +
        "场买卖发实热类树层广导岁农军战戏击护挥损摆搞搬难爱乐帮带气标极";

    private static readonly SearchValues<char> SimplifiedSearchValues =
        SearchValues.Create(ExplicitSimplifiedChars);

    /// <summary>
    /// 分析纯文本的语言特征与注音需求。
    /// </summary>
    /// <param name="text">待探测的纯文本（如 OCR 识别结果）。</param>
    /// <param name="targetLanguage">当前配置的目标语言（默认为日译中偏好）。</param>
    /// <returns>探测结果，包含命中的特征位与注音插件推荐。</returns>
    public static LanguageDetectionResult Detect(string? text, string? targetLanguage = "中文（简体）")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // 图像或空文本场景下，若偏好为日译中，依然推荐准备日语注音插件以供模型兜底
            var defaultToJapanese = IsJapaneseToChinesePreference(targetLanguage);
            return new LanguageDetectionResult(
                defaultToJapanese ? LanguageFeatures.Japanese : LanguageFeatures.None,
                NeedsJapaneseRuby: defaultToJapanese,
                NeedsEnglishIpa: false,
                IsPureHanziJapaneseCandidate: false);
        }

        var span = text.AsSpan();
        var hasKana = false;
        var hasLatin = false;
        var hasCjk = false;
        var hasSimplifiedOnly = false;

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];

            // 1. 平假名 (\u3040-\u309F) 与片假名 (\u30A0-\u30FF, \u31F0-\u31FF)
            if (IsKana(ch))
            {
                hasKana = true;
                continue;
            }

            // 2. 拉丁字母 (A-Za-z)
            if (IsLatinLetter(ch))
            {
                hasLatin = true;
                continue;
            }

            // 3. CJK 统一汉字区间 (\u4E00-\u9FFF 及扩展 A 区 \u3400-\u4DBF)
            if (IsCjkIdeograph(ch))
            {
                hasCjk = true;

                // 检查是否为简体中文独占字（部首或独立字）
                if (IsSimplifiedOnly(ch))
                {
                    hasSimplifiedOnly = true;
                }
            }
        }

        var features = LanguageFeatures.None;
        var needsJapaneseRuby = false;
        var isPureHanziJapaneseCandidate = false;

        if (hasKana)
        {
            // 包含假名，确凿属于日语
            features |= LanguageFeatures.Japanese;
            needsJapaneseRuby = true;
        }
        else if (hasCjk)
        {
            // 无假名但包含汉字：进行启发式排查
            if (hasSimplifiedOnly)
            {
                // 命中简体中文独占字，判定为纯中文，不注入假名注音
                features |= LanguageFeatures.SimplifiedChinese;
            }
            else if (IsJapaneseToChinesePreference(targetLanguage))
            {
                // 未命中简体字，且用户处于日译中偏好：
                // 判定为纯汉字日文（如「進入禁止」「案内図」「攻撃力」「取扱説明書」）
                features |= LanguageFeatures.Japanese;
                needsJapaneseRuby = true;
                isPureHanziJapaneseCandidate = true;
            }
        }

        if (hasLatin)
        {
            features |= LanguageFeatures.English;
        }

        var needsEnglishIpa = hasLatin && !hasKana && !hasCjk;

        return new LanguageDetectionResult(
            features,
            NeedsJapaneseRuby: needsJapaneseRuby,
            NeedsEnglishIpa: needsEnglishIpa,
            IsPureHanziJapaneseCandidate: isPureHanziJapaneseCandidate);
    }

    /// <summary>
    /// 判断是否处于日译中或通用汉字偏好。
    /// </summary>
    public static bool IsJapaneseToChinesePreference(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return true;
        }

        // 默认目标语言为中文时，默认输入假定为外语（日文为主）
        return targetLanguage.Contains("中文", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("zh", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("Chinese", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断字符是否为日文平假名或片假名。
    /// </summary>
    private static bool IsKana(char ch) =>
        (ch >= '\u3040' && ch <= '\u309F') || // 平假名 (Hiragana)
        (ch >= '\u30A0' && ch <= '\u30FF') || // 片假名 (Katakana)
        (ch >= '\u31F0' && ch <= '\u31FF');   // 片假名拼音扩展

    /// <summary>
    /// 判断字符是否为拉丁字母。
    /// </summary>
    private static bool IsLatinLetter(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    /// <summary>
    /// 判断字符是否在 CJK 统一表意文字主要区间内。
    /// </summary>
    private static bool IsCjkIdeograph(char ch) =>
        (ch >= '\u4E00' && ch <= '\u9FFF') ||
        (ch >= '\u3400' && ch <= '\u4DBF');

    /// <summary>
    /// 启发式排查是否为简体中文独占字符。
    /// 利用 Unicode CJK 统一字表中仅中国大陆采用的简体部首字块，以及高频独占字查找表。
    /// </summary>
    private static bool IsSimplifiedOnly(char ch)
    {
        // 1. Unicode 中中国大陆专用的简化字连续编码区块（日语新字体绝对无此部首）
        if (ch >= '\u8BA0' && ch <= '\u8C02') return true; // 偏旁“讠”全部字符（言部简化）
        if (ch >= '\u7E9F' && ch <= '\u7F35') return true; // 偏旁“纟”全部字符（糸部简化）
        if (ch >= '\u9485' && ch <= '\u9576') return true; // 偏旁“钅”全部字符（金部简化）
        if (ch >= '\u95E8' && ch <= '\u960F') return true; // 偏旁“门”全部字符（門部简化）
        if (ch >= '\u9A6C' && ch <= '\u9AB8') return true; // 偏旁“马”全部字符（馬部简化）
        if (ch >= '\u8F66' && ch <= '\u8F9B') return true; // 偏旁“车”全部字符（車部简化）
        if (ch >= '\u8D1D' && ch <= '\u8D63') return true; // 偏旁“贝”全部字符（貝部简化）
        if (ch >= '\u9875' && ch <= '\u989C') return true; // 偏旁“页”全部字符（頁部简化）
        if (ch >= '\u98CE' && ch <= '\u98DF') return true; // 偏旁“风”全部字符（風部简化）
        if (ch >= '\u9E1F' && ch <= '\u9E58') return true; // 偏旁“鸟”全部字符（鳥部简化）

        // 2. 无法仅通过上述部首连续区间覆盖的常用高频简体独占字
        return SimplifiedSearchValues.Contains(ch);
    }
}
