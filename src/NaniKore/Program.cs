using Avalonia;
using NaniKore.Imaging;
using SkiaSharp;
using System.Text.Json;
using NaniKore.App;
using NaniKore.Config;
using NaniKore.Diagnostics;
using NaniKore.Interop;
using NaniKore.Ocr;
using NaniKore.Phonetics;
using NaniKore.Translation;


namespace NaniKore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 诊断 CLI（自测/探查/词典生成）统一用 UTF-8 控制台，中文与 IPA 输出都可读
        if (args.Length > 0)
        {
            // 作为 WinExe，CLI 交互执行时需主动附加控制台，并重置 Console.Out 保证输出可见
            NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
            try
            {
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                Console.OutputEncoding = utf8;
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            }
            catch (IOException)
            {
            }
        }
        else
        {
            UseUtf8Console();
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase) ||
                          args.Contains("-v", StringComparer.OrdinalIgnoreCase);
            return SelfTest.Run(verbose);
        }

        if (args.Contains("--inspect-models", StringComparer.OrdinalIgnoreCase))
        {
            return InspectModels();
        }

        if (args.Contains("--build-phonetics", StringComparer.OrdinalIgnoreCase))
        {
            return BuildPhonetics(args);
        }

        if (args.Contains("--build-zh-data", StringComparer.OrdinalIgnoreCase))
        {
            return BuildZhPinyinData();
        }

        if (args.Contains("--phonetics-probe", StringComparer.OrdinalIgnoreCase))
        {
            PhoneticsProbe.Run();
            return 0;
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

        if (args.Contains("--dump-prompt", StringComparer.OrdinalIgnoreCase))
        {
            return DumpPrompt(args);
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

        if (args.Contains("--settings-preview", StringComparer.OrdinalIgnoreCase))
        {
            return PreviewSettings();
        }

        // 桌面 GUI 启动：构建 Avalonia 应用并进入经典桌面生命周期
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int PreviewSettings()
    {
        // 待 P4 设置页迁移至 Avalonia 后接回，当前先呈现基座窗口
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<NaniKoreApp>()
            .UsePlatformDetect()

            .LogToTrace();

    /// <summary>CLI 统一建库入口：数据目录不可写时走 Console.Error 并回报失败，由调用方按退出码 1 结束。</summary>
    private static bool TryCreateConfigStore(out ConfigStore store)
    {
        try
        {
            store = new ConfigStore(AppPaths.DataDirectory);
            return true;
        }
        catch (DataDirectoryUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            store = null!;
            return false;
        }
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

    /// <summary>
    /// 开发期注音词典生成：CMUdict → 英语 SQLite；pypinyin + CC-CEDICT + Unihan → 中文拼音数据表。
    /// 用法：dotnet run --project src/NaniKore -- --build-phonetics（或单独 --build-zh-data 只重建中文）
    /// 详见 codestable/notes/phonetics-pipeline.md；产物落盘 deps/phonetics/，控制台打印 SHA-256 供回填清单。
    /// </summary>
    private static int BuildPhonetics(string[] args)
    {
        try
        {
            var repoRoot = LocateRepositoryRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("未找到仓库根目录（含 deps/phonetics/_source 的目录）。");
                return 1;
            }

            var dictIndexIndex = FindOption(args, "--dict-index");
            var dictIndexPath = dictIndexIndex >= 0 && dictIndexIndex + 1 < args.Length ? args[dictIndexIndex + 1] : null;
            PhoneticDictionaryBuilder.Run(repoRoot, dictIndexPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"注音词典生成失败：{exception.Message}");
            return 1;
        }
    }

    /// <summary>单独重建中文拼音运行期数据：从 resources/phonetics 合并源料，不依赖 mecab-dict-index。</summary>
    private static int BuildZhPinyinData()
    {
        try
        {
            var repoRoot = LocateResourceRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("未找到仓库根目录（需存在 resources/phonetics/pypinyin）");
                return 1;
            }

            NaniKore.Phonetics.ChinesePinyinDataBuilder.Build(repoRoot);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"中文拼音数据构建失败：{exception.Message}");
            return 1;
        }
    }

    /// <summary>从输出目录或当前目录向上找含 resources/phonetics/pypinyin 的仓库根。</summary>
    private static string? LocateResourceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "resources", "phonetics", "pypinyin")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>把标准输出切成 UTF-8：诊断输出含中文与 IPA，默认 OEM 代码页会花屏。</summary>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // 输出被重定向且不支持切换编码时保持默认，不影响日志流
        }
    }

    /// <summary>从输出目录向上找仓库根：以 deps/phonetics/_source 存在为锚。</summary>
    private static string? LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "deps", "phonetics", "_source")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static int RunFileOcr(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --ocr-file <image-path> [--json]");
            return 2;
        }

        try
        {
            var imagePath = Path.GetFullPath(args[optionIndex + 1]);
            using var image = SKBitmap.Decode(imagePath);
            if (image is null)
            {
                Console.Error.WriteLine($"Failed to decode image from {imagePath}");
                return 1;
            }
            using var engine = OcrEngine.CreateDefault();
            var result = engine.Recognize(image);
            if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
            {
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(result.ToDto(), NaniKoreJsonSerializerContext.Default.OcrResponseDto);
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(jsonBytes));
                return 0;
            }
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
            if (!TryCreateConfigStore(out var store))
            {
                return 1;
            }

            var config = store.Current;
            Console.WriteLine($"Config version: {config.Version}");
            Console.WriteLine($"Config source: {store.ConfigPath}");
            Console.WriteLine($"Active profile: {config.Pipeline.ActiveProfile}");
            Console.WriteLine($"Pipeline mode: {config.Pipeline.Mode}");
            Console.WriteLine($"Auto translate: {config.General.AutoTranslate}");
            Console.WriteLine();
            var secrets = new SecretStore();
            foreach (var profile in config.Profiles)
            {
                Console.WriteLine($"  [{profile.Name}]");
                Console.WriteLine($"    kind:            {profile.Kind}");
                Console.WriteLine($"    api_type:        {profile.ApiType}");
                Console.WriteLine($"    model:           {profile.Model}");
                Console.WriteLine($"    base_url:        {profile.BaseUrl}");
                Console.WriteLine($"    supports_vision: {profile.SupportsVision}");
                Console.WriteLine($"    api_key:         {(secrets.Exists(profile.Name) ? "present" : "missing")}");
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

    /// <summary>
    /// 提示词真值转储：按 ConfigStore 的真实配置复现本次请求最终下发的 system prompt，并拆出
    /// 带来源标注的组装段。prompt-preview.html 的内嵌组装数据由本命令的 --json 输出产生，
    /// 因此这里复用 PromptTemplates.DescribeSections / GetSystemPrompt，绝不另写一套拼装逻辑——
    /// 否则前端展示与真实下发会漂移。全程不触碰任何 API Key。
    /// </summary>
    private static int DumpPrompt(IReadOnlyList<string> args)
    {
        const string usage =
            "Usage: NaniKore --dump-prompt [--text \"...\"] [--image <png-path>] [--kind translate|explain] [--target <language>] [--json]";

        var kindIndex = FindOption(args, "--kind");
        var kind = UnderstandKind.Translate;
        if (kindIndex >= 0)
        {
            var rawKind = kindIndex + 1 < args.Count ? args[kindIndex + 1] : null;
            var parsed = ParseDumpKind(rawKind);
            if (parsed is null)
            {
                Console.Error.WriteLine($"Unrecognized --kind value: {rawKind ?? "(missing)"} (expected translate or explain)");
                Console.Error.WriteLine(usage);
                return 2;
            }

            kind = parsed.Value;
        }

        byte[]? imagePng = null;
        var imagePath = GetOptionValue(args, "--image");
        if (imagePath is { Length: > 0 } && !TryReadPng(imagePath, out imagePng))
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        var sourceText = GetOptionValue(args, "--text");
        var targetOverride = GetOptionValue(args, "--target");

        try
        {
            if (!TryCreateConfigStore(out var store))
            {
                return 1;
            }

            var prompts = store.Current.Prompts;
            var targetLanguage = string.IsNullOrWhiteSpace(targetOverride)
                ? store.Current.General.TargetLanguage
                : targetOverride;
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                Console.Error.WriteLine("No target language is configured; pass --target explicitly.");
                Console.Error.WriteLine(usage);
                return 2;
            }

            var request = new UnderstandRequest(
                kind,
                sourceText,
                imagePng is { Length: > 0 } ? new ReadOnlyMemory<byte>(imagePng) : null,
                targetLanguage);
            var path = PromptTemplates.ResolvePath(request);
            // 探测结果自 issue 008 Phase 3 起仅为报告信息（prompt 组装不再按探测注入语言插件）；
            // 视觉链路无源文本可扫，按空串探测得到全空特征，如实反映「无从预扫」。
            var detection = LanguageDetector.Detect(
                path == PromptPath.Vision ? string.Empty : request.SourceText,
                request.TargetLanguage);
            var sections = PromptTemplates.DescribeSections(request, prompts);
            var systemPrompt = PromptTemplates.GetSystemPrompt(request, prompts);

            // 字段名即 prompt-preview 页面（经 serve-preview.ps1 代理 /api/dump）的消费契约，改名需同步页面与中转服务。
            var report = new PromptDumpReportDto(
                kind == UnderstandKind.Explain ? "explain" : "translate",
                DescribePromptPath(path),
                targetLanguage,
                PreviewText(sourceText, string.Empty),
                new PromptDumpDetectionDto(
                    DescribeFeatures(detection),
                    detection.NeedsJapaneseRuby,
                    detection.NeedsEnglishIpa,
                    detection.IsPureHanziJapaneseCandidate),
                sections.Select(s => new PromptDumpSectionDto(s.Name, DescribeSectionSource(s.Source), s.Text)).ToArray(),
                systemPrompt,
                TokenEstimator.Estimate(systemPrompt));

            var json = JsonSerializer.Serialize(report, NaniKoreJsonSerializerContext.Default.PromptDumpReportDto);
            if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
            {
                // 机器通道：stdout 只留 JSON，供生成器 ConvertFrom-Json 直接消费。
                Console.WriteLine(json);

                return 0;
            }

            RenderPromptDump(path, kind, targetLanguage, sourceText, imagePath, detection, sections, systemPrompt, json);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Prompt dump failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>解析 --kind；缺省 translate，非法值返回 null 由调用方按用法错误退出。</summary>
    private static UnderstandKind? ParseDumpKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "translate" => UnderstandKind.Translate,
        "explain" => UnderstandKind.Explain,
        _ => null,
    };

    /// <summary>读取 PNG 字节并校验文件头；转储只搬运字节、不解码位图，故自行做最轻量签名校验，避免误传的文本文件得出漂亮的假结果。</summary>
    private static bool TryReadPng(string path, out byte[]? png)
    {
        png = null;
        try
        {
            var bytes = File.ReadAllBytes(Path.GetFullPath(path));
            if (bytes.Length < PngSignature.Length || !bytes.AsSpan()[..PngSignature.Length].SequenceEqual(PngSignature))
            {
                Console.Error.WriteLine($"Not a valid PNG file: {path}");
                return false;
            }

            png = bytes;
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Image load failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>人读渲染：与 --json 共用同一批局部变量，避免出现两份互相矛盾的真相。</summary>
    private static void RenderPromptDump(
        PromptPath path,
        UnderstandKind kind,
        string targetLanguage,
        string? sourceText,
        string? imagePath,
        LanguageDetectionResult detection,
        IReadOnlyList<PromptSection> sections,
        string systemPrompt,
        string json)
    {
        Console.WriteLine("提示词真值转储（与 PromptTemplates.GetSystemPrompt 逐字一致）");
        Console.WriteLine($"  Path:          {DescribePromptPath(path)}");
        Console.WriteLine($"  Kind:          {(kind == UnderstandKind.Explain ? "explain" : "translate")}");
        Console.WriteLine($"  Target:        {targetLanguage}");
        Console.WriteLine($"  Source:        {PreviewText(sourceText, "(empty)")}");
        if (imagePath is { Length: > 0 })
        {
            Console.WriteLine($"  Image:         {imagePath}");
        }

        var features = DescribeFeatures(detection);
        Console.WriteLine($"  Detection:     {(features.Length == 0 ? "none" : string.Join(" + ", features))}");
        Console.WriteLine($"    japanese ruby:      {(detection.NeedsJapaneseRuby ? "yes" : "no")}");
        Console.WriteLine($"    english IPA:        {(detection.NeedsEnglishIpa ? "yes" : "no")}");
        Console.WriteLine($"    pure-hanzi ja cand.: {(detection.IsPureHanziJapaneseCandidate ? "yes" : "no")}");

        Console.WriteLine();
        Console.WriteLine($"  Sections ({sections.Count}, in dispatch order):");
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            Console.WriteLine($"    [{index + 1}] {section.Name,-16} {DescribeSectionSource(section.Source),-8} {section.Text.Length} chars");
        }

        Console.WriteLine();
        Console.WriteLine($"Final system prompt (estimated {TokenEstimator.Estimate(systemPrompt)} tokens):");
        Console.WriteLine(systemPrompt);
        Console.WriteLine();
        Console.WriteLine("Reproduce with:");
        Console.WriteLine($"  NaniKore.exe --dump-prompt {BuildReproArguments(kind, sourceText, imagePath, targetLanguage)}");
        Console.WriteLine();
        Console.WriteLine("JSON (exactly what the preview generator consumes):");
        Console.WriteLine(json);
    }

    private static string BuildReproArguments(UnderstandKind kind, string? sourceText, string? imagePath, string targetLanguage) => string.Join(" ", new[]
    {
        kind == UnderstandKind.Explain ? "--kind explain" : string.Empty,
        imagePath is { Length: > 0 } ? $"--image \"{imagePath}\"" : $"--text \"{PreviewText(sourceText, string.Empty)}\"",
        $"--target \"{targetLanguage}\"",
        "--json",
    }.Where(argument => argument.Length > 0));

    private static string DescribePromptPath(PromptPath path) => path switch
    {
        PromptPath.Vision => "vision",
        PromptPath.Explain => "explain",
        _ => "text",
    };

    private static string DescribeSectionSource(PromptSectionSource source) => source switch
    {
        PromptSectionSource.Custom => "custom",
        PromptSectionSource.Session => "session",
        _ => "builtin",
    };

    /// <summary>按枚举声明顺序列出命中的语言特征位，便于与预览页的探测位图逐项对齐。</summary>
    private static string[] DescribeFeatures(LanguageDetectionResult detection)
    {
        var features = new List<string>(3);
        if (detection.HasJapanese)
        {
            features.Add(nameof(LanguageFeatures.Japanese));
        }

        if (detection.HasEnglish)
        {
            features.Add(nameof(LanguageFeatures.English));
        }

        if (detection.HasSimplifiedChinese)
        {
            features.Add(nameof(LanguageFeatures.SimplifiedChinese));
        }

        return features.ToArray();
    }

    /// <summary>转储只展示原文片段，避免超长选区文本把人读输出与内嵌 JSON 撑爆。</summary>
    private static string PreviewText(string? text, string emptyText, int limit = 120)
    {
        if (string.IsNullOrEmpty(text))
        {
            return emptyText;
        }

        return text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit), "...");
    }

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', (byte)'\r', (byte)'\n', 0x1A, (byte)'\n'];

    private static async Task<int> RunTranslateTextAsync(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --translate-text <text> [--profile <profile-name>]");
            return 2;
        }

        return await RunTranslationAsync(args, args[optionIndex + 1], null).ConfigureAwait(false);
    }

    private static async Task<int> RunTranslateImageAsync(IReadOnlyList<string> args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Count)
        {
            Console.Error.WriteLine("Usage: NaniKore --translate-image <image-path> [--profile <profile-name>]");
            return 2;
        }

        try
        {
            var imagePath = Path.GetFullPath(args[optionIndex + 1]);
            using var image = SKBitmap.Decode(imagePath);
            if (image is null)
            {
                Console.Error.WriteLine($"Failed to decode image from {imagePath}");
                return 1;
            }

            var pngBytes = PngEncoder.Encode(image);
            return await RunTranslationAsync(args, string.Empty, pngBytes).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Image load failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTranslationAsync(IReadOnlyList<string> args, string sourceText, byte[]? imagePng)
    {
        if (!TryCreateConfigStore(out var store))
        {
            return 1;
        }

        var profileName = GetOptionValue(args, "--profile") ?? store.Current.Pipeline.ActiveProfile;
        try
        {
            var profile = store.GetProfile(profileName);
            using var factory = new ProviderFactory(store.Current);
            ReadOnlyMemory<byte>? imagePayload = null;
            if (imagePng is not null)
            {
                imagePayload = new ReadOnlyMemory<byte>(imagePng);
            }

            var request = new UnderstandRequest(UnderstandKind.Translate, sourceText, imagePayload, store.Current.General.TargetLanguage);
            var provider = factory.GetProvider(profileName, request);
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
            var profile = store.Current.Profiles.FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
            Console.Error.WriteLine(ProviderErrorMapper.ToUserMessage(exception, profileName, profile?.Model, profile?.BaseUrl));
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
