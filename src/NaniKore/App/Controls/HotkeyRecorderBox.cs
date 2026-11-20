using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using NaniKore.Input;

namespace NaniKore.App.Controls;

/// <summary>
/// Avalonia 版热键录制框：点击或按 Enter/空格进入录制，按键期间实时回显规范化组合。
/// 录制规则完全复用 <see cref="HotkeyGesture"/>，避免「能录进去但注册不了」的分叉；
/// 非法组合与仅修饰键会保留录制状态并给出提示，默认不会写出空热键。
/// <see cref="AllowUnbind"/> 为 true 的槽位（选取翻译）额外支持 Esc 解除绑定，空串＝关闭该热键。
/// </summary>
[PseudoClasses(":recording")]
internal sealed class HotkeyRecorderBox : TemplatedControl
{
    private const string RecordingPrompt = "请按下快捷键，Esc 取消";

    public static readonly StyledProperty<string> GestureProperty =
        AvaloniaProperty.Register<HotkeyRecorderBox, string>(nameof(Gesture), string.Empty, defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<HotkeyRecorderBox, string> DisplayTextProperty =
        AvaloniaProperty.RegisterDirect<HotkeyRecorderBox, string>(nameof(DisplayText), o => o.DisplayText);

    public static readonly DirectProperty<HotkeyRecorderBox, bool> IsRecordingProperty =
        AvaloniaProperty.RegisterDirect<HotkeyRecorderBox, bool>(nameof(IsRecording), o => o.IsRecording);

    public static readonly StyledProperty<bool> AllowUnbindProperty =
        AvaloniaProperty.Register<HotkeyRecorderBox, bool>(nameof(AllowUnbind), false);

    public static readonly StyledProperty<string> EmptyHintProperty =
        AvaloniaProperty.Register<HotkeyRecorderBox, string>(nameof(EmptyHint), string.Empty);

    private string _displayText = string.Empty;
    private bool _isRecording;
    private string _gestureBeforeRecording = string.Empty;

    public string Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value ?? string.Empty);
    }

    public string DisplayText
    {
        get => _displayText;
        private set => SetAndRaise(DisplayTextProperty, ref _displayText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetAndRaise(IsRecordingProperty, ref _isRecording, value))
            {
                PseudoClasses.Set(":recording", value);
            }
        }
    }

    public bool AllowUnbind
    {
        get => GetValue(AllowUnbindProperty);
        set => SetValue(AllowUnbindProperty, value);
    }

    public string EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    /// <summary>录制状态变化或捕获失败时上报可展示的提示文本。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>录制开始/结束；宿主借此临时注销全局热键，否则已注册的组合会被系统吞掉。</summary>
    public event EventHandler<bool>? RecordingChanged;

    static HotkeyRecorderBox()
    {
        FocusableProperty.OverrideDefaultValue<HotkeyRecorderBox>(true);
        GestureProperty.Changed.AddClassHandler<HotkeyRecorderBox>((box, _) =>
        {
            if (!box.IsRecording)
            {
                box.DisplayText = box.ResolveDisplayText();
            }
        });
        EmptyHintProperty.Changed.AddClassHandler<HotkeyRecorderBox>((box, _) =>
        {
            if (!box.IsRecording)
            {
                box.DisplayText = box.ResolveDisplayText();
            }
        });
    }

    public HotkeyRecorderBox()
    {
        DisplayText = ResolveDisplayText();
        LostFocus += (_, _) => CancelRecording();
    }

    /// <summary>进入录制状态；点击显示框或聚焦后按 Enter/空格都走这里。</summary>
    public void BeginRecording()
    {
        if (IsRecording)
        {
            return;
        }

        _gestureBeforeRecording = Gesture;
        IsRecording = true;
        DisplayText = RecordingPrompt;
        if (!IsFocused)
        {
            _ = Focus();
        }

        RecordingChanged?.Invoke(this, true);
        StatusChanged?.Invoke(this, AllowUnbind
            ? "正在录制：按下新组合，或按 Esc 解除绑定"
            : "正在录制：按下新组合，F1–F11 可单独绑定，Esc 取消");
    }

    /// <summary>取消录制并恢复进入录制前的组合，绝不留下空热键。</summary>
    public void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        EndRecording(_gestureBeforeRecording);
        StatusChanged?.Invoke(this, "已取消录制，保留原有热键");
    }

    /// <summary>解释录制中的一次按键；修饰键状态由调用方提供，便于自测确定性地驱动录制。</summary>
    public void ProcessRecordingKey(uint keyCode, uint modifiers)
    {
        if (!IsRecording)
        {
            return;
        }

        const uint vkEscape = 0x1B;

        // 只有裸 Esc 执行取消/解绑，Ctrl+Esc 等组合仍可录制。
        if (keyCode == vkEscape && modifiers == 0)
        {
            if (AllowUnbind)
            {
                EndRecording(string.Empty);
                StatusChanged?.Invoke(this, "已解除绑定：保存后该热键关闭");
                return;
            }

            CancelRecording();
            return;
        }

        if (!HotkeyGesture.TryCapture(modifiers, keyCode, out var gesture, out var message))
        {
            ShowPreview(modifiers);
            StatusChanged?.Invoke(this, message);
            return;
        }

        EndRecording(gesture);
        StatusChanged?.Invoke(this, $"已录制：{gesture}。保存时会尝试向系统注册");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsRecording)
        {
            var properties = e.GetCurrentPoint(this).Properties;
            var button = properties.IsMiddleButtonPressed ? 2u : properties.IsXButton1Pressed ? 3u : properties.IsXButton2Pressed ? 4u : 0u;
            if (button != 0)
            {
                HotkeyGesture.TryCaptureMouse(HotkeyGesture.GetPressedModifiers(), button, out var gesture, out _);
                EndRecording(gesture);
                StatusChanged?.Invoke(this, $"已录制：{gesture}。保存时会尝试向系统注册");
            }
            e.Handled = true;
            return;
        }
        BeginRecording();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                BeginRecording();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            return;
        }

        var vk = ToVirtualKey(e.Key);
        var modifiers = HotkeyGesture.GetPressedModifiers();
        if (modifiers == 0)
        {
            modifiers = ToModifiers(e.KeyModifiers);
        }

        ProcessRecordingKey(vk, modifiers);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsRecording)
        {
            var modifiers = HotkeyGesture.GetPressedModifiers();
            if (modifiers == 0)
            {
                modifiers = ToModifiers(e.KeyModifiers);
            }

            ShowPreview(modifiers);
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    private void ShowPreview(uint modifiers)
    {
        var preview = HotkeyGesture.FormatPreview(modifiers);
        DisplayText = preview.Length > 0 ? preview : RecordingPrompt;
    }

    private void EndRecording(string gesture)
    {
        IsRecording = false;
        Gesture = gesture;
        DisplayText = ResolveDisplayText();
        RecordingChanged?.Invoke(this, false);
    }

    private string ResolveDisplayText() => Gesture is { Length: > 0 } gesture ? gesture : EmptyHint;

    internal static uint ToVirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.F1 and <= Key.F24 => 0x70 + (uint)(key - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (uint)(key - Key.NumPad0),
        Key.Tab => 0x09,
        Key.Clear => 0x0C,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Apps => 0x5D,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Separator => 0x6C,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.NumLock => 0x90,
        Key.Scroll => 0x91,
        Key.BrowserBack => 0xA6,
        Key.BrowserForward => 0xA7,
        Key.BrowserRefresh => 0xA8,
        Key.BrowserStop => 0xA9,
        Key.BrowserSearch => 0xAA,
        Key.BrowserFavorites => 0xAB,
        Key.BrowserHome => 0xAC,
        Key.VolumeMute => 0xAD,
        Key.VolumeDown => 0xAE,
        Key.VolumeUp => 0xAF,
        Key.MediaNextTrack => 0xB0,
        Key.MediaPreviousTrack => 0xB1,
        Key.MediaStop => 0xB2,
        Key.MediaPlayPause => 0xB3,
        Key.LaunchMail => 0xB4,
        Key.SelectMedia => 0xB5,
        Key.LaunchApplication1 => 0xB6,
        Key.LaunchApplication2 => 0xB7,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        Key.Oem8 => 0xDF,
        Key.OemBackslash => 0xE2,
        Key.Escape => 0x1B,
        Key.Back => 0x08,
        Key.Delete => 0x2E,
        Key.Enter => 0x0D,
        Key.Space => 0x20,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.LWin or Key.RWin => 0x5B,
        _ => 0,
    };

    private static uint ToModifiers(KeyModifiers keyModifiers)
    {
        uint modifiers = 0;
        if ((keyModifiers & KeyModifiers.Control) != 0) modifiers |= HotkeyGesture.ModControl;
        if ((keyModifiers & KeyModifiers.Shift) != 0) modifiers |= HotkeyGesture.ModShift;
        if ((keyModifiers & KeyModifiers.Alt) != 0) modifiers |= HotkeyGesture.ModAlt;
        if ((keyModifiers & KeyModifiers.Meta) != 0) modifiers |= HotkeyGesture.ModWin;
        return modifiers;
    }
}
