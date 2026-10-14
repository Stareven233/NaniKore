using System.Runtime.InteropServices;

namespace NaniKore.Diagnostics;

/// <summary>
/// 自测专用的 Win32 剪贴板写入/枚举：造一个「纯外来」剪贴板（EmptyClipboard 后只放标准格式，
/// 不带 WinForms 写入必然附带的 .NET 私有位图格式），模拟用户从浏览器/截图工具复制的内容。
/// 仅 <see cref="SelfTest.TestForeignImageClipboardRoundTrip"/> 使用，不参与产品路径。
/// </summary>
internal static class Win32TestClipboard
{
    public const uint FormatBitmap = 2;
    public const uint FormatUnicodeText = 13;

    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint EnumClipboardFormats(uint format);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteObject(IntPtr handle);

    /// <summary>清空剪贴板后写入 CF_BITMAP + CF_UNICODETEXT；任一 SetClipboardData 成功即句柄归系统所有。</summary>
    public static bool SetForeignImage(IntPtr hBitmap, IntPtr hText)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        EmptyClipboard();
        var bitmapSet = SetClipboardData(FormatBitmap, hBitmap) != IntPtr.Zero;
        var textSet = SetClipboardData(FormatUnicodeText, hText) != IntPtr.Zero;
        CloseClipboard();
        // 全失败才算写不成：部分成功时句柄已移交系统，调用方绝不能再释放。
        return bitmapSet && textSet;
    }

    /// <summary>枚举系统剪贴板上的标准格式号（只回 >= 0xC000 之外的已知标准格式，够断言用）。</summary>
    public static List<uint> EnumerateStandardFormats()
    {
        var result = new List<uint>();
        if (!OpenClipboard(IntPtr.Zero))
        {
            return result;
        }

        try
        {
            var format = 0u;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (format < 0xC000)
                {
                    result.Add(format);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        return result;
    }

    /// <summary>SetClipboardData 全失败后清理本进程分配的句柄。</summary>
    public static void ReleaseFailedHandles(IntPtr hBitmap, IntPtr hText)
    {
        DeleteObject(hBitmap);
        Marshal.FreeHGlobal(hText);
    }
}
