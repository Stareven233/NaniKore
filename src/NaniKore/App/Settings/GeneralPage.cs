using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

internal sealed class GeneralPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly CheckBox _autoTranslate = new() { Text = "OCR 后自动翻译", AutoSize = true };
    private readonly TextBox _targetLanguage = new() { Width = 260 };
    private readonly ComboBox _pipelineMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    public GeneralPage()
    {
        var label = new Label { Text = "目标语言（例如 zh-CN、English）：", AutoSize = true, Top = 16, Left = 16 };
        _targetLanguage.SetBounds(16, 42, 300, 28);
        _autoTranslate.SetBounds(16, 86, 260, 28);
        _view.Controls.AddRange([label, _targetLanguage, _autoTranslate]);
    }

    public string Title => "通用";

    public Control View => _view;

    public void Load(AppConfig config)
    {
        _autoTranslate.Checked = config.General.AutoTranslate;
        _targetLanguage.Text = config.General.TargetLanguage;
        _pipelineMode.SelectedItem = config.Pipeline.Mode;
    }

    public string? Validate() => string.IsNullOrWhiteSpace(_targetLanguage.Text) ? "目标语言不能为空。" : null;

    public void Apply(AppConfig config)
    {
        config.General.AutoTranslate = _autoTranslate.Checked;
        config.General.TargetLanguage = _targetLanguage.Text.Trim();
        config.Pipeline.Mode = _pipelineMode.SelectedItem as string ?? "local_ocr";
    }
}
