using System.Drawing.Imaging;
using NaniKore.Ocr.Models;

namespace NaniKore.Ocr.Imaging;

internal static class TensorImageConverter
{
    public static ImageTensor CreateDetectorTensor(Bitmap image, DetectorConfig config)
    {
        var target = ComputeDetectorSize(image.Size, config.LimitSideLength, config.MaximumSideLength);
        var tensor = ResizeToNchwBgr(
            image,
            target.Width,
            target.Height,
            target.Width,
            (value, channel) => (value / 255f - config.Means[channel]) / config.StandardDeviations[channel]);
        return new ImageTensor(tensor, target.Width, target.Height);
    }

    public static ImageTensor CreateRecognizerTensor(Bitmap image, RecognizerConfig config)
    {
        var widthRatio = image.Width / (double)Math.Max(1, image.Height);
        var targetWidth = Math.Clamp(
            AlignUp((int)Math.Ceiling(config.ImageHeight * Math.Max(
                config.BaseImageWidth / (double)config.ImageHeight,
                widthRatio)), 8),
            config.BaseImageWidth,
            config.MaximumImageWidth);
        var resizedWidth = Math.Clamp(
            (int)Math.Ceiling(config.ImageHeight * widthRatio),
            1,
            targetWidth);

        var tensor = ResizeToNchwBgr(
            image,
            resizedWidth,
            config.ImageHeight,
            targetWidth,
            static (value, _) => (value / 255f - 0.5f) / 0.5f);
        return new ImageTensor(tensor, targetWidth, config.ImageHeight);
    }

    internal static Size ComputeDetectorSize(Size source, int minimumSideLength, int maximumSideLength)
    {
        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Image dimensions must be positive.");
        }

        var scale = Math.Max(1d, minimumSideLength / (double)Math.Min(source.Width, source.Height));
        if (Math.Max(source.Width, source.Height) * scale > maximumSideLength)
        {
            scale = maximumSideLength / (double)Math.Max(source.Width, source.Height);
        }

        // DB 检测网络会连续下采样，宽高必须对齐到 32；限制后至少保留一个 32 像素单元。
        var width = Math.Clamp(AlignNearest((int)Math.Round(source.Width * scale), 32), 32, maximumSideLength);
        var height = Math.Clamp(AlignNearest((int)Math.Round(source.Height * scale), 32), 32, maximumSideLength);
        return new Size(width, height);
    }

    private static unsafe float[] ResizeToNchwBgr(
        Bitmap image,
        int resizedWidth,
        int resizedHeight,
        int canvasWidth,
        Func<float, int, float> normalize)
    {
        var output = new float[checked(3 * canvasWidth * resizedHeight)];
        var planeSize = checked(canvasWidth * resizedHeight);
        var rectangle = new Rectangle(Point.Empty, image.Size);
        var bitmapData = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var sourceBase = (byte*)bitmapData.Scan0;
            for (var targetY = 0; targetY < resizedHeight; targetY++)
            {
                var sourceY = (targetY + 0.5) * image.Height / resizedHeight - 0.5;
                var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, image.Height - 1);
                var y1 = Math.Min(y0 + 1, image.Height - 1);
                var yWeight = (float)Math.Clamp(sourceY - y0, 0, 1);
                var row0 = sourceBase + y0 * bitmapData.Stride;
                var row1 = sourceBase + y1 * bitmapData.Stride;

                for (var targetX = 0; targetX < resizedWidth; targetX++)
                {
                    var sourceX = (targetX + 0.5) * image.Width / resizedWidth - 0.5;
                    var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, image.Width - 1);
                    var x1 = Math.Min(x0 + 1, image.Width - 1);
                    var xWeight = (float)Math.Clamp(sourceX - x0, 0, 1);
                    var outputIndex = targetY * canvasWidth + targetX;

                    // GDI 的 32bpp 内存顺序是 BGRA，正好对应 PaddleOCR 的 BGR DecodeImage。
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var top = Lerp(row0[x0 * 4 + channel], row0[x1 * 4 + channel], xWeight);
                        var bottom = Lerp(row1[x0 * 4 + channel], row1[x1 * 4 + channel], xWeight);
                        output[channel * planeSize + outputIndex] = normalize(Lerp(top, bottom, yWeight), channel);
                    }
                }
            }
        }
        finally
        {
            image.UnlockBits(bitmapData);
        }

        return output;
    }

    private static float Lerp(float left, float right, float amount) => left + (right - left) * amount;

    private static int AlignNearest(int value, int alignment) =>
        Math.Max(alignment, (int)Math.Round(value / (double)alignment) * alignment);

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

internal sealed record ImageTensor(float[] Data, int Width, int Height)
{
    public long[] Dimensions => [1, 3, Height, Width];
}