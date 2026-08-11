using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Input;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class HotkeyPage : UserControl, ISettingsPage
{
    private const string IdleHint = "点击当前热键即可开始录制；需要至少一个修饰键与一个字母、数字或 F1–F24 按键。";

    public HotkeyPage()
    {
        InitializeComponent();
        RecorderStatus.Show(IdleHint, InfoSeverity.Neutral);
        Recorder.StatusChanged += (_, message) => RecorderStatus.Show(
            message,
            // 录制中与失败提示用不同语义色，用户不必读完整句就知道是否已成功。
            Recorder.IsRecording ? InfoSeverity.Warning : InfoSeverity.Success);
        Recorder.RecordingChanged += (_, recording) => RecordingChanged?.Invoke(this, recording);
    }

    public string Title => "热键";

    public string Glyph => "\uE765";

    public FrameworkElement View => this;

    /// <summary>录制开始/结束时通知宿主临时注销、恢复全局热键。</summary>
    public event EventHandler<bool>? RecordingChanged;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 旧配置可能是 control+shift+a 等写法，读入时统一成展示用组合；非法值保持原样交给校验提示。
        Recorder.Gesture = TryNormalize(config.Hotkeys.Capture);
    }

    public string? Validate()
    {
        if (Recorder.IsRecording)
        {
            return "热键仍在录制中：请按下组合键或按 Esc 取消。";
        }

        try
        {
            GlobalHotkeyWindow.ValidateGesture(Recorder.Gesture);
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
        config.Hotkeys.Capture = Recorder.Gesture;
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
