using System.ComponentModel;
using System.Runtime.InteropServices;
using NaniKore.Interop;

namespace NaniKore.Input;

/// <summary>消息专用窗口承载全局热键；支持设置面板保存后的重新注册。</summary>
internal sealed class GlobalHotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _onPressed;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyWindow(string gesture, Action onPressed)
    {
        _onPressed = onPressed;
        // 解析规则与设置页的录制控件完全一致，避免录制通过却注册失败。
        var (modifiers, key) = HotkeyGesture.Parse(gesture);
        CreateHandle(new CreateParams
        {
            Caption = "NaniKore Hotkey Window",
            Parent = HwndMessage,
        });

        _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, modifiers | ModNoRepeat, key);
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
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
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
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        DestroyHandle();
        _disposed = true;
    }
}
