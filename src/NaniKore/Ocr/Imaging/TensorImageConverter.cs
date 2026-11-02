using NaniKore.Ocr.Models;
using SkiaSharp;

namespace NaniKore.Ocr.Imaging;

/// <summary>
/// 将 SkiaSharp SKBitmap 原生转换为 ONNX 推理所需的 NCHW BGR 浮点张量，彻底摆脱 System.Drawing。
/// </summary>
internal static class TensorImageConverter
{
    public static ImageTensor CreateDetectorTensor(SKBitmap image, DetectorConfig config)
    {
        var target = ComputeDetectorSize(new SKSizeI(image.Width, image.Height), config.LimitSideLength, config.MaximumSideLength);
        var tensor = ResizeToNchwBgr(
            image,
            target.Width,
            target.Height,
            target.Width,
            (value, channel) => (value / 255f - config.Means[channel]) / config.StandardDeviations[channel]);
        return new ImageTensor(tensor, target.Width, target.Height);
    }

    public static ImageTensor CreateRecognizerTensor(SKBitmap image, RecognizerConfig config)
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

    internal static SKSizeI ComputeDetectorSize(SKSizeI source, int minimumSideLength, int maximumSideLength)
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
        return new SKSizeI(width, height);
    }

    private static unsafe float[] ResizeToNchwBgr(
        SKBitmap image,
        int resizedWidth,
        int resizedHeight,
        int canvasWidth,
        Func<float, int, float> normalize)
    {
        var output = new float[checked(3 * canvasWidth * resizedHeight)];
        var planeSize = checked(canvasWidth * resizedHeight);

        // 确保位图格式为 Bgra8888（内存顺序为 B, G, R, A，与 PaddleOCR DecodeImage BGR 匹配）
        var source = image.ColorType == SKColorType.Bgra8888
            ? image
            : image.Copy(SKColorType.Bgra8888);
        var needDispose = !ReferenceEquals(source, image);

        try
        {
            var sourceBase = (byte*)source.GetPixels();
            var stride = source.RowBytes;

            for (var targetY = 0; targetY < resizedHeight; targetY++)
            {
                var sourceY = (targetY + 0.5) * source.Height / resizedHeight - 0.5;
                var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, source.Height - 1);
                var y1 = Math.Min(y0 + 1, source.Height - 1);
                var yWeight = (float)Math.Clamp(sourceY - y0, 0, 1);
                var row0 = sourceBase + y0 * stride;
                var row1 = sourceBase + y1 * stride;

                for (var targetX = 0; targetX < resizedWidth; targetX++)
                {
                    var sourceX = (targetX + 0.5) * source.Width / resizedWidth - 0.5;
                    var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, source.Width - 1);
                    var x1 = Math.Min(x0 + 1, source.Width - 1);
                    var xWeight = (float)Math.Clamp(sourceX - x0, 0, 1);
                    var outputIndex = targetY * canvasWidth + targetX;

                    // Bgra8888 内存顺序是 B, G, R, A，对应 channel 0=B, 1=G, 2=R
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
            if (needDispose)
            {
                source.Dispose();
            }
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
