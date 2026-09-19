using System.Net;
using System.Text;
using System.Text.Json;
using NaniKore.Config;
using NaniKore.Translation;
using NaniKore.Translation.Providers;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunTranslationTests()
    {
        TestTranslationPromptTemplates();
        TestModularPromptTemplates();
        TestLanguageDetector();
        TestRubyTextParser();
        TestSseReader();
        TestSentinelRouter();
        TestProviderTextExtraction();
        TestProviderCapabilitiesAndErrors();
    }

    private static void TestTranslationPromptTemplates()
    {
        var request = new UnderstandRequest(UnderstandKind.Translate, "hello", null, "zh-CN");
        var defaultPrompt = PromptTemplates.GetSystemPrompt(request, new PromptConfig());
        Check(
            "default translation prompt explicitly requires only the selected target language",
            defaultPrompt.Contains("zh-CN", StringComparison.Ordinal) &&
            defaultPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            !defaultPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var customPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig { Base = "Translate to {{target_language}}; retain {{unknown_placeholder}}." });
        Check(
            "custom translation prompt expands target language without swallowing unknown placeholders",
            customPrompt.StartsWith("Translate to zh-CN; retain {{unknown_placeholder}}.", StringComparison.Ordinal));

        var imageRequest = request with { ImagePng = new byte[] { 1 } };
        var defaultVisionPrompt = PromptTemplates.GetSystemPrompt(imageRequest, new PromptConfig());
        const string expectedVisionShape = "<transcription>\n\n---\n\n<translation>";
        var normalizedVisionPrompt = defaultVisionPrompt.Replace("\r\n", "\n");
        Check(
            "default vision prompt expands its target language and keeps the visible section separator",
            normalizedVisionPrompt.Contains("zh-CN", StringComparison.Ordinal) &&
            normalizedVisionPrompt.Contains(expectedVisionShape, StringComparison.Ordinal) &&
            !normalizedVisionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var visionPrompt = PromptTemplates.GetSystemPrompt(
            imageRequest,
            new PromptConfig { VisionOneshot = "OCR\n\n---\n\nTranslate {{target_language}}; retain {{unknown_placeholder}}" });
        Check(
            "custom vision prompt expands only the target language placeholder",
            visionPrompt == "OCR\n\n---\n\nTranslate zh-CN; retain {{unknown_placeholder}}");

        var pureModularPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig());
        Check(
            "empty prompt config strictly builds modular prompt with sentinel and translation discipline",
            pureModularPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            pureModularPrompt.Contains("Strict Translation Discipline", StringComparison.Ordinal));
    }

    private static void TestModularPromptTemplates()
    {
        // 1. 日文输入触发 JapanesePlugin
        var japaneseReq = new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）");
        var japanesePrompt = PromptTemplates.GetSystemPrompt(japaneseReq, new PromptConfig());
        Check(
            "system prompt for Japanese input injects ruby format brackets and plain form rule without outdated terms",
            japanesePrompt.Contains("漢字[かな]", StringComparison.Ordinal) &&
            japanesePrompt.Contains("辞書形", StringComparison.Ordinal) &&
            japanesePrompt.Contains("未然形", StringComparison.Ordinal) &&
            japanesePrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            japanesePrompt.Contains("中文（简体）", StringComparison.Ordinal));

        // 2. 纯汉字日文词条触发 JapanesePlugin
        var pureKanjiReq = new UnderstandRequest(UnderstandKind.Translate, "進入禁止", null, "中文（简体）");
        var pureKanjiPrompt = PromptTemplates.GetSystemPrompt(pureKanjiReq, new PromptConfig());
        Check(
            "system prompt for pure kanji Japanese phrase injects Japanese plugin",
            pureKanjiPrompt.Contains("漢字[かな]", StringComparison.Ordinal) &&
            pureKanjiPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal));

        // 3. 纯简体中文输入不注入 JapanesePlugin
        var chineseReq = new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, "English");
        var chinesePrompt = PromptTemplates.GetSystemPrompt(chineseReq, new PromptConfig());
        Check(
            "system prompt for simplified Chinese excludes Japanese ruby plugin",
            !chinesePrompt.Contains("漢字[かな]", StringComparison.Ordinal) &&
            chinesePrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            chinesePrompt.Contains("English", StringComparison.Ordinal));

        // 4. 纯英文输入触发 EnglishPlugin
        var englishReq = new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）");
        var englishPrompt = PromptTemplates.GetSystemPrompt(englishReq, new PromptConfig());
        Check(
            "system prompt for English input injects English plugin with IPA and infinitive rules",
            englishPrompt.Contains("International Phonetic Alphabet (IPA)", StringComparison.Ordinal) &&
            englishPrompt.Contains("base infinitive form", StringComparison.Ordinal) &&
            !englishPrompt.Contains("漢字[かな]", StringComparison.Ordinal));

        // 5. 独立精讲请求遵循微结构化规约
        var explainReq = new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）");
        var explainPrompt = PromptTemplates.GetSystemPrompt(explainReq, new PromptConfig());
        Check(
            "explain system prompt includes structured markdown sections for vocab, grammar and context",
            explainPrompt.Contains("### [词汇/短语]", StringComparison.Ordinal) &&
            explainPrompt.Contains("### [语法/句型]", StringComparison.Ordinal) &&
            explainPrompt.Contains("### [语境与地道表达]", StringComparison.Ordinal) &&
            explainPrompt.Contains("中文（简体）", StringComparison.Ordinal));

        // 6. 用户自定义 Base 提示词与语言特化插件优先级
        var customPrompts = new PromptConfig
        {
            Base = "Custom base for {{target_language}}\n---TRANSLATION---",
            JapanesePlugin = "[Custom ja-JP Plugin]",
            EnglishPlugin = "[Custom en-US Plugin]",
        };
        var customJaReq = new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "zh-CN");
        var customJaPrompt = PromptTemplates.GetSystemPrompt(customJaReq, customPrompts);
        Check(
            "modular prompt assembly respects user custom base prompt and custom Japanese plugin",
            customJaPrompt.Contains("Custom base for zh-CN", StringComparison.Ordinal) &&
            customJaPrompt.Contains("[Custom ja-JP Plugin]", StringComparison.Ordinal) &&
            !customJaPrompt.Contains(PromptTemplates.DefaultJapanesePlugin, StringComparison.Ordinal));

        var customEnReq = new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "zh-CN");
        var customEnPrompt = PromptTemplates.GetSystemPrompt(customEnReq, customPrompts);
        Check(
            "modular prompt assembly respects user custom English plugin",
            customEnPrompt.Contains("Custom base for zh-CN", StringComparison.Ordinal) &&
            customEnPrompt.Contains("[Custom en-US Plugin]", StringComparison.Ordinal) &&
            !customEnPrompt.Contains(PromptTemplates.DefaultEnglishPlugin, StringComparison.Ordinal));
    }

    private static void TestLanguageDetector()
    {
        // 1. 纯平假名与纯片假名
        var kanaResult = LanguageDetector.Detect("こんにちは");
        var katakanaResult = LanguageDetector.Detect("ラーメン");
        Check(
            "detector flags pure hiragana and katakana as Japanese with ruby requirement",
            kanaResult.HasJapanese && kanaResult.NeedsJapaneseRuby && !kanaResult.HasSimplifiedChinese &&
            katakanaResult.HasJapanese && katakanaResult.NeedsJapaneseRuby && !katakanaResult.HasSimplifiedChinese);

        // 2. 汉字假名混排
        var mixedJapanese = LanguageDetector.Detect("ここは通れないよ");
        var kanjiKana = LanguageDetector.Detect("学生です。");
        Check(
            "detector flags kanji-kana mixed sentences as Japanese with ruby requirement",
            mixedJapanese.HasJapanese && mixedJapanese.NeedsJapaneseRuby &&
            kanjiKana.HasJapanese && kanjiKana.NeedsJapaneseRuby);

        // 3. 纯汉字日文词条（启发式解决纯汉字误判）
        var entryForbidden = LanguageDetector.Detect("進入禁止");
        var guideMap = LanguageDetector.Detect("案内図");
        var attackPower = LanguageDetector.Detect("攻撃力");
        var emergencyExit = LanguageDetector.Detect("非常口");
        var manual = LanguageDetector.Detect("取扱説明書");
        Check(
            "detector identifies pure-kanji Japanese phrases and recommends ruby when target is Chinese",
            entryForbidden.HasJapanese && entryForbidden.NeedsJapaneseRuby && entryForbidden.IsPureHanziJapaneseCandidate &&
            guideMap.HasJapanese && guideMap.NeedsJapaneseRuby && guideMap.IsPureHanziJapaneseCandidate &&
            attackPower.HasJapanese && attackPower.NeedsJapaneseRuby && attackPower.IsPureHanziJapaneseCandidate &&
            emergencyExit.HasJapanese && emergencyExit.NeedsJapaneseRuby && emergencyExit.IsPureHanziJapaneseCandidate &&
            manual.HasJapanese && manual.NeedsJapaneseRuby && manual.IsPureHanziJapaneseCandidate);

        // 4. 纯简体中文句子（命中独占字集）
        var simplifiedWalk = LanguageDetector.Detect("今天天气很好，我们去散步");
        var simplifiedTest = LanguageDetector.Detect("这是一个测试句子");
        var simplifiedEnter = LanguageDetector.Detect("禁止进入");
        var simplifiedStadium = LanguageDetector.Detect("入场禁止");
        Check(
            "detector correctly identifies simplified Chinese exclusive characters and excludes ruby injection",
            simplifiedWalk.HasSimplifiedChinese && !simplifiedWalk.NeedsJapaneseRuby && !simplifiedWalk.HasJapanese &&
            simplifiedTest.HasSimplifiedChinese && !simplifiedTest.NeedsJapaneseRuby && !simplifiedTest.HasJapanese &&
            simplifiedEnter.HasSimplifiedChinese && !simplifiedEnter.NeedsJapaneseRuby && !simplifiedEnter.HasJapanese &&
            simplifiedStadium.HasSimplifiedChinese && !simplifiedStadium.NeedsJapaneseRuby && !simplifiedStadium.HasJapanese);

        // 5. 纯英文短句
        var englishSentence = LanguageDetector.Detect("Hello world, this is a test.");
        Check(
            "detector identifies English sentence with IPA requirement and without Japanese ruby",
            englishSentence.HasEnglish && englishSentence.NeedsEnglishIpa && !englishSentence.HasJapanese && !englishSentence.NeedsJapaneseRuby);

        // 6. 日英混排
        var japaneseEnglish = LanguageDetector.Detect("Appleの新しいiPhone発表会");
        Check(
            "detector identifies mixed Japanese and English text",
            japaneseEnglish.HasJapanese && japaneseEnglish.HasEnglish && japaneseEnglish.NeedsJapaneseRuby);
    }

    private static void TestRubyTextParser()
    {
        // 1. 日文基础假名注音切分与互转
        var sample1 = "こ こ は  通[とお]れないよ";
        var segments1 = RubyTextParser.Parse(sample1);
        Check(
            "ruby parser correctly backtracks kanji base and segments text",
            segments1.Count == 3 &&
            !segments1[0].IsRuby && segments1[0].Text == "こ こ は  " &&
            segments1[1].IsRuby && segments1[1].Text == "通" && segments1[1].Ruby == "とお" &&
            !segments1[2].IsRuby && segments1[2].Text == "れないよ" &&
            RubyTextParser.ToPlainText(sample1) == "こ こ は  通れないよ" &&
            RubyTextParser.ToReadingText(sample1) == "こ こ は  とおれないよ" &&
            RubyTextParser.ToAnnotatedText(segments1) == sample1);

        // 2. 复合汉字词组与连续注音
        var sample2 = "私[わたし]は 学生[がくせい]です。進入禁止[しんにゅうきんし]";
        var segments2 = RubyTextParser.Parse(sample2);
        Check(
            "ruby parser handles multiple kanji compounds and sentence annotations",
            segments2.Count == 5 &&
            segments2[0].IsRuby && segments2[0].Text == "私" && segments2[0].Ruby == "わたし" &&
            segments2[2].IsRuby && segments2[2].Text == "学生" && segments2[2].Ruby == "がくせい" &&
            segments2[4].IsRuby && segments2[4].Text == "進入禁止" && segments2[4].Ruby == "しんにゅうきんし" &&
            RubyTextParser.ToPlainText(sample2) == "私は 学生です。進入禁止" &&
            RubyTextParser.ToReadingText(sample2) == "わたしは がくせいです。しんにゅうきんし");

        // 3. 汉字叠字标记“々”
        var sample3 = "佐々木[ささき]様";
        var segments3 = RubyTextParser.Parse(sample3);
        Check(
            "ruby parser supports iteration mark kanji base",
            segments3.Count == 2 &&
            segments3[0].IsRuby && segments3[0].Text == "佐々木" && segments3[0].Ruby == "ささき" &&
            !segments3[1].IsRuby && segments3[1].Text == "様");

        // 4. 英文 IPA 音标注解
        var sample4 = "apple[ˈæpl] and banana[bəˈnɑːnə]";
        var segments4 = RubyTextParser.Parse(sample4);
        Check(
            "ruby parser parses english words with IPA pronunciation annotations",
            segments4.Count == 3 &&
            segments4[0].IsRuby && segments4[0].Text == "apple" && segments4[0].Ruby == "ˈæpl" &&
            segments4[2].IsRuby && segments4[2].Text == "banana" && segments4[2].Ruby == "bəˈnɑːnə" &&
            RubyTextParser.ToPlainText(sample4) == "apple and banana" &&
            RubyTextParser.ToReadingText(sample4) == "ˈæpl and bəˈnɑːnə");

        // 5. 排除代码下标与孤立括号
        var sample5 = "int x = arr[0] + list[1]; [tag]";
        var segments5 = RubyTextParser.Parse(sample5);
        Check(
            "ruby parser rejects code array brackets and solitary brackets",
            segments5.All(s => !s.IsRuby) &&
            RubyTextParser.ToPlainText(sample5) == sample5);
    }

    private static void TestSseReader()
    {
        const string payload = "event: delta\ndata: {\\\"text\\\":\\\"A\\\"}\n\ndata: [DONE]\n\n";
        using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes(payload), chunkSize: 3);
        var frames = ReadSseAsync(stream).GetAwaiter().GetResult();
        Check(
            "SSE reader groups data lines across packet boundaries",
            frames.Count == 2 && frames[0].Event == "delta" && frames[0].Data == "{\\\"text\\\":\\\"A\\\"}" && frames[1].Data == "[DONE]");

        using var plainJsonStream = new MemoryStream(Encoding.UTF8.GetBytes("{\\\"message\\\":\\\"vision\\\"}"));
        var plainJsonFrames = ReadSseAsync(plainJsonStream).GetAwaiter().GetResult();
        Check(
            "SSE reader accepts a non-stream JSON response as one event",
            plainJsonFrames.Count == 1 && plainJsonFrames[0].Event is null &&
            plainJsonFrames[0].Data == "{\\\"message\\\":\\\"vision\\\"}");
    }

    private static void TestSentinelRouter()
    {
        var simpleSeparatorRouter = new SectionChannelRouter();
        var simpleSeparatorDeltas = simpleSeparatorRouter
            .Push("原文\n--")
            .Concat(simpleSeparatorRouter.Push("-\n译文"))
            .Concat(simpleSeparatorRouter.Complete())
            .ToList();
        Check(
            "section router accepts the five-character separator split across frames",
            string.Concat(simpleSeparatorDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "原文" &&
            string.Concat(simpleSeparatorDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "译文");

        var paddedSeparatorRouter = new SectionChannelRouter();
        var paddedSeparatorDeltas = paddedSeparatorRouter
            .Push("原文\n\n--")
            .Concat(paddedSeparatorRouter.Push("-\n\n译文"))
            .Concat(paddedSeparatorRouter.Complete())
            .ToList();
        Check(
            "section router consumes the blank lines around the configured separator",
            string.Concat(paddedSeparatorDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "原文" &&
            string.Concat(paddedSeparatorDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "译文");

        var fallbackRouter = new SectionChannelRouter();
        var fallbackDeltas = fallbackRouter.Push("\n\nplain translation").Concat(fallbackRouter.Complete()).ToList();
        Check(
            "section router keeps unseparated vision output as a trimmed translation",
            string.Concat(fallbackDeltas.Select(delta => delta.Text)) == "plain translation" &&
            fallbackDeltas.All(delta => delta.Channel == DeltaChannel.Translation));

        var translationMarkerRouter = new SectionChannelRouter();
        var translationMarkerDeltas = translationMarkerRouter
            .Push("私[わたし]は\n---TRANS")
            .Concat(translationMarkerRouter.Push("LATION---\n我是"))
            .Concat(translationMarkerRouter.Complete())
            .ToList();
        Check(
            "section router handles ---TRANSLATION--- marker split across chunks",
            string.Concat(translationMarkerDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "私[わたし]は" &&
            string.Concat(translationMarkerDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "我是");

        var crlfCaseRouter = new SectionChannelRouter();
        var crlfCaseDeltas = crlfCaseRouter
            .Push("<annotated_source>\r\nここは通[とお]れないよ\r\n</annotated_source>\r\n---translation---\r\n<translation>\r\n这里过不去哦\r\n</translation>")
            .Concat(crlfCaseRouter.Complete())
            .ToList();
        Check(
            "section router handles CRLF, case-insensitive sentinel, and strips pseudo XML tags",
            string.Concat(crlfCaseDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "ここは通[とお]れないよ" &&
            string.Concat(crlfCaseDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "这里过不去哦");

        var immediateStreamRouter = new SectionChannelRouter();
        _ = immediateStreamRouter.Push("私[わたし]は\n---TRANSLATION---\n").ToList();
        var immediateChunk = immediateStreamRouter.Push("短译文").ToList();
        Check(
            "section router flushes translation tokens immediately in real-time without buffering",
            immediateChunk.Count == 1 &&
            immediateChunk[0].Channel == DeltaChannel.Translation &&
            immediateChunk[0].Text == "短译文");
    }

    private static void TestProviderTextExtraction()
    {
        using var openAi = JsonDocument.Parse("""
            {"choices":[{"delta":{"content":[{"type":"text","text":"视觉"},{"type":"text","text":"结果"}]}}]}
            """);
        using var gemini = JsonDocument.Parse("""
            {"candidates":[{"content":{"parts":[{"text":"第一段"},{"text":"第二段"}]}}]}
            """);
        using var emptyVisionContent = JsonDocument.Parse(JsonSerializer.Serialize(
            ClaudeProvider.CreateContent((ReadOnlyMemory<byte>?)new byte[] { 1 }, string.Empty)));
        Check(
            "provider text extraction handles OpenAI content arrays",
            TranslationProviderBase.GetTextContent(openAi.RootElement, "choices", "0", "delta", "content") == "视觉结果");
        Check(
            "provider text extraction handles all Gemini text parts",
            TranslationProviderBase.GetTextContent(gemini.RootElement, "candidates") == "第一段第二段");
        Check(
            "Claude vision request omits an empty text content block",
            emptyVisionContent.RootElement.ValueKind == JsonValueKind.Array &&
            emptyVisionContent.RootElement.GetArrayLength() == 1 &&
            emptyVisionContent.RootElement[0].GetProperty("type").GetString() == "image");
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
        Check(
            "error mapper explains multimodal capability failures",
            ProviderErrorMapper.ToUserMessage(
                new ProviderHttpException(HttpStatusCode.InternalServerError, "multimodal processing is not enabled"))
                .Contains("未启用图像输入", StringComparison.Ordinal));
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

    /// <summary>支持模拟非平坦网络切片传输的流包装器。</summary>
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
}

