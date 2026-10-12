using System.ComponentModel;
using System.Runtime.InteropServices;
using NaniKore.Interop;

namespace NaniKore.Input;

/// <summary>消息专用窗口承载全局热键；支持设置面板保存后的重新注册。</summary>
internal sealed class GlobalHotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly nint HwndMessage = new(-3);

    private readonly int _hotkeyId;
    private readonly Action _onPressed;
    private bool _registered;
    private bool _disposed;

    /// <param name="gesture">规范化组合文本；解析规则与设置页录制控件完全一致。</param>
    /// <param name="onPressed">系统回调（跑在本窗口的线程上）转发按钮按下。</param>
    /// <param name="hotkeyId">
    /// 本实例在进程内的热键 id。同进程注册多个全局热键时必须互不相同，
    /// 否则 <c>WM_HOTKEY</c> 的 wParam 无法区分是谁被按下。
    /// </param>
    public GlobalHotkeyWindow(string gesture, Action onPressed, int hotkeyId)
    {
        _onPressed = onPressed;
        _hotkeyId = hotkeyId;
        // 解析规则与设置页的录制控件完全一致，避免录制通过却注册失败。
        var (modifiers, key) = HotkeyGesture.Parse(gesture);
        CreateHandle(new CreateParams
        {
            Caption = "NaniKore Hotkey Window",
            Parent = HwndMessage,
        });

        _registered = NativeMethods.RegisterHotKey(Handle, _hotkeyId, modifiers | ModNoRepeat, key);
        if (!_registered)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, $"无法注册热键 {gesture}。它可能正被其他程序占用。");
        }
    }

    public static void ValidateGesture(string gesture) => _ = HotkeyGesture.Parse(gesture);

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == _hotkeyId)
        {
            _onPressed();
            return;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, _hotkeyId);
            _registered = false;
        }

        DestroyHandle();
        _disposed = true;
    }
}
