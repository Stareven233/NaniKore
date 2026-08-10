using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

internal sealed class AboutPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly Label _description = new()
    {
        AutoSize = true,
        Left = 16,
        Top = 16,
    };

    public AboutPage()
    {
        _view.Controls.Add(_description);
    }

    public string Title => "关于";

    public Control View => _view;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // version 是产品发布版本，由 config.yaml 手工维护；不为尚未使用的 schema 版本引入额外字段。
        _description.Text = $"NaniKore v{config.Version}\r\n\r\n本地 PP-OCRv6 + 可配置云端流式翻译。\r\nAPI Key 仅以当前 Windows 用户的 DPAPI 密文保存。";
    }

    public string? Validate() => null;

    public void Apply(AppConfig config) { }
}
