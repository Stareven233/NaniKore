using System.ComponentModel;
using System.Runtime.InteropServices;
using NaniKore.Interop;

namespace NaniKore.Input;

internal sealed class GlobalHotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyA = 0x41;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _onPressed;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyWindow(Action onPressed)
    {
        _onPressed = onPressed;

        CreateHandle(new CreateParams
        {
            Caption = "NaniKore Hotkey Window",
            Parent = HwndMessage,
        });

        _registered = NativeMethods.RegisterHotKey(
            Handle,
            HotkeyId,
            ModControl | ModShift | ModNoRepeat,
            VirtualKeyA);

        if (!_registered)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "Failed to register Ctrl+Shift+A.");
        }
    }

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
