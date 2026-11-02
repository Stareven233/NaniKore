using SkiaSharp;

namespace NaniKore.Imaging;

/// <summary>
/// 位图 → PNG 字节：截图链路与剪贴板图像链路共用同一份编码口径（无损、alpha 保留）。
/// 统一由 SkiaSharp 负责原生 PNG 编解码，彻底脱离 GDI+ 与 System.Drawing。
/// </summary>
internal static class PngEncoder
{
    /// <summary>
    /// 将 SKBitmap 无损编码为 PNG 字节流。
    /// </summary>
    public static byte[] Encode(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 过渡期支持 System.Drawing.Bitmap（供尚未完成 Skia 迁移的 OCR 部分使用）。
    /// </summary>
    public static byte[] Encode(System.Drawing.Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream();
        image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
}
