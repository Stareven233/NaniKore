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
            // 图像或空文本场景下，若目标语言非日语，依然推荐准备日语注音插件以供模型兜底
            var defaultToJapanese = !IsJapaneseTarget(targetLanguage);
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
        else if (hasCjk && !hasSimplifiedOnly && !IsJapaneseTarget(targetLanguage))
        {
            // 未命中简体字，且目标语言非日语：
            // 判定为纯汉字日文（如「進入禁止」「案内図」「攻撃力」「取扱説明書」）
            // 无论目标语言是中文还是英文/其他非日语，均认定为日语并需要振假名注音
            features |= LanguageFeatures.Japanese;
            needsJapaneseRuby = true;
            isPureHanziJapaneseCandidate = true;
        }

        // 简体独占字特征独立于假名置位：混排句（如「这是一个iPhoneの発表会」）同样是简体中文，
        // 中文拼音插件的门控据此纳入拼音规约，与振假名 / IPA 同享；日文汉字不含简体独占字，无误伤。
        if (hasSimplifiedOnly)
        {
            features |= LanguageFeatures.SimplifiedChinese;
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
    /// 判断目标语言是否为英语。用于插件门控的目标语言互斥：目标即英语时不再注入英语 IPA 插件，
    /// 避免日译英等场景给源文本中本就属于目标语言的英文（含商标、缩写、专名）乱注音标。
    /// 匹配展示名「英语」、英文名「English」与语言代码「en」三种形态，与 <see cref="IsJapaneseToChinesePreference"/> 同款宽松口径。
    /// 目标语言取自设置页固定选项的闭集，闭集内仅英语名含 "en" 子串，不至误伤其他语言。
    /// </summary>
    public static bool IsEnglishTarget(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return false;
        }

        return targetLanguage.Contains("英语", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("English", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("en", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断目标语言是否为日语。用于插件门控的目标语言互斥：目标即日语时不再注入日语振假名插件，
    /// 避免英/中译日场景把源文本里引述的假名或汉字按日文汉字标注假名。
    /// 匹配展示名「日语」、日文名「日本語」与语言代码「ja」三种形态，同款宽松口径；闭集内仅日语名含 "ja" 子串。
    /// </summary>
    public static bool IsJapaneseTarget(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return false;
        }

        return targetLanguage.Contains("日语", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("日本語", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("ja", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断目标语言是否为中文。用于插件门控的目标语言互斥：目标即中文时不再注入中文拼音插件，
    /// 避免外译中场景给源文本中本就属于目标语言的汉字标注拼音。
    /// 匹配展示名「中文」、英文名「Chinese」与语言代码「zh」三种形态，同款宽松口径；闭集内仅中文名含 "zh" 子串。
    /// 与 <see cref="IsJapaneseToChinesePreference"/> 的差别仅在空值口径：后者是探测启发式的偏好（空值按日译中处理），
    /// 本谓词是互斥剪枝判定（空值不剪枝），与 <see cref="IsEnglishTarget"/>/<see cref="IsJapaneseTarget"/> 一致。
    /// </summary>
    public static bool IsChineseTarget(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return false;
        }

        return targetLanguage.Contains("中文", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("Chinese", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("zh", StringComparison.OrdinalIgnoreCase);
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
