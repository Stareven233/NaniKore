using System.Net;
using System.Net.Http;
using Avalonia;
using NaniKore.App;
using NaniKore.Imaging;
using NaniKore.Ocr;
using NaniKore.Ocr.Detection;
using NaniKore.Ocr.Imaging;
using NaniKore.Ocr.Models;
using NaniKore.Ocr.Recognition;
using NaniKore.Overlay;
using NaniKore.Translation;
using SkiaSharp;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunOcrTests()
    {
        TestSelectionGeometry();
        TestImageTensorConversion();
        TestDetectionPostprocessing();
        TestCtcDecoding();
        TestReadingOrder();
        TestSourceTextNormalization();
        TestOcrHttpServer();
    }

    private static void TestSelectionGeometry()
    {
        Check(
            "normalizes reverse drag",
            SelectionGeometry.Normalize(new PixelPoint(90, 70), new PixelPoint(10, 20), new PixelSize(100, 80)) ==
            new PixelRect(10, 20, 80, 50));

        Check(
            "clamps selection to bitmap bounds",
            SelectionGeometry.Normalize(new PixelPoint(-20, 10), new PixelPoint(130, 90), new PixelSize(100, 80)) ==
            new PixelRect(0, 10, 100, 70));

        var outer = new PixelRect(0, 0, 100, 80);
        var selection = new PixelRect(10, 20, 30, 25);
        var outsideArea = SelectionGeometry.GetOutsideRegions(outer, selection)
            .Sum(region => region.Width * region.Height);
        Check(
            "outside dim regions do not overlap the selection",
            outsideArea == outer.Width * outer.Height - selection.Width * selection.Height);

        using var source = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        var subset = new SKBitmap();
        var croppedOk = source.ExtractSubset(subset, new SKRectI(2, 3, 6, 8));
        Check("bitmap crop keeps requested dimensions", croppedOk && subset.Width == 4 && subset.Height == 5);
        subset.Dispose();
    }

    private static void TestImageTensorConversion()
    {
        Check(
            "detector resize aligns dimensions to 32",
            TensorImageConverter.ComputeDetectorSize(new SKSizeI(100, 50), 736, 4000) == new SKSizeI(1472, 736));

        using var pixel = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        pixel.SetPixel(0, 0, new SKColor(10, 20, 30, 255)); // R=10, G=20, B=30
        var detectorConfig = new DetectorConfig(
            0.2f,
            0.45f,
            1.4f,
            3000,
            32,
            32,
            [0, 0, 0],
            [1, 1, 1]);
        var detectorTensor = TensorImageConverter.CreateDetectorTensor(pixel, detectorConfig);
        var plane = detectorTensor.Width * detectorTensor.Height;
        Check(
            "detector tensor keeps BGR channel order",
            NearlyEqual(detectorTensor.Data[0], 30 / 255f) &&
            NearlyEqual(detectorTensor.Data[plane], 20 / 255f) &&
            NearlyEqual(detectorTensor.Data[plane * 2], 10 / 255f));

        var recognizerTensor = TensorImageConverter.CreateRecognizerTensor(
            pixel,
            new RecognizerConfig(48, 320, 3200, []));
        var recognizerPlane = recognizerTensor.Width * recognizerTensor.Height;
        Check(
            "recognizer tensor pads unused width with zero",
            NearlyEqual(recognizerTensor.Data[recognizerTensor.Width - 1], 0) &&
            NearlyEqual(recognizerTensor.Data[recognizerPlane + recognizerTensor.Width - 1], 0));
    }

    private static void TestDetectionPostprocessing()
    {
        var map = new float[100];
        for (var y = 3; y <= 5; y++)
        {
            for (var x = 2; x <= 4; x++)
            {
                map[y * 10 + x] = 0.9f;
            }
        }

        var boxes = DetectionPostprocessor.Extract(
            map,
            10,
            10,
            new SKSizeI(100, 100),
            new DetectorConfig(0.2f, 0.45f, 1.4f, 100, 32, 4000, [0, 0, 0], [1, 1, 1]));
        Check(
            "detector postprocess extracts and expands a connected text region",
            boxes.Count == 1 && boxes[0].Bounds.Contains(new SKPointI(25, 35)));
    }

    private static void TestCtcDecoding()
    {
        const int timeSteps = 5;
        const int classCount = 3;
        var probabilities = new float[timeSteps * classCount];
        SetClass(probabilities, 0, classCount, 1, 0.9f);
        SetClass(probabilities, 1, classCount, 1, 0.8f);
        SetClass(probabilities, 2, classCount, 0, 0.95f);
        SetClass(probabilities, 3, classCount, 1, 0.85f);
        SetClass(probabilities, 4, classCount, 2, 0.9f);

        var decoded = CtcDecoder.Decode(probabilities, timeSteps, classCount, ["A", "文"]);
        Check(
            "CTC decoder removes repeats and blank while preserving Unicode",
            decoded.Text == "AA文" && NearlyEqual(decoded.Confidence, (0.9f + 0.85f + 0.9f) / 3));
    }

    private static void TestReadingOrder()
    {
        var sorted = ReadingOrder.Sort([
            new OcrLine(new SKRectI(80, 0, 100, 20), "right", 1),
            new OcrLine(new SKRectI(0, 40, 20, 60), "bottom", 1),
            new OcrLine(new SKRectI(0, 2, 20, 22), "left", 1),
        ]);
        Check(
            "OCR lines sort by row then horizontal position",
            sorted.Select(line => line.Text).SequenceEqual(["left", "right", "bottom"]));
    }

    private static void TestSourceTextNormalization()
    {
        var normalized = SourceTextNormalizer.Normalize("  这是\nOCR\t结果123测试ABC中文  ");
        Check(
            "source normalization collapses whitespace and separates CJK from ASCII words",
            normalized == "这是 OCR 结果 123 测试 ABC 中文");

        Check(
            "source normalization removes whitespace around CJK punctuation",
            SourceTextNormalizer.Normalize("你好\n， 世界！") == "你好，世界！");

        Check(
            "source normalization removes whitespace between adjacent CJK characters",
            SourceTextNormalizer.Normalize("你 好\n世 界") == "你好世界");

        Check(
            "source normalization preserves already normalized text",
            SourceTextNormalizer.Normalize("A 1 中文") == "A 1 中文");
    }

    private static void TestOcrHttpServer()
    {
        using var server = new OcrHttpServer("127.0.0.1:0", bmp => new OcrResult([
            new OcrLine(new SKRectI(10, 20, 40, 60), "mock_http_text", 0.99f)
        ], TimeSpan.FromMilliseconds(42)));

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var healthResp = client.GetAsync("/health").GetAwaiter().GetResult();
        var healthJson = healthResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("OcrHttpServer health endpoint returns 200 ok", healthResp.IsSuccessStatusCode && healthJson.Contains("\"status\":\"ok\""));

        using var dummy = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pngBytes = PngEncoder.Encode(dummy);
        var postContent = new ByteArrayContent(pngBytes);
        var postResp = client.PostAsync("/ocr", postContent).GetAwaiter().GetResult();
        var postJson = postResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("OcrHttpServer recognizes raw image binary", postResp.IsSuccessStatusCode && postJson.Contains("mock_http_text") && postJson.Contains("\"elapsed_ms\""));

        var tempFile = Path.Combine(Path.GetTempPath(), $"nanikore_test_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tempFile, pngBytes);
            var queryResp = client.PostAsync($"/ocr?path={Uri.EscapeDataString(tempFile)}", null).GetAwaiter().GetResult();
            var queryJson = queryResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check("OcrHttpServer recognizes image via path query", queryResp.IsSuccessStatusCode && queryJson.Contains("mock_http_text"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }

        var notFoundResp = client.GetAsync("/nonexistent").GetAwaiter().GetResult();
        Check("OcrHttpServer returns 404 for unknown route", notFoundResp.StatusCode == HttpStatusCode.NotFound);
    }

    private static void SetClass(float[] probabilities, int time, int classCount, int character, float value) =>
        probabilities[time * classCount + character] = value;

    private static bool NearlyEqual(float left, float right) => MathF.Abs(left - right) < 0.0001f;
}
