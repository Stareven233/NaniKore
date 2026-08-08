using System.Windows;
using NaniKore.App;
using NaniKore.Config;
using NaniKore.Diagnostics;
using NaniKore.Ocr;
using Application = System.Windows.Forms.Application;

namespace NaniKore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        if (args.Contains("--dump-config", StringComparer.OrdinalIgnoreCase))
            return DumpConfig();

        var app = new NaniKoreApp();
        return app.Run();
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

    private static int DumpConfig()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var store = new ConfigStore(dir);
            var c = store.Current;
            Console.WriteLine($"Config version: {c.Version}");
            Console.WriteLine($"Config source: {dir}");
            Console.WriteLine();
            Console.WriteLine($"Active profile: {c.Translation.ActiveProfile}");
            Console.WriteLine();
            foreach (var p in c.Translation.Profiles)
            {
                var hasKey = SecretStore.Exists(p.Id);
                Console.WriteLine($"  [{p.Id}]");
                Console.WriteLine($"    provider:    {p.Provider}");
                Console.WriteLine($"    model:       {p.Model}");
                Console.WriteLine($"    base_url:    {p.BaseUrl}");
                Console.WriteLine($"    api_key:     {(hasKey ? "present" : "missing")}");
                Console.WriteLine($"    target_lang: {p.TargetLanguage}");
                Console.WriteLine();
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"Config dump failed: {e.Message}"); return 1; }
    }
}
