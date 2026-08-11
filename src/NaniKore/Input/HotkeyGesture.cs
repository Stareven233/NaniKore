using System.Globalization;
using System.Windows.Forms;

namespace NaniKore.Input;

/// <summary>
/// 热键组合的唯一解析、校验与规范化入口。
/// 设置页的录制控件与实际的 RegisterHotKey 注册共用这里的规则，避免「能录进去但注册不了」的分叉。
/// </summary>
internal static class HotkeyGesture
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const uint VirtualKeyF1 = 0x70;

    /// <summary>解析配置里的文本组合；非法输入抛出可直接展示给用户的说明。</summary>
    internal static (uint Modifiers, uint Key) Parse(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            throw new ArgumentException("截图热键不能为空。", nameof(gesture));
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var rawPart in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // 配置可能是 control+shift+q 或 alt+f9 等写法，统一大写后再判定修饰键与普通键。
            var part = rawPart.ToUpperInvariant();
            switch (part)
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

    /// <summary>把任意合法写法（如 control+shift+q）统一成展示用的 Ctrl+Shift+Q。</summary>
    internal static string Normalize(string gesture)
    {
        var (modifiers, key) = Parse(gesture);
        return Format(modifiers, key);
    }

    /// <summary>按固定顺序拼接修饰键，保证配置、界面和提示里的文本完全一致。</summary>
    internal static string Format(uint modifiers, uint key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(key));
        return string.Join('+', parts);
    }

    /// <summary>
    /// 把一次按键（普通键 + 当前修饰键状态）转换成规范化组合。
    /// 返回 false 时 <paramref name="message"/> 是给用户的可理解提示，录制状态应保持不变。
    /// </summary>
    internal static bool TryCapture(uint modifiers, Keys keyCode, out string gesture, out string message)
    {
        gesture = string.Empty;
        if (IsModifierKey(keyCode))
        {
            message = "请在按住修饰键的同时按下一个普通键。";
            return false;
        }

        if (modifiers == 0)
        {
            message = "热键必须包含至少一个修饰键，例如 Ctrl、Alt、Shift 或 Win。";
            return false;
        }

        if (!TryMapKeyCode(keyCode, out var virtualKey))
        {
            message = "当前只支持字母、数字和 F1–F24 作为普通键。";
            return false;
        }

        gesture = Format(modifiers, virtualKey);
        message = string.Empty;
        return true;
    }

    /// <summary>录制过程中的实时回显：只有修饰键时显示 `Ctrl+Shift+…` 提示还差一个普通键。</summary>
    internal static string FormatPreview(uint modifiers)
    {
        if (modifiers == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        return string.Join('+', parts) + "+…";
    }

    /// <summary>
    /// 读取当前真实按下的修饰键。
    /// 录制走原始窗口消息，`WM_KEYDOWN` 的 wParam 只带虚拟键码，修饰键必须单独查询。
    /// </summary>
    internal static uint GetPressedModifiers()
    {
        uint modifiers = 0;
        if (IsKeyDown(VirtualKeyControl))
        {
            modifiers |= ModControl;
        }

        if (IsKeyDown(VirtualKeyShift))
        {
            modifiers |= ModShift;
        }

        if (IsKeyDown(VirtualKeyMenu))
        {
            modifiers |= ModAlt;
        }

        if (IsKeyDown(VirtualKeyLeftWindows) || IsKeyDown(VirtualKeyRightWindows))
        {
            modifiers |= ModWin;
        }

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (Interop.NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

    internal static bool IsModifierKey(Keys keyCode) => keyCode
        is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
        or Keys.LControlKey or Keys.RControlKey
        or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    private static bool TryMapKeyCode(Keys keyCode, out uint virtualKey)
    {
        virtualKey = 0;
        if (keyCode is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9)
        {
            virtualKey = (uint)keyCode;
            return true;
        }

        if (keyCode is >= Keys.F1 and <= Keys.F24)
        {
            virtualKey = (uint)keyCode;
            return true;
        }

        return false;
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
            key = VirtualKeyF1 + (uint)functionKey - 1;
            return true;
        }

        key = 0;
        return false;
    }

    private static string FormatKey(uint key) => key switch
    {
        >= VirtualKeyF1 and <= VirtualKeyF1 + 23 => $"F{key - VirtualKeyF1 + 1}",
        _ => ((char)key).ToString(CultureInfo.InvariantCulture),
    };
}
