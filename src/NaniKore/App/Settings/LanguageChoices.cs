using NaniKore.Translation;

namespace NaniKore.App.Settings;

/// <summary>
/// 设置页目标语言、收藏 Tab 语言筛选、划词气泡语言下拉共用的唯一语言选项列表。
/// 主人拍板（issue 015 / 2.3）：三处永不漂移——「设置页可选目标语言 ≡ 收藏语言选项 ≡ 气泡语言下拉」。
/// 展示名统一采用各语言母语写法；别名集保留中文习惯名与语言代码，
/// 旧配置值（如「日语」「中文（简体）」「ja」）经 <see cref="LanguageChoice.Matches"/> 仍可命中，无需配置迁移。
/// </summary>
internal static class LanguageChoices
{
    /// <summary>全部共享语言选项（母语写法），列表扩充时改这一处即可。</summary>
    public static IReadOnlyList<LanguageChoice> All { get; } = new LanguageChoice[]
    {
        new("中文（简体）", "zh-CN", "中文", "简体中文", "Chinese", "Chinese (Simplified)"),
        new("繁體中文", "zh-TW", "中文（繁体）", "Chinese (Traditional)"),
        new("English", "en", "英语", "English"),
        new("日本語", "ja", "日语", "Japanese"),
        new("한국어", "ko", "韩语", "Korean"),
        new("Русский", "ru", "俄语", "Russian"),
        new("Français", "fr", "法语", "French"),
        new("Deutsch", "de", "德语", "German"),
        new("Español", "es", "西班牙语", "Spanish"),
        new("Português", "pt", "葡萄牙语", "Portuguese"),
        new("Italiano", "it", "意大利语", "Italian"),
    };

    /// <summary>按配置/筛选值（中文名、母语名或语言代码）在共享列表中查找对应选项。</summary>
    public static LanguageChoice? Find(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return All.FirstOrDefault(choice => choice.Matches(value));
    }

    /// <summary>
    /// 把 <see cref="LanguageDetector"/> 的本轮探测结果映射回共享列表的展示名（不是三档硬编码）。
    /// 规则：含假名或纯汉字日语 → 日本語；命中简体独占字 → 中文（简体）；仅拉丁字母 → English；
    /// 均不命中时回落 日本語（与探测器对空文本默认推荐日语注音一致）。
    /// </summary>
    public static string FromDetection(LanguageDetectionResult detection)
    {
        if (detection.HasJapanese)
        {
            return "日本語";
        }

        if (detection.HasSimplifiedChinese)
        {
            return "中文（简体）";
        }

        if (detection.HasEnglish)
        {
            return "English";
        }

        return "日本語";
    }
}

/// <summary>
/// 单个语言选项：展示名为母语写法，别名集涵盖中文习惯名、英文名与语言代码。
/// </summary>
internal sealed class LanguageChoice(string name, params string[] aliases)
{
    public string Name { get; } = name;

    /// <summary>宽松匹配：别名与展示名任意命中即算（忽略大小写）。</summary>
    public bool Matches(string value) => aliases.Append(Name).Any(alias =>
        string.Equals(alias, value, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}
