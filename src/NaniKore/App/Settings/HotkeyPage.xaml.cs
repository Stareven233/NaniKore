using System.Windows;
using System.Windows.Controls;
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
        CaptureRecorderStatus.Text = IdleHint;
        PasteRecorderStatus.Text = IdleHint;
        CaptureRecorder.StatusChanged += (_, message) => CaptureRecorderStatus.Text = message;
        PasteRecorder.StatusChanged += (_, message) => PasteRecorderStatus.Text = message;
        // 哪个框在录制就临时注销哪个实例，另一个全局热键保持可用。
        CaptureRecorder.RecordingChanged += (_, recording) =>
            RecordingChanged?.Invoke(this, new HotkeyRecordingState(recording, HotkeySlot.Capture));
        PasteRecorder.RecordingChanged += (_, recording) =>
            RecordingChanged?.Invoke(this, new HotkeyRecordingState(recording, HotkeySlot.Paste));
    }

    public string Title => "热键";

    public string Glyph => "\uE765";

    public FrameworkElement View => this;

    /// <summary>录制开始/结束时通知宿主临时注销、恢复对应的全局热键。</summary>
    public event EventHandler<HotkeyRecordingState>? RecordingChanged;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 旧配置可能是 control+shift+a 等写法，读入时统一成展示用组合；非法值保持原样交给校验提示。
        CaptureRecorder.Gesture = TryNormalize(config.Hotkeys.Capture);
        PasteRecorder.Gesture = TryNormalize(config.Hotkeys.Paste);
    }

    public string? Validate()
    {
        if (CaptureRecorder.IsRecording)
        {
            return "截图热键仍在录制中：请按下组合键或按 Esc 取消。";
        }

        if (PasteRecorder.IsRecording)
        {
            return "选取热键仍在录制中：请按下组合键，或按 Esc 解除绑定。";
        }

        // 截图热键必须恒有值；选取热键允许留空（空串＝关闭）。
        if (ValidateGesture("截图翻译热键", CaptureRecorder.Gesture, allowEmpty: false) is { } captureError)
        {
            return captureError;
        }

        return ValidateGesture("选取翻译热键", PasteRecorder.Gesture, allowEmpty: true);
    }

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Hotkeys.Capture = CaptureRecorder.Gesture;
        config.Hotkeys.Paste = PasteRecorder.Gesture;
    }

    private static string? ValidateGesture(string label, string gesture, bool allowEmpty)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        try
        {
            GlobalHotkeyWindow.ValidateGesture(gesture);
            return null;
        }
        catch (ArgumentException exception)
        {
            return $"{label}：{exception.Message}";
        }
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
