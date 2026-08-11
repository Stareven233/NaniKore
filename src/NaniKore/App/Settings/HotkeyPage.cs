using System.Windows.Forms;
using NaniKore.Config;
using NaniKore.Input;

namespace NaniKore.App.Settings;

internal sealed class HotkeyPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _fields = SettingsLayout.CreateFieldTable();
    private readonly HotkeyRecorderBox _capture = new();
    private readonly Label _status = SettingsLayout.CreateWrappingLabel(
        "点击当前热键即可开始录制；需要至少一个修饰键与一个字母、数字或 F1–F24 按键。");

    public HotkeyPage()
    {
        // 点击显示框本身就进入录制，不再需要额外的「录制」按钮。
        SettingsLayout.AddField(_fields, "截图热键：", _capture);
        SettingsLayout.AddFullWidthRow(_fields, _status);
        _view.Controls.Add(_fields);
        _view.Resize += (_, _) => SettingsLayout.UpdateWrapWidth(_view, _status);

        _capture.StatusChanged += (_, message) => _status.Text = message;
        _capture.RecordingChanged += (_, recording) => RecordingChanged?.Invoke(this, recording);
    }

    public string Title => "热键";

    public Control View => _view;

    /// <summary>录制开始/结束时通知宿主临时注销、恢复全局热键。</summary>
    public event EventHandler<bool>? RecordingChanged;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 旧配置可能是 control+shift+a 等写法，读入时统一成展示用组合；非法值保持原样交给校验提示。
        _capture.Gesture = TryNormalize(config.Hotkeys.Capture);
    }

    public string? Validate()
    {
        if (_capture.IsRecording)
        {
            return "热键仍在录制中：请按下组合键或按 Esc 取消。";
        }

        try
        {
            GlobalHotkeyWindow.ValidateGesture(_capture.Gesture);
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Hotkeys.Capture = _capture.Gesture;
    }

    private static string TryNormalize(string gesture)
    {
        try
        {
            return HotkeyGesture.Normalize(gesture);
        }
        catch (ArgumentException)
        {
            return gesture;
        }
    }
}
