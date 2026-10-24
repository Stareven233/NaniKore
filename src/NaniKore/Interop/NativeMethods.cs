using System.Runtime.InteropServices;

namespace NaniKore.Interop;

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int dwProcessId);

    internal const int AttachParentProcess = -1;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint windowHandle, int id);

    /// <summary>Win 键不在 WinForms 的 ModifierKeys 里，热键录制需要直接查询实时按键状态。</summary>
    [LibraryImport("user32.dll")]
    internal static partial short GetKeyState(int virtualKey);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    /// <summary>标题栏不属于 WPF 客户区；深色主题必须通过 DWM 属性同步，否则会出现白色标题栏。</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>合成输入（模拟 Ctrl+C）：Win32 推荐入口，比已废弃的 keybd_event 更可靠。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, [MarshalAs(UnmanagedType.LPArray)] NativeInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    /// <summary>Win32 INPUT 的托管形态：type + 三成员联合体，偏移 8 起为联合数据（x64 对齐）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeMouseInput Mouse;
        [FieldOffset(8)] public NativeKeyboardInput Keyboard;
        [FieldOffset(8)] public NativeHardwareInput Hardware;
    }

    internal static NativeInput KeyboardEvent(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeKeyboardInput
        {
            VirtualKey = virtualKey,
            Scan = 0,
            Flags = keyUp ? KeyEventKeyUp : 0,
            Time = 0,
            ExtraInfo = 0,
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
