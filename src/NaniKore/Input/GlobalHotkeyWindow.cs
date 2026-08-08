using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using NaniKore.Interop;

namespace NaniKore.Input;

/// <summary>消息专用窗口承载全局热键；支持设置面板保存后的重新注册。</summary>
internal sealed class GlobalHotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _onPressed;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyWindow(string gesture, Action onPressed)
    {
        _onPressed = onPressed;
        var (modifiers, key) = ParseGesture(gesture);
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

    public static void ValidateGesture(string gesture) => _ = ParseGesture(gesture);

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

    private static (uint Modifiers, uint Key) ParseGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            throw new ArgumentException("截图热键不能为空。", nameof(gesture));
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= ModControl; break;
                case "SHIFT": modifiers |= ModShift; break;
                case "ALT": modifiers |= ModAlt; break;
                case "WIN" or "WINDOWS": modifiers |= ModWin; break;
                default:
                    if (key is not null || !TryParseKey(part, out var parsed))
                    {
                        throw new ArgumentException("热键格式应类似 Ctrl+Shift+A 或 Alt+F9。", nameof(gesture));
                    }

                    key = parsed;
                    break;
            }
        }

        if (modifiers == 0 || key is null)
        {
            throw new ArgumentException("热键必须包含至少一个修饰键和一个普通按键。", nameof(gesture));
        }

        return (modifiers, key.Value);
    }

    private static bool TryParseKey(string value, out uint key)
    {
        if (value.Length == 1 && char.IsLetterOrDigit(value[0]))
        {
            key = char.ToUpperInvariant(value[0]);
            return true;
        }

        if (value.Length is 2 or 3 && value.StartsWith('F') &&
            int.TryParse(value[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = (uint)(0x70 + functionKey - 1);
            return true;
        }

        key = 0;
        return false;
    }
}
