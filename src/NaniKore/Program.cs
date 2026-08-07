using NaniKore.App;
using NaniKore.Diagnostics;
using NaniKore.Ocr;

namespace NaniKore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 必须在创建任何 HWND 前启用 Per-Monitor V2，确保多屏与混合 DPI 下使用物理像素坐标。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        if (args.Contains("--inspect-models", StringComparer.OrdinalIgnoreCase))
        {
            return InspectModels();
        }

        var ocrFileIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--ocr-file", StringComparison.OrdinalIgnoreCase));
        if (ocrFileIndex >= 0)
        {
            return RunFileOcr(args, ocrFileIndex);
        }

        using var context = new NaniKoreApplicationContext();
        Application.Run(context);
        return 0;
    }

    private static int InspectModels()
    {
        try
        {
            using var engine = OcrEngine.CreateDefault();
            Console.WriteLine(engine.DescribeModels());
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunFileOcr(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --ocr-file <image-path>");
            return 2;
        }

        try
        {
            var path = Path.GetFullPath(args[optionIndex + 1]);
            using var image = new Bitmap(path);
            using var engine = OcrEngine.CreateDefault();
            var result = engine.Recognize(image);
            foreach (var line in result.Lines)
            {
                Console.WriteLine($"[{line.Confidence:P1}] {line.Bounds}: {line.Text}");
            }

            Console.WriteLine($"OCR completed in {result.Elapsed.TotalMilliseconds:N0} ms; lines: {result.Lines.Count}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}