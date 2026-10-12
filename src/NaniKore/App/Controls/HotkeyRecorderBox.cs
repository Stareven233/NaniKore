using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NaniKore.Input;
using FormsKeys = System.Windows.Forms.Keys;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NaniKore.App.Controls;

/// <summary>
/// WPF 版热键录制框：点击或按 Enter/空格进入录制，按键期间实时回显规范化组合。
/// 录制规则完全复用 <see cref="HotkeyGesture"/>，避免「能录进去但注册不了」的分叉；
/// 非法组合与仅修饰键会保留录制状态并给出提示，默认不会写出空热键。
/// <see cref="AllowUnbind"/> 为 true 的槽位（选取翻译）额外支持 Esc 解除绑定，空串＝关闭该热键。
/// </summary>
internal sealed class HotkeyRecorderBox : Control
{
    private const string RecordingPrompt = "请按下快捷键，Esc 取消";

    /// <summary>当前已确认的规范化组合；录制取消时会回到这个值。</summary>
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture),
        typeof(string),
        typeof(HotkeyRecorderBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnGestureChanged, CoerceGesture));

    /// <summary>录制中的实时回显文本；非录制状态等于 <see cref="Gesture"/>，手势为空时回落 <see cref="EmptyHint"/>。</summary>
    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText),
        typeof(string),
        typeof(HotkeyRecorderBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    /// <summary>是否正在录制；模板据此切换强调色描边与提示态。</summary>
    private static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRecording),
        typeof(bool),
        typeof(HotkeyRecorderBox),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;

    /// <summary>
    /// 为 true 时 Esc 解除绑定（手势置空串，空串＝关闭该热键）；
    /// 为 false 时 Esc 只是取消录制并保留进入录制前的组合（截图热键必须恒有值）。
    /// </summary>
    public static readonly DependencyProperty AllowUnbindProperty = DependencyProperty.Register(
        nameof(AllowUnbind),
        typeof(bool),
        typeof(HotkeyRecorderBox),
        new PropertyMetadata(false));

    /// <summary>手势为空时方框里的占位提示；留空则显示空串。</summary>
    public static readonly DependencyProperty EmptyHintProperty = DependencyProperty.Register(
        nameof(EmptyHint),
        typeof(string),
        typeof(HotkeyRecorderBox),
        new PropertyMetadata(string.Empty, OnEmptyHintChanged));

    private string _gestureBeforeRecording = string.Empty;

    static HotkeyRecorderBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(HotkeyRecorderBox),
            new FrameworkPropertyMetadata(typeof(HotkeyRecorderBox)));
        FocusableProperty.OverrideMetadata(typeof(HotkeyRecorderBox), new FrameworkPropertyMetadata(true));
    }

    /// <summary>录制状态变化或捕获失败时上报可展示的提示文本。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>录制开始/结束；宿主借此临时注销全局热键，否则已注册的组合会被系统吞掉。</summary>
    public event EventHandler<bool>? RecordingChanged;

    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextPropertyKey, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        private set => SetValue(IsRecordingPropertyKey, value);
    }

    /// <summary>见 <see cref="AllowUnbindProperty"/>。</summary>
    public bool AllowUnbind
    {
        get => (bool)GetValue(AllowUnbindProperty);
        set => SetValue(AllowUnbindProperty, value);
    }

    /// <summary>见 <see cref="EmptyHintProperty"/>。</summary>
    public string EmptyHint
    {
        get => (string)GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
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
        if (!IsKeyboardFocusWithin)
        {
            _ = Focus();
        }

        RecordingChanged?.Invoke(this, true);
        StatusChanged?.Invoke(this, "正在录制：按下修饰键加一个普通键，Esc 取消。");
    }

    /// <summary>取消录制并恢复进入录制前的组合，绝不留下空热键。</summary>
    public void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        EndRecording(_gestureBeforeRecording);
        StatusChanged?.Invoke(this, "已取消录制，保留原有热键。");
    }

    /// <summary>解释录制中的一次按键；修饰键状态由调用方提供，便于自测确定性地驱动录制。</summary>
    public void ProcessRecordingKey(FormsKeys keyCode, uint modifiers)
    {
        if (!IsRecording)
        {
            return;
        }

        keyCode &= FormsKeys.KeyCode;
        if (keyCode == FormsKeys.Escape)
        {
            if (AllowUnbind)
            {
                // 允许留空的槽位（选取翻译）：Esc 直接解除绑定，保存后该热键关闭。
                EndRecording(string.Empty);
                StatusChanged?.Invoke(this, "已解除绑定：保存后该热键关闭。");
                return;
            }

            CancelRecording();
            return;
        }

        if (keyCode is FormsKeys.Back or FormsKeys.Delete)
        {
            // Backspace 不能清空热键：截图翻译必须有一个可注册的组合；选取翻译用 Esc 解绑。
            StatusChanged?.Invoke(this, AllowUnbind
                ? "请直接录制新的组合，或按 Esc 解除绑定。"
                : "热键不能为空；请直接录制新的组合或按 Esc 取消。");
            return;
        }

        if (!HotkeyGesture.TryCapture(modifiers, keyCode, out var gesture, out var message))
        {
            // 只按住修饰键时实时回显 Ctrl+Shift+… ，让用户看到录制确实在进行。
            ShowPreview(modifiers);
            StatusChanged?.Invoke(this, message);
            return;
        }

        EndRecording(gesture);
        StatusChanged?.Invoke(this, $"已录制：{gesture}。保存时会尝试向系统注册。");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        BeginRecording();
        e.Handled = true;
    }

    /// <summary>
    /// 录制走 PreviewKeyDown 而不是 KeyDown：Tab、Esc、方向键会被 WPF 的焦点导航先行消费，
    /// 只有在预览阶段拦截才能捕获完整组合。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            // 仅在显式操作时进入录制；不用 GotFocus 触发，否则切到热键页就会被动注销全局热键。
            if (e.Key is Key.Enter or Key.Space)
            {
                BeginRecording();
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        // 系统键（带 Alt）走 SystemKey，普通键走 Key。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        ProcessRecordingKey((FormsKeys)KeyInterop.VirtualKeyFromKey(key), HotkeyGesture.GetPressedModifiers());
        e.Handled = true;
    }

    /// <summary>修饰键释放时刷新回显，让 Ctrl+Shift+… 预览跟随实际按住的键。</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (IsRecording)
        {
            ShowPreview(HotkeyGesture.GetPressedModifiers());
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        CancelRecording();
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

    /// <summary>非录制态的展示文本：空手势回落占位提示，避免方框里什么都不显示。</summary>
    private string ResolveDisplayText() => Gesture is { Length: > 0 } gesture ? gesture : EmptyHint;

    private static object CoerceGesture(DependencyObject sender, object baseValue) =>
        baseValue as string is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : string.Empty;

    private static void OnGestureChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (HotkeyRecorderBox)sender;
        if (!box.IsRecording)
        {
            box.DisplayText = box.ResolveDisplayText();
        }
    }

    private static void OnEmptyHintChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (HotkeyRecorderBox)sender;
        if (!box.IsRecording)
        {
            box.DisplayText = box.ResolveDisplayText();
        }
    }

}
