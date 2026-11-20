using System.Globalization;

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
    private const uint MouseMiddle = 0x10001;
    private const uint MouseX1 = 0x10002;
    private const uint MouseX2 = 0x10003;

    /// <summary>允许零修饰键单独绑定的功能键上限（F1–F11）；F13–F24 与字母数字一样必须搭配修饰键。</summary>
    private const uint BareFunctionKeyLimit = VirtualKeyF1 + 10;

    // 使用稳定的虚拟键名保存配置；OEM 标点受键盘布局影响，不能把显示字符直接当键码。
    private static readonly Dictionary<uint, string> NamedKeys = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0C] = "Clear", [0x0D] = "Enter",
        [0x13] = "Pause", [0x14] = "CapsLock", [0x1B] = "Escape", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete", [0x5D] = "Apps",
        [0x6A] = "Multiply", [0x6B] = "Add", [0x6C] = "Separator", [0x6D] = "Subtract",
        [0x6E] = "Decimal", [0x6F] = "Divide", [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xA6] = "BrowserBack", [0xA7] = "BrowserForward", [0xA8] = "BrowserRefresh",
        [0xA9] = "BrowserStop", [0xAA] = "BrowserSearch", [0xAB] = "BrowserFavorites", [0xAC] = "BrowserHome",
        [0xAD] = "VolumeMute", [0xAE] = "VolumeDown", [0xAF] = "VolumeUp",
        [0xB0] = "MediaNextTrack", [0xB1] = "MediaPreviousTrack", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause",
        [0xB4] = "LaunchMail", [0xB5] = "SelectMedia", [0xB6] = "LaunchApplication1", [0xB7] = "LaunchApplication2",
        [0xBA] = "OemSemicolon", [0xBB] = "OemPlus", [0xBC] = "OemComma", [0xBD] = "OemMinus",
        [0xBE] = "OemPeriod", [0xBF] = "OemQuestion", [0xC0] = "OemTilde",
        [0xDB] = "OemOpenBrackets", [0xDC] = "OemPipe", [0xDD] = "OemCloseBrackets",
        [0xDE] = "OemQuotes", [0xDF] = "Oem8", [0xE2] = "OemBackslash",
    };

    private static readonly Dictionary<string, uint> KeyNames = CreateKeyNames();

    /// <summary>解析配置里的文本组合；非法输入抛出可直接展示给用户的说明。</summary>
    internal static (uint Modifiers, uint Key) Parse(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            throw new ArgumentException("热键不能为空", nameof(gesture));
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var rawPart in gesture.Split('+', StringSplitOptions.TrimEntries))
        {
            // 配置可能是 control+shift+q 或 alt+f9 等写法，统一大写后再判定修饰键与普通键。
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= ModControl; break;
                case "SHIFT": modifiers |= ModShift; break;
                case "ALT": modifiers |= ModAlt; break;
                case "WIN" or "WINDOWS": modifiers |= ModWin; break;
                case "MIDDLEMOUSE" or "MOUSEMIDDLE" or "中键": key = MouseMiddle; break;
                case "MOUSE4" or "XBUTTON1" or "MOUSEX1" or "鼠标侧键1" or "侧键1": key = MouseX1; break;
                case "MOUSE5" or "XBUTTON2" or "MOUSEX2" or "鼠标侧键2" or "侧键2": key = MouseX2; break;
                default:
                    if (key is not null || !TryParseKey(part, out var parsed))
                    {
                        throw new ArgumentException("热键格式应类似 Ctrl+Shift+A、Alt+F9 或 F5", nameof(gesture));
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            throw new ArgumentException("热键必须包含一个普通按键，格式应类似 Ctrl+Shift+A、Alt+F9 或 F5", nameof(gesture));
        }

        if (key == 0x7B)
        {
            throw new ArgumentException("F12 由 Windows 保留给调试器，不能注册为全局热键", nameof(gesture));
        }

        // F1–F11 允许零修饰键绑定；F12 是系统保留键，其余按键要求修饰键。
        if (modifiers == 0 && !IsBareFunctionKey(key.Value) && !IsMouseButton(key.Value))
        {
            throw new ArgumentException("除 F1–F11 可单独绑定外，热键必须包含至少一个修饰键", nameof(gesture));
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
    internal static bool TryCapture(uint modifiers, uint keyCode, out string gesture, out string message)
    {
        gesture = string.Empty;
        if (IsModifierKey(keyCode))
        {
            message = "请在按住修饰键的同时按下一个普通键";
            return false;
        }

        if (!TryMapKeyCode(keyCode, out var virtualKey))
        {
            message = "不支持此按键；可使用字母、数字、功能键、标点、导航键、小键盘或多媒体键";
            return false;
        }

        // 直接复用配置校验，确保录制与注册的按键范围、系统保留键规则一致。
        try
        {
            gesture = Normalize(Format(modifiers, virtualKey));
            message = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }
    }

    internal static bool TryCaptureMouse(uint modifiers, uint button, out string gesture, out string message)
    {
        uint key = button switch { 2 => MouseMiddle, 3 => MouseX1, 4 => MouseX2, _ => 0u };
        if (key == 0)
        {
            gesture = string.Empty;
            message = "仅支持中键和鼠标侧键";
            return false;
        }

        gesture = Format(modifiers, key);
        message = string.Empty;
        return true;
    }

    internal static bool IsMouseButton(uint key) => key is MouseMiddle or MouseX1 or MouseX2;

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

    /// <summary>
    /// 空串＝关闭该热键：注册路径据此整体跳过，绝不把空手势喂给 RegisterHotKey。
    /// 截图热键另有归一化兜底（恒有值），这里对两个槽位一视同仁。
    /// </summary>
    /// <summary>低级全局钩子运行在后台线程，修饰键状态必须读取系统异步状态。</summary>
    internal static uint GetAsyncPressedModifiers()
    {
        uint modifiers = 0;
        if (IsAsyncKeyDown(VirtualKeyControl)) modifiers |= ModControl;
        if (IsAsyncKeyDown(VirtualKeyShift)) modifiers |= ModShift;
        if (IsAsyncKeyDown(VirtualKeyMenu)) modifiers |= ModAlt;
        if (IsAsyncKeyDown(VirtualKeyLeftWindows) || IsAsyncKeyDown(0x5C)) modifiers |= ModWin;
        return modifiers;
    }

    internal static bool IsEnabled(string? gesture) => !string.IsNullOrWhiteSpace(gesture);

    /// <summary>F1–F11 才允许裸键绑定；F13–F24 与字母数字一样必须带修饰键。</summary>
    private static bool IsBareFunctionKey(uint key) => key is >= VirtualKeyF1 and <= BareFunctionKeyLimit;

    private static bool IsKeyDown(int virtualKey) => (Interop.NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsAsyncKeyDown(int virtualKey) => (Interop.NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static bool IsModifierKey(uint keyCode) => keyCode
        is (uint)VirtualKeyShift or (uint)VirtualKeyControl or (uint)VirtualKeyMenu
        or 0xA0 or 0xA1 // LShift / RShift
        or 0xA2 or 0xA3 // LControl / RControl
        or 0xA4 or 0xA5 // LMenu / RMenu
        or (uint)VirtualKeyLeftWindows or (uint)VirtualKeyRightWindows;

    private static bool TryMapKeyCode(uint keyCode, out uint virtualKey)
    {
        virtualKey = 0;
        if (keyCode is >= (uint)'A' and <= (uint)'Z' or >= (uint)'0' and <= (uint)'9')
        {
            virtualKey = keyCode;
            return true;
        }

        if (keyCode is >= VirtualKeyF1 and <= VirtualKeyF1 + 23)
        {
            virtualKey = keyCode;
            return true;
        }

        if (keyCode is >= 0x60 and <= 0x69 || NamedKeys.ContainsKey(keyCode))
        {
            virtualKey = keyCode;
            return true;
        }

        return false;
    }

    private static bool TryParseKey(string value, out uint key)
    {
        if (value.Length == 1 && value[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
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

        if (value.StartsWith("NUMPAD", StringComparison.Ordinal) && value.Length == 7 && value[6] is >= '0' and <= '9')
        {
            key = 0x60 + (uint)(value[6] - '0');
            return true;
        }

        return KeyNames.TryGetValue(value, out key);
    }

    private static Dictionary<string, uint> CreateKeyNames()
    {
        var names = NamedKeys.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        // 符号只作为输入别名，写回统一使用 OEM 名，避免 '+' 与组合分隔符混淆。
        foreach (var (name, key) in new (string, uint)[]
        {
            ("Esc", 0x1B), ("Back", 0x08), ("Return", 0x0D), ("PgUp", 0x21), ("PgDn", 0x22),
            ("Del", 0x2E), ("Ins", 0x2D), ("Print", 0x2C), ("Plus", 0xBB), ("Minus", 0xBD),
            (";", 0xBA), ("=", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF),
            ("`", 0xC0), ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE),
        })
        {
            names.Add(name, key);
        }
        return names;
    }

    private static string FormatKey(uint key) => key switch
    {
        MouseMiddle => "中键",
        MouseX1 => "侧键1",
        MouseX2 => "侧键2",
        >= VirtualKeyF1 and <= VirtualKeyF1 + 23 => $"F{key - VirtualKeyF1 + 1}",
        >= 0x60 and <= 0x69 => $"NumPad{key - 0x60}",
        _ when NamedKeys.TryGetValue(key, out var name) => name,
        _ => ((char)key).ToString(CultureInfo.InvariantCulture),
    };
}
