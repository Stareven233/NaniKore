using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

internal sealed class GeneralPage : ISettingsPage
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

    private readonly Panel _view = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly CheckBox _autoTranslate = new() { Text = "OCR 后自动翻译", AutoSize = true };
    // 目标语言会直接写入模型提示词，使用自然语言名称比让主人记忆地区代码更直观。
    private readonly ComboBox _targetLanguage = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _pipelineMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };

    public GeneralPage()
    {
        _targetLanguage.Items.AddRange(TargetLanguages);
        _pipelineMode.Items.AddRange(["local_ocr", "vision_oneshot"]);

        var targetLabel = new Label { Text = "翻译目标语言：", AutoSize = true, Top = 16, Left = 16 };
        _targetLanguage.SetBounds(16, 42, 300, 28);
        var pipelineLabel = new Label { Text = "翻译流程：", AutoSize = true, Top = 82, Left = 16 };
        _pipelineMode.SetBounds(16, 108, 300, 28);
        _autoTranslate.SetBounds(16, 152, 260, 28);
        _view.Controls.AddRange([targetLabel, _targetLanguage, pipelineLabel, _pipelineMode, _autoTranslate]);
    }

    public string Title => "通用";

    public Control View => _view;

    public void Load(AppConfig config)
    {
        _autoTranslate.Checked = config.General.AutoTranslate;
        _targetLanguage.SelectedItem = FindLanguage(config.General.TargetLanguage) ?? TargetLanguages[0];
        _pipelineMode.SelectedItem = config.Pipeline.Mode;
        if (_pipelineMode.SelectedIndex < 0)
        {
            _pipelineMode.SelectedItem = "local_ocr";
        }
    }

    public string? Validate() => _targetLanguage.SelectedItem is LanguageChoice
        ? null
        : "请选择翻译目标语言。";

    public void Apply(AppConfig config)
    {
        config.General.AutoTranslate = _autoTranslate.Checked;
        config.General.TargetLanguage = ((LanguageChoice)_targetLanguage.SelectedItem!).Name;
        config.Pipeline.Mode = _pipelineMode.SelectedItem as string ?? "local_ocr";
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
