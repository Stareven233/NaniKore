namespace NaniKore.Phonetics;

/// <summary>
/// 受支持注音语言的描述：代码 + 展示名。展示名同时服务于设置页词典卡片与
/// 结果面板的「源语言」下拉（issue 008 决策 4），两处引用同一份定义避免口径漂移。
/// </summary>
internal sealed record PhoneticLanguage(string Code, string DisplayName);

/// <summary>
/// 初版三语清单（issue 008 决策 1）：ja / en / zh（简体）。ko（韩语）与繁体中文等
/// 待后续以新词典 + 新标注器接入，此处按序追加即可，引擎与分发链路零改动。
/// </summary>
internal static class PhoneticLanguages
{
    public static IReadOnlyList<PhoneticLanguage> All { get; } =
    [
        new PhoneticLanguage(PhoneticDependencyManifest.JapaneseCode, "日本語"),
        new PhoneticLanguage(PhoneticDependencyManifest.EnglishCode, "English"),
        new PhoneticLanguage(PhoneticDependencyManifest.ChineseCode, "中文（简体）"),
    ];

    /// <summary>按代码取展示名；未受支持的语言返回 null（调用方按「不注音」处理）。</summary>
    public static string? DisplayName(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName;
}
