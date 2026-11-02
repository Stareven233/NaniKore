using System.Diagnostics;
using NaniKore.Ocr.Detection;
using NaniKore.Ocr.Models;
using NaniKore.Ocr.Recognition;
using SkiaSharp;

namespace NaniKore.Ocr;

/// <summary>
/// 本地 OCR 核心引擎：编排 TextDetector（文本行检测）与 TextRecognizer（字符识别）。
/// 输入与切片全面基于 SkiaSharp SKBitmap，彻底脱离 GDI+ 与 System.Drawing。
/// </summary>
internal sealed class OcrEngine : IDisposable
{
    private readonly TextDetector _detector;
    private readonly TextRecognizer _recognizer;

    private OcrEngine(PaddleOcrModelConfig config)
    {
        _detector = new TextDetector(config.DetectorModelPath, config.Detector);
        try
        {
            _recognizer = new TextRecognizer(config.RecognizerModelPath, config.Recognizer);
        }
        catch
        {
            _detector.Dispose();
            throw;
        }
    }

    public static OcrEngine CreateDefault() =>
        new(PaddleOcrModelConfig.Load(ModelLocator.FindPaddleOcrDirectory()));

    /// <summary>
    /// 对给定位图执行完整的检测与识别，返回按自然阅读顺序排序后的文本行结果。
    /// </summary>
    public OcrResult Recognize(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var stopwatch = Stopwatch.StartNew();
        var detections = _detector.Detect(image);
        var recognized = new List<OcrLine>(detections.Count);

        foreach (var detection in detections)
        {
            using var crop = Crop(image, detection.Bounds);
            var text = _recognizer.Recognize(crop);
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            recognized.Add(new OcrLine(
                detection.Bounds,
                text.Text,
                MathF.Sqrt(Math.Clamp(detection.Confidence * text.Confidence, 0, 1))));
        }

        stopwatch.Stop();
        return new OcrResult(ReadingOrder.Sort(recognized), stopwatch.Elapsed);
    }

    /// <summary>
    /// 从源位图安全裁剪子区域。优先利用零拷贝 ExtractSubset，越界时回退到 Canvas 绘制拷贝。
    /// </summary>
    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var subset = new SKBitmap();
        if (source.ExtractSubset(subset, bounds))
        {
            return subset;
        }

        var safeWidth = Math.Max(1, bounds.Width);
        var safeHeight = Math.Max(1, bounds.Height);
        var copy = new SKBitmap(safeWidth, safeHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(source, new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), new SKRect(0, 0, safeWidth, safeHeight));
        return copy;
    }

    public string DescribeModels() =>
        $"Detector {_detector.DescribeModel()}{Environment.NewLine}" +
        $"Recognizer {_recognizer.DescribeModel()}";

    public void Dispose()
    {
        _recognizer.Dispose();
        _detector.Dispose();
    }
}

/// <summary>
/// 文本行自然阅读顺序排序器（行分群与从左至右排序）。
/// </summary>
internal static class ReadingOrder
{
    public static IReadOnlyList<OcrLine> Sort(IEnumerable<OcrLine> source)
    {
        var remaining = source
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToList();
        var rows = new List<List<OcrLine>>();

        foreach (var line in remaining)
        {
            var row = rows.FirstOrDefault(candidate => IsSameRow(candidate, line));
            if (row is null)
            {
                rows.Add([line]);
            }
            else
            {
                row.Add(line);
            }
        }

        return rows
            .OrderBy(row => row.Min(line => line.Bounds.Top))
            .SelectMany(row => row.OrderBy(line => line.Bounds.Left))
            .ToArray();
    }

    private static bool IsSameRow(IReadOnlyList<OcrLine> row, OcrLine candidate)
    {
        var representative = row[0].Bounds;
        var intersectionTop = Math.Max(representative.Top, candidate.Bounds.Top);
        var intersectionBottom = Math.Min(representative.Bottom, candidate.Bounds.Bottom);
        var intersectionHeight = Math.Max(0, intersectionBottom - intersectionTop);
        var baselineHeight = Math.Min(representative.Height, candidate.Bounds.Height);
        return baselineHeight > 0 && intersectionHeight / (double)baselineHeight >= 0.5;
    }
}
