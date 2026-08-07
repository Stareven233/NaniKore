using System.Drawing.Imaging;
using NaniKore.Ocr;
using NaniKore.Ocr.Detection;
using NaniKore.Ocr.Imaging;
using NaniKore.Ocr.Models;
using NaniKore.Ocr.Recognition;
using NaniKore.Overlay;

namespace NaniKore.Diagnostics;

internal static class SelfTest
{
    private static int _failed;

    public static int Run()
    {
        _failed = 0;
        TestSelectionGeometry();
        TestImageTensorConversion();
        TestDetectionPostprocessing();
        TestCtcDecoding();
        TestReadingOrder();

        Console.WriteLine(_failed == 0
            ? "All self-tests passed."
            : $"Self-tests failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestSelectionGeometry()
    {
        Check(
            "normalizes reverse drag",
            SelectionGeometry.Normalize(new Point(90, 70), new Point(10, 20), new Size(100, 80)) ==
            Rectangle.FromLTRB(10, 20, 90, 70));

        Check(
            "clamps selection to bitmap bounds",
            SelectionGeometry.Normalize(new Point(-20, 10), new Point(130, 90), new Size(100, 80)) ==
            Rectangle.FromLTRB(0, 10, 100, 80));

        var outer = new Rectangle(0, 0, 100, 80);
        var selection = new Rectangle(10, 20, 30, 25);
        var outsideArea = SelectionGeometry.GetOutsideRegions(outer, selection)
            .Sum(region => region.Width * region.Height);
        Check(
            "outside dim regions do not overlap the selection",
            outsideArea == outer.Width * outer.Height - selection.Width * selection.Height);

        using var source = new Bitmap(10, 10, PixelFormat.Format32bppPArgb);
        using var cropped = source.Clone(new Rectangle(2, 3, 4, 5), PixelFormat.Format32bppPArgb);
        Check("bitmap crop keeps requested dimensions", cropped.Size == new Size(4, 5));
    }

    private static void TestImageTensorConversion()
    {
        Check(
            "detector resize aligns dimensions to 32",
            TensorImageConverter.ComputeDetectorSize(new Size(100, 50), 736, 4000) == new Size(1472, 736));

        using var pixel = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        pixel.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30));
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
            new Size(100, 100),
            new DetectorConfig(0.2f, 0.45f, 1.4f, 100, 32, 4000, [0, 0, 0], [1, 1, 1]));
        Check(
            "detector postprocess extracts and expands a connected text region",
            boxes.Count == 1 && boxes[0].Bounds.Contains(new Rectangle(20, 30, 30, 30)));
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
            new OcrLine(new Rectangle(80, 0, 20, 20), "right", 1),
            new OcrLine(new Rectangle(0, 40, 20, 20), "bottom", 1),
            new OcrLine(new Rectangle(0, 2, 20, 20), "left", 1),
        ]);
        Check(
            "OCR lines sort by row then horizontal position",
            sorted.Select(line => line.Text).SequenceEqual(["left", "right", "bottom"]));
    }

    private static void SetClass(float[] probabilities, int time, int classCount, int character, float value) =>
        probabilities[time * classCount + character] = value;

    private static bool NearlyEqual(float left, float right) => MathF.Abs(left - right) < 0.0001f;

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            Console.WriteLine($"PASS: {name}");
            return;
        }

        _failed++;
        Console.Error.WriteLine($"FAIL: {name}");
    }
}