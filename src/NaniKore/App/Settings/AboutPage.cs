using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

internal sealed class AboutPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };

    public AboutPage()
    {
        _view.Controls.Add(new Label
        {
            Text = "NaniKore\r\n\r\n本地 PP-OCRv6 + 可配置云端流式翻译。\r\nAPI Key 仅以当前 Windows 用户的 DPAPI 密文保存。",
            AutoSize = true,
            Left = 16,
            Top = 16,
        });
    }

    public string Title => "关于";

    public Control View => _view;

    public void Load(AppConfig config) { }

    public string? Validate() => null;

    public void Apply(AppConfig config) { }
}
