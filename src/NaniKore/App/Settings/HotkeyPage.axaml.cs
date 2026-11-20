using System;
using Avalonia.Controls;
using Avalonia.Threading;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Input;

namespace NaniKore.App.Settings;

internal sealed partial class HotkeyPage : UserControl, ISettingsPage
{
    public HotkeyPage()
    {
        InitializeComponent();
        // 通用录制说明集中在页尾；槽位下方仅在发生交互时显示各自状态。
        CaptureRecorder.StatusChanged += (_, message) =>
        {
            CaptureRecorderStatus.Text = message;
            CaptureRecorderStatus.IsVisible = true;
        };
        PasteRecorder.StatusChanged += (_, message) =>
        {
            PasteRecorderStatus.Text = message;
            PasteRecorderStatus.IsVisible = true;
        };

        CaptureRecorder.RecordingChanged += (_, recording) => OnRecordingChanged(CaptureRecorder, recording, HotkeySlot.Capture);
        PasteRecorder.RecordingChanged += (_, recording) => OnRecordingChanged(PasteRecorder, recording, HotkeySlot.Paste);
    }

    public string Title => "热键";

    public string Glyph => "\uE765";

    public Control View => this;

    /// <summary>录制开始/结束时通知宿主临时注销、恢复全部全局热键。</summary>
    public event EventHandler<HotkeyRecordingState>? RecordingChanged;

    private void OnRecordingChanged(HotkeyRecorderBox recorder, bool recording, HotkeySlot slot)
    {
        if (recording)
        {
            var otherRecorder = slot == HotkeySlot.Capture ? PasteRecorder : CaptureRecorder;
            otherRecorder.CancelRecording();
        }
        Win32HotkeyHost.Shared.SetMouseRecordingCallback(recording
            ? (button, modifiers) => Dispatcher.UIThread.Post(() => recorder.ProcessRecordingMouse(button, modifiers))
            : null);
        RecordingChanged?.Invoke(this, new HotkeyRecordingState(recording, slot));
    }
    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        CaptureRecorder.Gesture = TryNormalize(config.Hotkeys.Capture);
        PasteRecorder.Gesture = TryNormalize(config.Hotkeys.Paste);
    }

    public void StopRecording()
    {
        CaptureRecorder.CancelRecording();
        PasteRecorder.CancelRecording();
        Win32HotkeyHost.Shared.SetMouseRecordingCallback(null);
    }

    public string? Validate()
    {
        if (CaptureRecorder.IsRecording)
        {
            return "截图热键仍在录制中：请按下组合键或按 Esc 取消";
        }

        if (PasteRecorder.IsRecording)
        {
            return "选取热键仍在录制中：请按下组合键，或按 Esc 解除绑定";
        }

        if (ValidateGesture("截图翻译热键", CaptureRecorder.Gesture, allowEmpty: false) is { } captureError)
        {
            return captureError;
        }

        if (ValidateGesture("选取翻译热键", PasteRecorder.Gesture, allowEmpty: true) is { } pasteError)
        {
            return pasteError;
        }

        // 按解析后的键码比较，避免 control / Ctrl 或标点别名绕过重复校验。
        if (HotkeyGesture.IsEnabled(PasteRecorder.Gesture) &&
            HotkeyGesture.Parse(CaptureRecorder.Gesture) == HotkeyGesture.Parse(PasteRecorder.Gesture))
        {
            return "截图翻译和选取翻译不能使用相同的热键";
        }

        return null;
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
