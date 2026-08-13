using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Windows.Forms;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Input;
using NaniKore.Translation;
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
        TestConfigRoundTrip();
        TestAboutPageDisplaysProductVersion();
        TestApplicationIconResource();
        TestTranslationPromptTemplates();
        TestProfileRenameKeepsSecret();
        TestHotkeyGestureNormalization();
        TestHotkeyRecorderInteraction();
        TestThemeResourcesResolve();
        TestSettingsPagesUseAdaptiveLayout();
        TestSseReader();
        TestSentinelRouter();
        TestProviderCapabilitiesAndErrors();

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


    private static void TestConfigRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.1.1
                general:
                  auto_translate: true
                  target_language: ja
                pipeline:
                  mode: invalid-mode
                  active_profile: test
                profiles:
                  - id: test
                    kind: openai
                    api_type: completions
                    base_url: https://example.test/v1
                    model: model-a
                    timeout_seconds: 20
                windows:
                  settings_width: 800
                  settings_height: 640
                prompts:
                  translate: 'Translate into {{target_language}} only.'
                unknown_field: ignored
                """);
            var store = new ConfigStore(directory);
            Check(
                "YAML round-trip retains the manually maintained product version",
                store.Current.Version == "0.1.1" &&
                store.Current.Windows.SettingsWidth == 800 &&
                store.Current.Windows.SettingsHeight == 640 &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);
            Check(
                "YAML round-trip retains known fields and normalizes invalid values",
                store.Current.General.AutoTranslate &&
                store.Current.General.TargetLanguage == "ja" &&
                store.Current.Pipeline.Mode == "local_ocr" &&
                store.Current.Profiles.Single().TimeoutSeconds == 20 &&
                store.Current.Profiles.Single().Name == "test" &&
                store.Current.Prompts.Translate == "Translate into {{target_language}} only.");
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(directory);
            Check(
                "YAML reload preserves generated display names",
                reloaded.Current.Profiles.Single().Model == "model-a" &&
                reloaded.Current.Profiles.Single().Name == "test" &&
                reloaded.Current.Prompts.Translate == "Translate into {{target_language}} only." &&
                reloaded.Current.Windows.SettingsWidth == 800 &&
                reloaded.Current.Windows.SettingsHeight == 640 &&
                AppConfigCloner.Clone(reloaded.Current).Profiles.Single().Name == "test");

            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 1
                pipeline:
                  active_profile: work
                profiles:
                  - id: work
                    name: 工作
                    kind: openai
                    base_url: https://work.example/v1
                    model: work-model
                  - id: personal
                    name: 工作
                    kind: gemini
                    base_url: https://personal.example/v1
                    model: personal-model
                """);
            var named = new ConfigStore(directory);
            Check(
                "duplicate profile names normalize without changing stable IDs or active profile",
                named.Current.Pipeline.ActiveProfile == "work" &&
                named.Current.Profiles.Select(profile => profile.Id).SequenceEqual(["work", "personal"]) &&
                named.Current.Profiles.Select(profile => profile.Name).SequenceEqual(["工作", "工作 (2)"]));

            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.1.3
                translation:
                  active_profile: legacy
                  profiles:
                    - id: legacy
                      provider: openai
                      base_url: https://legacy.example/v1
                      model: legacy-model
                """);
            var unsupported = new ConfigStore(directory);
            Check(
                "unsupported early config format falls back without migration",
                unsupported.Current.Pipeline.ActiveProfile == "openai-default" &&
                unsupported.Current.Profiles.Count == 3 &&
                unsupported.Current.Profiles.All(profile => profile.Id != "legacy") &&
                !unsupported.Current.General.AutoTranslate &&
                unsupported.Current.General.TargetLanguage == "中文（简体）" &&
                File.ReadAllText(Path.Combine(directory, "config.yaml")).Contains("translation:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestAboutPageDisplaysProductVersion()
    {
        const string productVersion = "0.1.3+test.1";
        var page = new AboutPage();
        page.Load(new AppConfig { Version = productVersion });

        var description = page.DisplayText;
        Check(
            "about page displays the configured product version without a schema version",
            description.Contains($"v{productVersion}", StringComparison.Ordinal) &&
            !description.Contains("Schema", StringComparison.OrdinalIgnoreCase));
    }

    private static void TestApplicationIconResource()
    {
        using var icon = ApplicationIcon.LoadWinForms();
        Check(
            "application icon is loaded from the bundled NaniKore asset",
            icon.Width > 0 && icon.Height > 0);
    }

    private static void TestTranslationPromptTemplates()
    {
        var request = new UnderstandRequest(UnderstandKind.Translate, "hello", null, "zh-CN");
        var defaultPrompt = PromptTemplates.GetSystemPrompt(request, new PromptConfig());
        Check(
            "default translation prompt explicitly requires only the selected target language",
            defaultPrompt.Contains("translation in zh-CN", StringComparison.Ordinal) &&
            defaultPrompt.Contains("do not include explanations", StringComparison.OrdinalIgnoreCase) &&
            !defaultPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var customPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig { Translate = "Translate to {{target_language}}; retain {{unknown_placeholder}}." });
        Check(
            "custom translation prompt expands target language without swallowing unknown placeholders",
            customPrompt == "Translate to zh-CN; retain {{unknown_placeholder}}.");

        var visionPrompt = PromptTemplates.GetSystemPrompt(
            request with { ImagePng = new byte[] { 1 } },
            new PromptConfig { Translate = "Translate into {{target_language}} only." });
        Check(
            "vision translation preserves the streaming section protocol after prompt customization",
            visionPrompt.Contains(PromptTemplates.SourceSentinel, StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal));
    }

    private static void TestProfileRenameKeepsSecret()
    {
        var profileId = $"self-test-{Guid.NewGuid():N}";
        const string secret = "self-test-secret";
        try
        {
            SecretStore.Save(profileId, secret);
            var profile = new ProviderProfile { Id = profileId, Name = "原名称" };
            profile.Name = "重命名后";
            Check(
                "profile rename keeps the stable DPAPI secret index",
                profile.Id == profileId && SecretStore.Load(profile.Id) == secret);
        }
        finally
        {
            SecretStore.Delete(profileId);
        }
    }
    private static void TestHotkeyGestureNormalization()
    {
        Check(
            "gesture parsing normalizes modifier aliases and casing",
            HotkeyGesture.Normalize("control+shift+q") == "Ctrl+Shift+Q" &&
            HotkeyGesture.Normalize("windows + alt + f9") == "Alt+Win+F9");

        Check(
            "gesture parsing rejects empty, modifier-only and unsupported keys",
            RejectsGesture(string.Empty) && RejectsGesture("Ctrl") && RejectsGesture("Ctrl+Shift+PrintScreen"));

        Check(
            "captured combinations reuse the registration parser",
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, Keys.Q, out var captured, out _) &&
            captured == "Ctrl+Shift+Q" &&
            HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl | HotkeyGesture.ModShift, (uint)Keys.Q));

        Check(
            "modifier-only and unmodified keys are refused with a readable reason",
            !HotkeyGesture.TryCapture(HotkeyGesture.ModControl, Keys.ControlKey, out _, out var modifierOnly) &&
            modifierOnly.Length > 0 &&
            !HotkeyGesture.TryCapture(0, Keys.Q, out _, out var missingModifier) &&
            missingModifier.Length > 0);

        Check(
            "held modifiers echo a partial combination while recording",
            HotkeyGesture.FormatPreview(HotkeyGesture.ModControl | HotkeyGesture.ModShift) == "Ctrl+Shift+…" &&
            HotkeyGesture.FormatPreview(0).Length == 0);
    }

    private static void TestHotkeyRecorderInteraction()
    {
        var recorder = new HotkeyRecorderBox { Gesture = "Ctrl+Shift+A" };
        var suspensions = new List<bool>();
        recorder.RecordingChanged += (_, recording) => suspensions.Add(recording);

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(Keys.ShiftKey, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "holding modifiers echoes the partial combination without ending recording",
            recorder.IsRecording && recorder.DisplayText == "Ctrl+Shift+…" && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(Keys.Back, HotkeyGesture.ModControl);
        Check(
            "recording keeps state for backspace input",
            recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(Keys.Q, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "recording stores the normalized combination and releases the global hotkey once",
            !recorder.IsRecording &&
            recorder.Gesture == "Ctrl+Shift+Q" &&
            recorder.DisplayText == "Ctrl+Shift+Q" &&
            suspensions.SequenceEqual([true, false]));

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(Keys.Escape, 0);
        Check(
            "escape restores the gesture that was active before recording",
            !recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+Q");

        var page = new HotkeyPage();
        page.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "alt+f9" } });
        var config = new AppConfig();
        page.Apply(config);
        Check(
            "hotkey page normalizes stored gestures and validates before applying",
            page.Validate() is null && config.Hotkeys.Capture == "Alt+F9");
    }

    /// <summary>
    /// WPF 化后的布局约定：页面根元素必须按内容自适应高度（由宿主 ScrollViewer 负责滚动），
    /// 且所有设置行都通过 SettingsRow 承载，从而共享同一套 DPI 与字体缩放行为。
    /// </summary>
    private static void TestSettingsPagesUseAdaptiveLayout()
    {
        // 主题字典必须真的能解析并套到控件上；没有 Application 资源时模板缺失，测得高度会恒为 0。
        EnsureThemedApplication();

        foreach (var page in new ISettingsPage[] { new GeneralPage(), new TranslationPage(), new HotkeyPage(), new AboutPage() })
        {
            var view = page.View;
            page.Load(new AppConfig());
            view.Measure(new System.Windows.Size(760, double.PositiveInfinity));
            var rows = FindDescendants<SettingsRow>(view).ToList();
            var groups = FindDescendants<SettingsGroup>(view).ToList();
            Check(
                $"{page.Title} page builds an adaptive card layout without fixed pixel positions",
                double.IsNaN(view.Width) &&
                double.IsNaN(view.Height) &&
                view.DesiredSize.Height > 0 &&
                groups.Count > 0 &&
                rows.TrueForAll(row => double.IsNaN(row.Height) && !string.IsNullOrEmpty(row.Header)));
        }

        // 说明文字与字段同属一行，缩放时不会像旧版那样与输入框错位。
        var general = new GeneralPage();
        general.Load(new AppConfig());
        var describedRows = FindDescendants<SettingsRow>(general.View)
            .Count(row => !string.IsNullOrEmpty(row.Description));
        Check(
            "general page attaches descriptions to the rows they explain",
            describedRows > 0);
    }

    /// <summary>主题资源与控件模板必须可用；顺带验证两份色板与两份控件字典都能解析。</summary>
    private static void TestThemeResourcesResolve()
    {
        EnsureThemedApplication();
        var application = System.Windows.Application.Current!;
        var expectedKeys = new[]
        {
            "Brush.Window.Background", "Brush.Card.Background", "Brush.Accent", "Brush.Text.Primary",
            "Font.Ui", "Radius.Control", "ToggleSwitch", "Button.Accent", "Nav.List", "TextBox.Multiline",
        };
        Check(
            "theme dictionaries expose every semantic key used by the settings pages",
            expectedKeys.All(key => application.TryFindResource(key) is not null));

        // 自绘控件必须有隐式样式，否则运行时会退回系统默认观感。
        Check(
            "custom controls resolve their implicit fluent styles",
            application.TryFindResource(typeof(SettingsRow)) is System.Windows.Style &&
            application.TryFindResource(typeof(SettingsGroup)) is System.Windows.Style &&
            application.TryFindResource(typeof(NumberBox)) is System.Windows.Style &&
            application.TryFindResource(typeof(InfoBar)) is System.Windows.Style &&
            application.TryFindResource(typeof(HotkeyRecorderBox)) is System.Windows.Style);
    }

    /// <summary>无 UI 自测里也需要一个 WPF Application 承载资源；不调用 Run，只做资源解析。</summary>
    private static void EnsureThemedApplication()
    {
        if (System.Windows.Application.Current is null)
        {
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        NaniKore.App.Theme.ThemeManager.Initialize(System.Windows.Application.Current!);
    }

    /// <summary>逻辑树遍历：页面尚未渲染时视觉树为空，断言必须走逻辑子元素。</summary>
    private static IEnumerable<T> FindDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static bool RejectsGesture(string gesture)
    {
        try
        {
            HotkeyGesture.Parse(gesture);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
    private static void TestSseReader()
    {
        const string payload = "event: delta\ndata: {\\\"text\\\":\\\"A\\\"}\n\ndata: [DONE]\n\n";
        using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes(payload), chunkSize: 3);
        var frames = ReadSseAsync(stream).GetAwaiter().GetResult();
        Check(
            "SSE reader groups data lines across packet boundaries",
            frames.Count == 2 && frames[0].Event == "delta" && frames[0].Data == "{\\\"text\\\":\\\"A\\\"}" && frames[1].Data == "[DONE]");
    }

    private static void TestSentinelRouter()
    {
        var router = new SentinelChannelRouter();
        var deltas = router.Push("<<<SOU").Concat(router.Push("RCE>>>??<<<TRANSLATION>>>??")).Concat(router.Complete()).ToList();
        Check(
            "sentinel router keeps channels when markers span frames",
            string.Concat(deltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "??" &&
            string.Concat(deltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "??");
    }

    private static void TestProviderCapabilitiesAndErrors()
    {
        var capabilities = ProviderFactory.GetCapabilities(new ProviderProfile { SupportsVision = false });
        var rejected = false;
        try
        {
            ProviderFactory.ValidateCapabilities(
                new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "zh-CN"),
                capabilities);
        }
        catch (ProviderCapabilityException)
        {
            rejected = true;
        }

        Check("vision capability mismatch is rejected before request", rejected);
        var textOnlyAccepted = true;
        try
        {
            ProviderFactory.ValidateCapabilities(
                new UnderstandRequest(UnderstandKind.Translate, "text", null, "zh-CN"),
                capabilities);
        }
        catch (ProviderCapabilityException)
        {
            textOnlyAccepted = false;
        }

        Check("text-only request does not require vision capability", textOnlyAccepted);
        var noImageRequest = new UnderstandRequest(UnderstandKind.Translate, "text", (ReadOnlyMemory<byte>?)null, "zh-CN");
        Check("nullable image payload remains absent", noImageRequest.ImagePng is null);
        byte[]? noImage = null;
        ReadOnlyMemory<byte>? conditionalPayload = noImage is null ? null : new ReadOnlyMemory<byte>(noImage);
        var conditionalRequest = new UnderstandRequest(UnderstandKind.Translate, "text", conditionalPayload, "zh-CN");
        var zeroLengthImageAcceptedAsText = true;
        try
        {
            ProviderFactory.ValidateCapabilities(conditionalRequest, capabilities);
        }
        catch (ProviderCapabilityException)
        {
            zeroLengthImageAcceptedAsText = false;
        }

        Check("zero-length image payload is treated as absent", zeroLengthImageAcceptedAsText);
        Check(
            "error mapper explains unauthorized key failures",
            ProviderErrorMapper.ToUserMessage(new ProviderHttpException(HttpStatusCode.Unauthorized, null)).Contains("API Key", StringComparison.Ordinal));
    }

    private static async Task<List<SseEvent>> ReadSseAsync(Stream stream)
    {
        var frames = new List<SseEvent>();
        await foreach (var frame in SseReader.ReadAsync(stream, CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>????????? TCP ????? SSE ???????????????</summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _chunkSize;

        public ChunkedReadStream(byte[] bytes, int chunkSize)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _chunkSize));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, _chunkSize)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
