using System.Windows;
using System.Windows.Controls;
using NaniKore.Config;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class GeneralPage : UserControl, ISettingsPage
{
    private static readonly LanguageChoice[] TargetLanguages =
    [
        new("中文（简体）", "zh-CN", "中文", "简体中文", "Chinese", "Chinese (Simplified)"),
        new("中文（繁体）", "zh-TW", "繁體中文", "Chinese (Traditional)"),
        new("英语", "en", "English"),
        new("日语", "ja", "日本語", "Japanese"),
        new("韩语", "ko", "한국어", "Korean"),
        new("俄语", "ru", "Русский", "Russian"),
        new("法语", "fr", "Français", "French"),
        new("德语", "de", "Deutsch", "German"),
        new("西班牙语", "es", "Español", "Spanish"),
        new("葡萄牙语", "pt", "Português", "Portuguese"),
        new("意大利语", "it", "Italiano", "Italian"),
    ];

    public GeneralPage()
    {
        InitializeComponent();
        foreach (var language in TargetLanguages)
        {
            TargetLanguageBox.Items.Add(language);
        }

        PipelineModeBox.Items.Add("local_ocr");
        PipelineModeBox.Items.Add("vision_oneshot");
    }

    public string Title => "通用";

    public string Glyph => "\uE713";

    public FrameworkElement View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AutoTranslateToggle.IsChecked = config.General.AutoTranslate;
        TargetLanguageBox.SelectedItem = FindLanguage(config.General.TargetLanguage) ?? TargetLanguages[0];
        PipelineModeBox.SelectedItem = config.Pipeline.Mode;
        if (PipelineModeBox.SelectedIndex < 0)
        {
            PipelineModeBox.SelectedItem = "local_ocr";
        }
    }

    public string? Validate() => TargetLanguageBox.SelectedItem is LanguageChoice
        ? null
        : "请选择翻译目标语言。";

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.General.AutoTranslate = AutoTranslateToggle.IsChecked == true;
        config.General.TargetLanguage = ((LanguageChoice)TargetLanguageBox.SelectedItem!).Name;
        config.Pipeline.Mode = PipelineModeBox.SelectedItem as string ?? "local_ocr";
    }

    private static LanguageChoice? FindLanguage(string configuredValue) => TargetLanguages.FirstOrDefault(choice =>
        choice.Matches(configuredValue));

    /// <summary>保留旧版语言代码兼容；保存后统一改用适合提示词与设置页展示的自然语言名称。</summary>
    private sealed class LanguageChoice(string name, params string[] aliases)
    {
        public string Name { get; } = name;

        public bool Matches(string value) => aliases.Append(Name).Any(alias =>
            string.Equals(alias, value, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Name;
    }
}
