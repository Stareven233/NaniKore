using System.Diagnostics;
using System.Drawing.Imaging;
using NaniKore.Ocr.Detection;
using NaniKore.Ocr.Models;
using NaniKore.Ocr.Recognition;

namespace NaniKore.Ocr;

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

    public OcrResult Recognize(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var stopwatch = Stopwatch.StartNew();
        var detections = _detector.Detect(image);
        var recognized = new List<OcrLine>(detections.Count);

        foreach (var detection in detections)
        {
            using var crop = image.Clone(detection.Bounds, PixelFormat.Format32bppArgb);
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

    public string DescribeModels() =>
        $"Detector {_detector.DescribeModel()}{Environment.NewLine}" +
        $"Recognizer {_recognizer.DescribeModel()}";

    public void Dispose()
    {
        _recognizer.Dispose();
        _detector.Dispose();
    }
}

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
        var overlap = Math.Max(0, intersectionBottom - intersectionTop);
        var minimumHeight = Math.Max(1, Math.Min(representative.Height, candidate.Bounds.Height));
        return overlap / (double)minimumHeight >= 0.5;
    }
}