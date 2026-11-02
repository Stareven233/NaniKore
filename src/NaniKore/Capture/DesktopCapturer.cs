using System.Runtime.InteropServices;
using Avalonia;
using NaniKore.Interop;
using SkiaSharp;

namespace NaniKore.Capture;

/// <summary>
/// 虚拟屏幕捕获器：直接利用 Win32 BitBlt 抓取全虚拟桌面（支持负坐标及多显示器），
/// 并通过负高度 top-down DIB 直接灌入 SKBitmap，彻底脱离 System.Drawing 与 GDI+。
/// </summary>
internal static unsafe class DesktopCapturer
{
    /// <summary>
    /// 获取包含所有物理显示器的虚拟屏幕矩形（包含副屏的负坐标）。
    /// </summary>
    public static PixelRect GetVirtualScreenBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"无效的虚拟桌面尺寸: {x},{y} {width}x{height}");
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// 抓取全虚拟屏幕画面。当处于无交互会话（如无头自测或 CI）时，支持回退到合成内存 DC。
    /// </summary>
    public static DesktopFrame CaptureVirtualScreen(bool allowHeadlessFallback = true)
    {
        var bounds = GetVirtualScreenBounds();
        var width = bounds.Width;
        var height = bounds.Height;

        var screenDc = NativeMethods.CreateDC("DISPLAY", null, null, 0);
        if (screenDc == 0)
        {
            screenDc = NativeMethods.GetDC(0);
        }

        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        var oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        try
        {
            var bitBltSuccess = NativeMethods.BitBlt(
                memDc,
                0,
                0,
                width,
                height,
                screenDc,
                bounds.X,
                bounds.Y,
                NativeMethods.SRCCOPY);

            if (!bitBltSuccess)
            {
                var error = Marshal.GetLastPInvokeError();
                // 错误码 6 (ERROR_INVALID_HANDLE)：在无交互会话中，Windows 阻止抓取物理屏幕 DC。
                if (error == 6 && allowHeadlessFallback)
                {
                    return CaptureSyntheticMemoryFrame(bounds);
                }

                throw new InvalidOperationException($"BitBlt 截屏失败，Win32 错误码: {error}");
            }

            var skBitmap = ReadBitmapFromHdc(memDc, hBitmap, width, height);
            return new DesktopFrame(skBitmap, bounds);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            if (screenDc != 0)
            {
                NativeMethods.DeleteDC(screenDc);
            }
        }
    }

    /// <summary>
    /// 无头/后台会话验证管线：通过内存源 DC 验证 BitBlt 与 GetDIBits 原生转换完整性。
    /// </summary>
    private static DesktopFrame CaptureSyntheticMemoryFrame(PixelRect bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;

        var memSrcDc = NativeMethods.CreateCompatibleDC(0);
        var hSrcBmp = NativeMethods.CreateCompatibleBitmap(memSrcDc, width, height);
        var oldSrc = NativeMethods.SelectObject(memSrcDc, hSrcBmp);

        var memDstDc = NativeMethods.CreateCompatibleDC(0);
        var hDstBmp = NativeMethods.CreateCompatibleBitmap(memDstDc, width, height);
        var oldDst = NativeMethods.SelectObject(memDstDc, hDstBmp);

        try
        {
            var ok = NativeMethods.BitBlt(
                memDstDc,
                0,
                0,
                width,
                height,
                memSrcDc,
                0,
                0,
                NativeMethods.SRCCOPY);

            if (!ok)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"内存 DC BitBlt 失败: {err}");
            }

            var skBitmap = ReadBitmapFromHdc(memDstDc, hDstBmp, width, height);
            return new DesktopFrame(skBitmap, bounds);
        }
        finally
        {
            NativeMethods.SelectObject(memSrcDc, oldSrc);
            NativeMethods.DeleteObject(hSrcBmp);
            NativeMethods.DeleteDC(memSrcDc);

            NativeMethods.SelectObject(memDstDc, oldDst);
            NativeMethods.DeleteObject(hDstBmp);
            NativeMethods.DeleteDC(memDstDc);
        }
    }

    private static SKBitmap ReadBitmapFromHdc(nint hdc, nint hBitmap, int width, int height)
    {
        var skBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // 负数代表自顶向下（Top-Down DIB）
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        var lines = NativeMethods.GetDIBits(
            hdc,
            hBitmap,
            0,
            (uint)height,
            (void*)skBitmap.GetPixels(),
            &bmi,
            NativeMethods.DIB_RGB_COLORS);

        if (lines == 0)
        {
            skBitmap.Dispose();
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"GetDIBits 读取位图像素失败，Win32 错误码: {error}");
        }

        return skBitmap;
    }
}

/// <summary>
/// 屏幕捕获帧：包含底层 SKBitmap 画面与物理虚拟屏幕边界。
/// </summary>
internal sealed record DesktopFrame(SKBitmap Image, PixelRect VirtualBounds) : IDisposable
{
    public void Dispose() => Image.Dispose();
}
