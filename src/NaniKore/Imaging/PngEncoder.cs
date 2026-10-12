using System.Drawing;
using System.Drawing.Imaging;

namespace NaniKore.Imaging;

/// <summary>
/// 位图 → PNG 字节：截图链路与剪贴板图像链路共用同一份编码口径（无损、alpha 保留）。
/// 两条链路的 provider 因此拿到同一种字节形态，杜绝两份各自漂移的私有实现。
/// </summary>
internal static class PngEncoder
{
    public static byte[] Encode(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
