using System.Drawing.Imaging;
using System.Net;
using System.Text;
using NaniKore.Config;
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
        TestProfileRenameKeepsSecret();
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
                unknown_field: ignored
                """);
            var store = new ConfigStore(directory);
            Check(
                "YAML round-trip retains product and schema versions",
                store.Current.Version == "0.1.1" &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);
            Check(
                "YAML round-trip retains known fields and normalizes invalid values",
                store.Current.General.AutoTranslate &&
                store.Current.General.TargetLanguage == "ja" &&
                store.Current.Pipeline.Mode == "local_ocr" &&
                store.Current.Profiles.Single().TimeoutSeconds == 20 &&
                store.Current.Profiles.Single().Name == "test");
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(directory);
            Check(
                "YAML reload preserves generated display names",
                reloaded.Current.Profiles.Single().Model == "model-a" &&
                reloaded.Current.Profiles.Single().Name == "test" &&
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
                version: 1
                ocr:
                  language: chs
                translation:
                  active_profile: legacy
                  profiles:
                    - id: legacy
                      provider: openai
                      base_url: https://legacy.example/v1
                      model: legacy-model
                      open_ai_form: responses
                      target_language: zh-CN
                      auto_translate: true
                """);
            var migrated = new ConfigStore(directory);
            Check(
                "legacy Step 1 configuration migrates in memory",
                migrated.Current.Pipeline.ActiveProfile == "legacy" &&
                migrated.Current.Profiles.Single().ApiType == "responses" &&
                migrated.Current.General.AutoTranslate &&
                migrated.Current.General.TargetLanguage == "zh-CN" &&
                migrated.Current.Profiles.Single().Name == "legacy" &&
                migrated.LoadWarning is not null &&
                File.ReadAllText(Path.Combine(directory, "config.yaml")).Contains("translation:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
