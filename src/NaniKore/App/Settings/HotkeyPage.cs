using System.Windows.Forms;
using NaniKore.Config;
using NaniKore.Input;

namespace NaniKore.App.Settings;

internal sealed class HotkeyPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly TextBox _capture = new() { Width = 260 };

    public HotkeyPage()
    {
        _view.Controls.Add(new Label
        {
            Text = "截图热键（例如 Ctrl+Shift+A、Alt+F9）：",
            AutoSize = true,
            Left = 16,
            Top = 16,
        });
        _capture.SetBounds(16, 42, 300, 28);
        _view.Controls.Add(_capture);
    }

    public string Title => "热键";

    public Control View => _view;

    public void Load(AppConfig config) => _capture.Text = config.Hotkeys.Capture;

    public string? Validate()
    {
        try
        {
            GlobalHotkeyWindow.ValidateGesture(_capture.Text);
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    public void Apply(AppConfig config) => config.Hotkeys.Capture = _capture.Text.Trim();
}
