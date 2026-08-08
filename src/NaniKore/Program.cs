using System.Drawing.Imaging;
using NaniKore.App;
using NaniKore.Config;
using NaniKore.Diagnostics;
using NaniKore.Ocr;
using NaniKore.Translation;
using System.Windows.Forms;

namespace NaniKore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        if (args.Contains("--inspect-models", StringComparer.OrdinalIgnoreCase))
        {
            return InspectModels();
        }

        var ocrFileIndex = FindOption(args, "--ocr-file");
        if (ocrFileIndex >= 0)
        {
            return RunFileOcr(args, ocrFileIndex);
        }

        if (args.Contains("--dump-config", StringComparer.OrdinalIgnoreCase))
        {
            return DumpConfig();
        }

        var translateTextIndex = FindOption(args, "--translate-text");
        if (translateTextIndex >= 0)
        {
            return RunTranslateTextAsync(args, translateTextIndex).GetAwaiter().GetResult();
        }

        var translateImageIndex = FindOption(args, "--translate-image");
        if (translateImageIndex >= 0)
        {
            return RunTranslateImageAsync(args, translateImageIndex).GetAwaiter().GetResult();
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        return new NaniKoreApp().Run();
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
            using var image = new Bitmap(Path.GetFullPath(args[optionIndex + 1]));
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
            var store = new ConfigStore(AppContext.BaseDirectory);
            var config = store.Current;
            Console.WriteLine($"Config version: {config.Version}");
            Console.WriteLine($"Config source: {store.ConfigPath}");
            Console.WriteLine($"Active profile: {config.Pipeline.ActiveProfile}");
            Console.WriteLine($"Pipeline mode: {config.Pipeline.Mode}");
            Console.WriteLine($"Auto translate: {config.General.AutoTranslate}");
            Console.WriteLine();
            foreach (var profile in config.Profiles)
            {
                Console.WriteLine($"  [{profile.Id}]");
                Console.WriteLine($"    kind:            {profile.Kind}");
                Console.WriteLine($"    api_type:        {profile.ApiType}");
                Console.WriteLine($"    model:           {profile.Model}");
                Console.WriteLine($"    base_url:        {profile.BaseUrl}");
                Console.WriteLine($"    supports_vision: {profile.SupportsVision}");
                Console.WriteLine($"    api_key:         {(SecretStore.Exists(profile.Id) ? "present" : "missing")}");
            }

            if (!string.IsNullOrWhiteSpace(store.LoadWarning))
            {
                Console.WriteLine();
                Console.WriteLine($"Warning: {store.LoadWarning}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Config dump failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTranslateTextAsync(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --translate-text <text> [--profile <profile-id>]");
            return 2;
        }

        return await RunTranslationAsync(args, args[optionIndex + 1], null).ConfigureAwait(false);
    }

    private static async Task<int> RunTranslateImageAsync(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --translate-image <image-path> [--profile <profile-id>]");
            return 2;
        }

        try
        {
            using var image = new Bitmap(Path.GetFullPath(args[optionIndex + 1]));
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return await RunTranslationAsync(args, string.Empty, stream.ToArray()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Image load failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTranslationAsync(IReadOnlyList<string> args, string sourceText, byte[]? imagePng)
    {
        var store = new ConfigStore(AppContext.BaseDirectory);
        var profileId = GetOptionValue(args, "--profile") ?? store.Current.Pipeline.ActiveProfile;
        try
        {
            var profile = store.GetProfile(profileId);
            using var factory = new ProviderFactory(store.Current);
            ReadOnlyMemory<byte>? imagePayload = null;
            if (imagePng is not null)
            {
                imagePayload = new ReadOnlyMemory<byte>(imagePng);
            }

            var request = new UnderstandRequest(UnderstandKind.Translate, sourceText, imagePayload, store.Current.General.TargetLanguage);
            var provider = factory.GetProvider(profileId, request);
            await foreach (var delta in provider.StreamAsync(request, CancellationToken.None).ConfigureAwait(false))
            {
                if (delta.Channel == DeltaChannel.Translation)
                {
                    Console.Write(delta.Text);
                }
            }

            Console.WriteLine();
            return 0;
        }
        catch (Exception exception)
        {
            var profile = store.Current.Profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            Console.Error.WriteLine(ProviderErrorMapper.ToUserMessage(exception, profileId, profile?.Model, profile?.BaseUrl));
            return 1;
        }
    }

    private static int FindOption(IReadOnlyList<string> args, string option) => Array.FindIndex(args.ToArray(), argument =>
        string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));

    private static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        var index = FindOption(args, option);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }
}
