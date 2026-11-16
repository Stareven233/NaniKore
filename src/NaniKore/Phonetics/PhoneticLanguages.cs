namespace NaniKore.Phonetics;

/// <summary>
/// 注音语言目录：语言代码与展示名的唯一定义处。代码被拼音数据清单、原生包身份清单、
/// 设置页语言包卡片与源语言下拉共用；展示名服务设置页与结果面板的「源语言」下拉。
/// 本轮只装配 ja / en / zh（简体）；新语言的接入路线见 codestable/talks/005。
/// </summary>
internal static class PhoneticLanguages
{
    public const string JapaneseCode = "ja";
    public const string EnglishCode = "en";
    public const string ChineseCode = "zh";

    public static IReadOnlyList<PhoneticLanguage> All { get; } =
    [
        new(JapaneseCode, "日本語"),
        new(EnglishCode, "English"),
        new(ChineseCode, "中文（简体）"),
    ];

    /// <summary>按代码取展示名；未装配的语言返回 null（调用方按「不注音」处理）。</summary>
    public static string? DisplayName(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName;
}

/// <summary>注音语言的描述：代码 + 展示名。</summary>
internal sealed record PhoneticLanguage(string Code, string DisplayName);
