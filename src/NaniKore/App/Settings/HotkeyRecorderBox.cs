using System.ComponentModel;
using System.Windows.Forms;
using NaniKore.Input;

namespace NaniKore.App.Settings;

/// <summary>
/// 受控热键录制框：点击显示框即进入录制，按键期间实时回显规范化组合。
/// 非法组合与仅修饰键会保留录制状态并给出提示，不会写出空热键或未支持的按键。
/// </summary>
internal sealed class HotkeyRecorderBox : TextBox
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmChar = 0x0102;
    private const int WmSysChar = 0x0106;

    private string _gesture = string.Empty;
    private string _gestureBeforeRecording = string.Empty;

    public HotkeyRecorderBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TabStop = true;
        TextAlign = HorizontalAlignment.Center;
        ShortcutsEnabled = false;
    }

    /// <summary>录制状态变化或捕获失败时上报可展示的提示文本。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>录制开始/结束；宿主借此临时注销全局热键，否则已注册的组合会被系统吞掉。</summary>
    public event EventHandler<bool>? RecordingChanged;

    /// <summary>当前已确认的规范化组合；录制取消时会回到这个值。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Gesture
    {
        get => _gesture;
        set
        {
            _gesture = string.IsNullOrWhiteSpace(value) ? string.Empty : value;
            Text = _gesture;
        }
    }

    public bool IsRecording { get; private set; }

    /// <summary>进入录制状态；点击显示框或获得键盘焦点都走这里。</summary>
    public void BeginRecording()
    {
        if (IsRecording)
        {
            return;
        }

        _gestureBeforeRecording = _gesture;
        IsRecording = true;
        Text = "请按下快捷键，Esc 取消";
        if (!Focused)
        {
            Focus();
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        BeginRecording();
    }


    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        CancelRecording();
    }

    /// <summary>录制期间必须吃掉 Tab、Esc、方向键等对话框导航键，否则捕获不到完整组合。</summary>
    protected override bool IsInputKey(Keys keyData) => IsRecording || base.IsInputKey(keyData);

    /// <summary>
    /// 直接在窗口消息层处理按键。
    /// 宿主是 WPF <see cref="System.Windows.Application"/>，WinForms 的 <c>ProcessCmdKey</c>
    /// 依赖 WinForms 消息循环的预处理，在这里不会被调用，所以录制必须走 <see cref="WndProc"/>。
    /// </summary>
    protected override void WndProc(ref Message message)
    {
        if (!IsRecording)
        {
            // 仅在显式操作时进入录制：鼠标点击，或聚焦后按 Enter/空格（键盘可达）。
            // 不用 GotFocus 触发，否则切到热键页时会被动开始录制并注销全局热键。
            if (message.Msg is WmKeyDown or WmSysKeyDown && ((Keys)message.WParam.ToInt32() & Keys.KeyCode) is Keys.Enter or Keys.Space)
            {
                BeginRecording();
                return;
            }

            base.WndProc(ref message);
            return;
        }

        switch (message.Msg)
        {
            case WmKeyDown or WmSysKeyDown:
                ProcessRecordingKey((Keys)message.WParam.ToInt32(), HotkeyGesture.GetPressedModifiers());
                return;
            // 修饰键释放时刷新回显，并吃掉字符消息避免只读框发出提示音。
            case WmKeyUp or WmSysKeyUp:
                UpdatePreview();
                return;
            case WmChar or WmSysChar:
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    /// <summary>解释录制中的一次按键；修饰键状态由调用方提供，便于自测确定性地驱动录制。</summary>
    internal void ProcessRecordingKey(Keys keyCode, uint modifiers)
    {
        if (!IsRecording)
        {
            return;
        }

        keyCode &= Keys.KeyCode;
        if (keyCode == Keys.Escape)
        {
            CancelRecording();
            return;
        }

        if (keyCode is Keys.Back or Keys.Delete)
        {
            // Backspace 不能清空热键：应用始终需要一个可注册的组合。
            StatusChanged?.Invoke(this, "热键不能为空；请直接录制新的组合或按 Esc 取消。");
            return;
        }

        if (!HotkeyGesture.TryCapture(modifiers, keyCode, out var gesture, out var message))
        {
            // 只按住修饰键时实时回显 Ctrl+Shift+… ，让主人看到录制确实在进行。
            ShowPreview(modifiers);
            StatusChanged?.Invoke(this, message);
            return;
        }

        EndRecording(gesture);
        StatusChanged?.Invoke(this, $"已录制：{gesture}。保存时会尝试向系统注册。");
    }

    private void UpdatePreview()
    {
        if (IsRecording)
        {
            ShowPreview(HotkeyGesture.GetPressedModifiers());
        }
    }

    private void ShowPreview(uint modifiers)
    {
        var preview = HotkeyGesture.FormatPreview(modifiers);
        Text = preview.Length > 0 ? preview : "请按下快捷键，Esc 取消";
    }

    private void EndRecording(string gesture)
    {
        IsRecording = false;
        Gesture = gesture;
        RecordingChanged?.Invoke(this, false);
    }
}
