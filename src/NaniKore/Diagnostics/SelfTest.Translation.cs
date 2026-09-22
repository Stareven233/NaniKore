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
        TestPromptLiteralFormatting();
        TestModularPromptTemplates();
        TestLanguageDetector();
        TestRubyTextParser();
        TestSseReader();
        TestSentinelRouter();
        TestNoiseTagCalibration();
        TestProviderTextExtraction();
        TestProviderCapabilitiesAndErrors();
        TestTokenUsageAndMetrics();
        TestTranslationSessionAndMultiTurnReplay();
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
        const string expectedVisionShape = "<annotated_source>\n---TRANSLATION---\n<translation>";
        var normalizedVisionPrompt = defaultVisionPrompt.Replace("\r\n", "\n");
        Check(
            "default vision prompt expands its target language and keeps the two-part protocol",
            normalizedVisionPrompt.Contains("zh-CN", StringComparison.Ordinal) &&
            normalizedVisionPrompt.Contains(expectedVisionShape, StringComparison.Ordinal) &&
            !normalizedVisionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var visionPrompt = PromptTemplates.GetSystemPrompt(
            imageRequest,
            new PromptConfig { VisionOneshot = "OCR\n---TRANSLATION---\nTranslate to {{target_language}}; retain {{unknown_placeholder}}" });
        Check(
            "custom vision prompt expands only the target language placeholder",
            visionPrompt == "OCR\n---TRANSLATION---\nTranslate to zh-CN; retain {{unknown_placeholder}}");

        var pureModularPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig());
        Check(
            "empty prompt config strictly builds modular prompt with sentinel and zero explanation directives",
            pureModularPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            pureModularPrompt.Contains("你当前工作在一个翻译应用中", StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("### [词汇/短语]", StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("### [语法/句型]", StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("辞書形", StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("JLPT", StringComparison.Ordinal));
    }

    /// <summary>校验内置提示词字面量的排版契约：标题与协议行顶格、规则条目统一缩进两个空格，零 CR、零首尾与行尾空白。</summary>
    private static void TestPromptLiteralFormatting()
    {
        var literals = new (string Name, string Value)[]
        {
            (nameof(PromptTemplates.DefaultTranslationBasePrompt), PromptTemplates.DefaultTranslationBasePrompt),
            (nameof(PromptTemplates.JapanesePlugin), PromptTemplates.JapanesePlugin),
            (nameof(PromptTemplates.EnglishPlugin), PromptTemplates.EnglishPlugin),
            (nameof(PromptTemplates.LanguageAlignmentRule), PromptTemplates.LanguageAlignmentRule),
            (nameof(PromptTemplates.DefaultVisionOneshotPrompt), PromptTemplates.DefaultVisionOneshotPrompt),
            (nameof(PromptTemplates.ExplainStructureRule), PromptTemplates.ExplainStructureRule),
            (nameof(PromptTemplates.ExplainLanguageAlignmentRule), PromptTemplates.ExplainLanguageAlignmentRule),
            (nameof(PromptTemplates.DefaultExplainUserPrompt), PromptTemplates.DefaultExplainUserPrompt),
        };

        foreach (var (name, value) in literals)
        {
            // 缩进策略：全文仅容许「两个空格」这一档（规则条目），标题、协议行与占位符行一律顶格
            var badLines = value.Split('\n').Where(line =>
                line != line.TrimEnd() ||
                line.StartsWith('\t') ||
                (line.StartsWith(' ') &&
                 (!line.StartsWith("  ", StringComparison.Ordinal) ||
                  line.StartsWith("   ", StringComparison.Ordinal))))
                .ToArray();
            Check(
                $"prompt literal {name} keeps two-space rule indentation without stray whitespace or CR",
                value == value.Trim() &&
                !value.Contains('\r', StringComparison.Ordinal) &&
                badLines.Length == 0);
        }

        // 全中文化：规则声明与插件标题不得残留英文指令字眼（IPA / Markdown / JLPT 等技术术语除外）
        string[] englishRuleMarkers =
        [
            "You are", "Strictly", "NEVER", "MUST", "[Strict", "Plugin]", "Specification]", "Constraint]", "Discipline]",
        ];
        foreach (var (name, value) in literals)
        {
            var englishHits = englishRuleMarkers
                .Where(marker => value.Contains(marker, StringComparison.Ordinal))
                .ToArray();
            Check(
                $"prompt literal {name} is fully sinicized without English rule declarations",
                englishHits.Length == 0);
        }

        // 协议一致性：声明两段式输出的提示词必须且只能使用唯一哨兵，杜绝“提示词说一套、路由层认一套”的漂移
        (string Name, string Value)[] twoPartProtocols =
        [
            (nameof(PromptTemplates.DefaultTranslationBasePrompt), PromptTemplates.DefaultTranslationBasePrompt),
            (nameof(PromptTemplates.DefaultVisionOneshotPrompt), PromptTemplates.DefaultVisionOneshotPrompt),
        ];
        foreach (var (name, value) in twoPartProtocols)
        {
            Check(
                $"two-part protocol prompt {name} declares only the single translation sentinel",
                value.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
                !value.Contains("\n---\n", StringComparison.Ordinal));
        }

        // 组装后的 system prompt 同样必须干净：无 CR、首尾无空白
        var assembled = new[]
        {
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "text", new byte[] { 1 }, "中文（简体）"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）"), new PromptConfig()),
        };

        foreach (var prompt in assembled)
        {
            Check(
                "assembled system prompt is trimmed and free of carriage returns",
                prompt == prompt.Trim() && !prompt.Contains('\r', StringComparison.Ordinal));
        }
    }

    private static void TestModularPromptTemplates()
    {
        // 1. 日文输入触发 JapanesePlugin（只注入振假名规约，不注入精讲文法与辞书形）
        var japaneseReq = new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）");
        var japanesePrompt = PromptTemplates.GetSystemPrompt(japaneseReq, new PromptConfig());
        Check(
            "system prompt for Japanese input injects ruby format brackets and excludes explanation grammar terms",
            japanesePrompt.Contains("漢字[かな]", StringComparison.Ordinal) &&
            !japanesePrompt.Contains("辞書形", StringComparison.Ordinal) &&
            !japanesePrompt.Contains("未然形", StringComparison.Ordinal) &&
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
            "system prompt for English input injects English plugin with IPA and excludes Japanese ruby",
            englishPrompt.Contains("国际音标（IPA）", StringComparison.Ordinal) &&
            !englishPrompt.Contains("漢字[かな]", StringComparison.Ordinal));

        // 5. 独立精讲请求遵循微结构化规约与文法术语要求
        var explainReq = new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）");
        var explainPrompt = PromptTemplates.GetSystemPrompt(explainReq, new PromptConfig());
        Check(
            "explain system prompt includes structured markdown sections for vocab, grammar and context",
            explainPrompt.Contains("### [词汇/短语]", StringComparison.Ordinal) &&
            explainPrompt.Contains("### [语法/句型]", StringComparison.Ordinal) &&
            explainPrompt.Contains("### [翻译精讲]", StringComparison.Ordinal) &&
            explainPrompt.Contains("辞書形", StringComparison.Ordinal) &&
            explainPrompt.Contains("JLPT N3", StringComparison.Ordinal) &&
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
            !customJaPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal));

        var customEnReq = new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "zh-CN");
        var customEnPrompt = PromptTemplates.GetSystemPrompt(customEnReq, customPrompts);
        Check(
            "modular prompt assembly respects user custom English plugin",
            customEnPrompt.Contains("Custom base for zh-CN", StringComparison.Ordinal) &&
            customEnPrompt.Contains("[Custom en-US Plugin]", StringComparison.Ordinal) &&
            !customEnPrompt.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));
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
        var legacyRuleRouter = new SectionChannelRouter();
        var legacyRuleDeltas = legacyRuleRouter
            .Push("私[わたし]は\n---\n学生[がくせい]です。")
            .Concat(legacyRuleRouter.Complete())
            .ToList();
        Check(
            "section router no longer recognizes the legacy bare markdown rule as a separator",
            string.Concat(legacyRuleDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "私[わたし]は\n---\n学生[がくせい]です。" &&
            legacyRuleDeltas.All(delta => delta.Channel == DeltaChannel.SourceText));

        var crlfSplitRouter = new SectionChannelRouter();
        var crlfSplitDeltas = crlfSplitRouter
            .Push("原文\r\n---TRANS")
            .Concat(crlfSplitRouter.Push("LATION---\r\n译文"))
            .Concat(crlfSplitRouter.Complete())
            .ToList();
        Check(
            "section router splits the translation sentinel across frames and absorbs CRLF padding",
            string.Concat(crlfSplitDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "原文" &&
            string.Concat(crlfSplitDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "译文");

        var paddedSeparatorRouter = new SectionChannelRouter();
        var paddedSeparatorDeltas = paddedSeparatorRouter
            .Push("原文\n\n---TRANS")
            .Concat(paddedSeparatorRouter.Push("LATION---\n\n译文"))
            .Concat(paddedSeparatorRouter.Complete())
            .ToList();
        Check(
            "section router consumes the blank lines around the translation sentinel",
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

        // 视觉一条龙默认提示词声明的协议形态必须能被路由层无损拆分（防止提示词与路由层协议漂移）
        var visionProtocolRouter = new SectionChannelRouter();
        var visionProtocolDeltas = visionProtocolRouter
            .Push("""
<annotated_source>
私[わたし]は 学生[がくせい]です。
---TRANSLATION---
我是学生。
</translation>
""")
            .Concat(visionProtocolRouter.Complete())
            .ToList();
        Check(
            "section router splits the exact two-part shape declared by the default vision prompt",
            string.Concat(visionProtocolDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)) == "私[わたし]は 学生[がくせい]です。" &&
            string.Concat(visionProtocolDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "我是学生。");

        var immediateStreamRouter = new SectionChannelRouter();
        _ = immediateStreamRouter.Push("私[わたし]は\n---TRANSLATION---\n").ToList();
        var immediateChunk = immediateStreamRouter.Push("短译文").ToList();
        Check(
            "section router flushes translation tokens immediately in real-time without buffering",
            immediateChunk.Count == 1 &&
            immediateChunk[0].Channel == DeltaChannel.Translation &&
            immediateChunk[0].Text == "短译文");

        var sourceStreamRouter = new SectionChannelRouter();
        var earlySourceDeltas = sourceStreamRouter.Push("私[わたし]は学生[がくせい]です。").ToList();
        Check(
            "section router flushes annotated source tokens in real-time before sentinel arrives",
            earlySourceDeltas.Count > 0 &&
            earlySourceDeltas.All(delta => delta.Channel == DeltaChannel.SourceText) &&
            string.Concat(earlySourceDeltas.Select(delta => delta.Text)) == "私[わたし]は学生[がくせい]です。");

        var splitSentinelDeltas = sourceStreamRouter.Push("\n---TRANS").ToList();
        Check(
            "section router holds trailing sentinel prefix in sliding window without leaking to source channel",
            splitSentinelDeltas.Count == 0);

        var continuationDeltas = sourceStreamRouter.Push("LATION---\n我是学生。").Concat(sourceStreamRouter.Complete()).ToList();
        Check(
            "section router completes sentinel match and streams translation seamlessly",
            string.Concat(continuationDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "我是学生。");

        var tagLeakProtectionRouter = new SectionChannelRouter();
        var leakTestDeltas = tagLeakProtectionRouter
            .Push("<annotated_source>\r\n私[わたし]は学生です。")
            .Concat(tagLeakProtectionRouter.Push("\r\n</annotated_source>\r\n"))
            .Concat(tagLeakProtectionRouter.Push("---TRANSLATION---\r\n<translation>\r\n我是学生。\r\n</translation>"))
            .Concat(tagLeakProtectionRouter.Complete())
            .ToList();
        var leakedSource = string.Concat(leakTestDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text));
        var leakedTrans = string.Concat(leakTestDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text));
        Check(
            "section router strictly prevents any pseudo XML closing tags from leaking into source channel",
            !leakedSource.Contains("</annotated_source>") &&
            !leakedSource.Contains("<annotated_source>") &&
            leakedSource == "私[わたし]は学生です。" &&
            leakedTrans == "我是学生。");

        var bracketTagAndExplanationLeakRouter = new SectionChannelRouter();
        var bracketTestDeltas = bracketTagAndExplanationLeakRouter
            .Push("[annotated_source]\n亡[な]くなった\n[/annotated_source]\n---TRANSLATION---\n<translation>\n逝去了\n</translation>\n\n### [词汇/短语]\n- 亡くなる：逝世")
            .Concat(bracketTagAndExplanationLeakRouter.Complete())
            .ToList();
        var bracketSource = string.Concat(bracketTestDeltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text));
        var bracketTrans = string.Concat(bracketTestDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text));
        Check(
            "section router strips square bracket tags, trailing </translation> and truncates rogue explanation markdown",
            !bracketSource.Contains("[annotated_source]") &&
            !bracketSource.Contains("[/annotated_source]") &&
            bracketSource == "亡[な]くなった" &&
            !bracketTrans.Contains("</translation>") &&
            !bracketTrans.Contains("###") &&
            bracketTrans == "逝去了");

        var rogueHeaderRouter = new SectionChannelRouter();
        var rogueHeaderDeltas = rogueHeaderRouter
            .Push("<annotated_source>\n新歓[しんかん]で気[き]が大き[おお]くなってしまう話[はなし]\n</annotated_source>\n---TRANSLATION---\n在迎新活动中变得胆大妄为的故事\n\n## 深度精讲拆解\n- 新歓：迎新活动")
            .Concat(rogueHeaderRouter.Complete())
            .ToList();
        var rogueTrans = string.Concat(rogueHeaderDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text));
        Check(
            "section router truncates ## 深度精讲拆解 and rogue explanation sections from translation channel",
            !rogueTrans.Contains("## 深度精讲拆解") &&
            !rogueTrans.Contains("新歓") &&
            rogueTrans == "在迎新活动中变得胆大妄为的故事");

        var cleanedWithInitial = NaniKore.App.ResultPanelWindow.CleanAnnotatedSourceWithInitial("[annotated_source]亡[な]くなった[/annotated_source]", "亡くなった");
        Check(
            "CleanAnnotatedSourceWithInitial strips square brackets based on initial text comparison",
            cleanedWithInitial == "亡[な]くなった");

        var cleanedWithCustomTag = NaniKore.App.ResultPanelWindow.CleanAnnotatedSourceWithInitial("[unknown_tag]亡[な]くなった", "亡くなった");
        Check(
            "CleanAnnotatedSourceWithInitial detects and removes unknown prefix tags by aligning with initial source first character",
            cleanedWithCustomTag == "亡[な]くなった");
    }

    /// <summary>
    /// 噪声标签清单校准契约（阶段二遗留项 A）：兜底标签必须与现行两段式协议的产生源一一对应，
    /// 旧版英文视觉提示词的 &lt;transcription&gt; 占位符系列已随协议废弃，不得回归。
    /// </summary>
    private static void TestNoiseTagCalibration()
    {
        // 现行提示词声明的结构占位符必须保留兜底（含方括号与冒号变体）
        Check(
            "noise tag lists keep covering the placeholders declared by the current two-part protocol",
            SectionChannelRouter.SourceLeadingTags.Contains("<annotated_source>") &&
            SectionChannelRouter.SourceLeadingTags.Contains("[annotated_source]") &&
            SectionChannelRouter.SourceLeadingTags.Contains("annotated_source:") &&
            SectionChannelRouter.SourceCloseTags.Contains("</annotated_source>") &&
            SectionChannelRouter.SourceCloseTags.Contains("[/annotated_source]") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("<translation>") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("[translation]") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("translation:") &&
            SectionChannelRouter.TranslationCloseTags.Contains("</translation>") &&
            SectionChannelRouter.TranslationCloseTags.Contains("[/translation]"));

        // 模型自发中文标签与代码围栏兜底保留
        Check(
            "noise tag lists keep the spontaneous Chinese section labels and markdown fences",
            SectionChannelRouter.SourceLeadingTags.Contains("【原文】") &&
            SectionChannelRouter.SourceLeadingTags.Contains("【注音】") &&
            SectionChannelRouter.SourceLeadingTags.Contains("原文：") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("【译文】") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("译文：") &&
            SectionChannelRouter.SourceLeadingTags.Contains("```") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("```"));

        // 旧协议遗留且无产生源的 transcription 占位符系列不得回归
        string[] legacyTranscriptionTags =
        [
            "<transcription>",
            "[transcription]",
            "transcription:",
            "</transcription>",
            "[/transcription]"
        ];
        Check(
            "legacy transcription placeholder family stays removed from every noise tag list",
            !legacyTranscriptionTags.Any(legacy =>
                SectionChannelRouter.SourceLeadingTags.Contains(legacy) ||
                SectionChannelRouter.SourceCloseTags.Contains(legacy) ||
                SectionChannelRouter.TranslationLeadingTags.Contains(legacy) ||
                SectionChannelRouter.TranslationCloseTags.Contains(legacy)));
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

    private static void TestTokenUsageAndMetrics()
    {
        // 1. OpenAI completions usage 提取
        var completionsJson = "{\"choices\":[{\"delta\":{\"content\":\"foo\"}}],\"usage\":{\"prompt_tokens\":320,\"completion_tokens\":45,\"total_tokens\":365}}";
        var usage = OpenAiProvider.ExtractCompletionsUsage(completionsJson);
        Check(
            "OpenAI completions usage extracts input and output tokens",
            usage.HasValue && usage.Value.InputTokens == 320 && usage.Value.OutputTokens == 45 && usage.Value.TotalTokens == 365);

        // 2. OpenAI responses usage 提取
        var responsesJson = "{\"type\":\"response.done\",\"response\":{\"usage\":{\"input_tokens\":150,\"output_tokens\":25,\"total_tokens\":175}}}";
        var rUsage = OpenAiProvider.ExtractResponsesUsage(responsesJson);
        Check(
            "OpenAI responses usage extracts input and output tokens",
            rUsage.HasValue && rUsage.Value.InputTokens == 150 && rUsage.Value.OutputTokens == 25 && rUsage.Value.TotalTokens == 175);

        // 3. Claude tokens 提取 (message_start + message_delta)
        var claudeInput = 0;
        var claudeOutput = 0;
        var startFrame = new SseEvent { Event = "message_start", Data = "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":200}}}" };
        ClaudeProvider.ExtractClaudeTokens(startFrame, ref claudeInput, ref claudeOutput);
        var deltaFrame = new SseEvent { Event = "message_delta", Data = "{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":35}}" };
        ClaudeProvider.ExtractClaudeTokens(deltaFrame, ref claudeInput, ref claudeOutput);
        Check(
            "Claude tokens extract input from message_start and output from message_delta",
            claudeInput == 200 && claudeOutput == 35);

        // 4. Gemini usageMetadata 提取
        var geminiInput = 0;
        var geminiOutput = 0;
        var geminiJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"bar\"}]}}],\"usageMetadata\":{\"promptTokenCount\":250,\"candidatesTokenCount\":60}}";
        GeminiProvider.ExtractGeminiTokens(geminiJson, ref geminiInput, ref geminiOutput);
        Check(
            "Gemini tokens extract input and output from usageMetadata",
            geminiInput == 250 && geminiOutput == 60);

        // 5. TokenEstimator 兜底测试
        var cjkEstimate = TokenEstimator.Estimate("ここは通れないよ");
        var engEstimate = TokenEstimator.Estimate("Hello world this is a test");
        var imgEstimate = TokenEstimator.Estimate(null, hasImage: true);
        var emptyEstimate = TokenEstimator.Estimate(string.Empty);
        Check(
            "TokenEstimator estimates CJK and English text smoothly with image support",
            cjkEstimate >= 5 && engEstimate >= 5 && imgEstimate >= 100 && emptyEstimate == 0);

        // 6. TranslationMetrics.FormatSummary 格式测试
        var metrics = new TranslationMetrics(
            InputTokens: 320,
            OutputTokens: 45,
            TimeToFirstTokenMs: 680,
            TotalDurationMs: 1750,
            TokensPerSecond: 42.1);
        Check(
            "TranslationMetrics formats summary matching Input / Output / TTFT / t/s structure",
            metrics.FormatSummary() == "Input: 320 tokens · Output: 45 tokens · TTFT: 680ms · 生成速度: 42 t/s");

        var estimatedMetrics = new TranslationMetrics(
            InputTokens: 320,
            OutputTokens: 45,
            TimeToFirstTokenMs: 680,
            TotalDurationMs: 1750,
            TokensPerSecond: 42.1,
            IsEstimated: true);
        Check(
            "TranslationMetrics formats estimated summary with tilde prefix",
            estimatedMetrics.FormatSummary() == "Input: ~320 tokens · Output: ~45 tokens · TTFT: 680ms · 生成速度: 42 t/s");
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

    private static void TestTranslationSessionAndMultiTurnReplay()
    {
        // 1. 测试 TranslationSession 初始化与阶段 1 原始增量无损累积
        var imageBytes = new byte[] { 137, 80, 78, 71 };
        var session = new TranslationSession("system_test_prompt", "中文（简体）", "こ こ は 通れないよ", imageBytes);
        Check(
            "translation session initializes user message with text and image payload",
            session.Messages.Count == 1 &&
            session.Messages[0].Role == ChatRole.User &&
            session.Messages[0].Text == "こ こ は 通れないよ" &&
            session.Messages[0].ImagePng is { Length: 4 });

        session.AppendRawAssistantOutput("こ こ は  通[とお]れないよ\n");
        session.AppendRawAssistantOutput("---TRANSLATION---\n这里过不去哦");
        session.FinalizeStage1Output();

        Check(
            "session finalizes assistant message matching raw streamed chunks exactly",
            session.Messages.Count == 2 &&
            session.Messages[1].Role == ChatRole.Assistant &&
            session.Messages[1].Text == "こ こ は  通[とお]れないよ\n---TRANSLATION---\n这里过不去哦" &&
            session.GetStage1Output() == "こ こ は  通[とお]れないよ\n---TRANSLATION---\n这里过不去哦");

        // 2. 准备阶段 2 精讲，验证 User 追问追加与 SystemPrompt 前缀缓存复用
        session.PrepareStage2Request(PromptTemplates.DefaultExplainUserPrompt);
        Check(
            "session prepares stage 2 request by appending explanation user prompt",
            session.Messages.Count == 3 &&
            session.Messages[2].Role == ChatRole.User &&
            session.Messages[2].Text == PromptTemplates.DefaultExplainUserPrompt);

        var stage2Request = new UnderstandRequest(UnderstandKind.Explain, session.InitialSourceText, session.ImagePng, "中文（简体）", session);
        var prompt = PromptTemplates.GetSystemPrompt(stage2Request, new PromptConfig());
        Check(
            "PromptTemplates.GetSystemPrompt reuses identical system prompt from session to preserve cache",
            prompt == "system_test_prompt");

        // 3. 验证 OpenAI Provider 生成多轮 messages 数组，且首轮输入输出与首轮完全对齐
        var openAiProfile = new ProviderProfile { Id = "openai", ApiType = "chat_completions", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" };
        var openAiProvider = new OpenAiProvider(openAiProfile, "test_key", new PromptConfig());
        using var openAiCompletionsMsg = openAiProvider.CreateCompletionsRequest(stage2Request);
        using var openAiDoc = JsonDocument.Parse(openAiCompletionsMsg.Content!.ReadAsStringAsync().Result);
        var openAiMessages = openAiDoc.RootElement.GetProperty("messages");
        Check(
            "OpenAI completions request creates 4 messages preserving system, user, assistant, and user turn 2",
            openAiMessages.GetArrayLength() == 4 &&
            openAiMessages[0].GetProperty("role").GetString() == "system" &&
            openAiMessages[0].GetProperty("content").GetString() == "system_test_prompt" &&
            openAiMessages[1].GetProperty("role").GetString() == "user" &&
            openAiMessages[1].GetProperty("content").ValueKind == JsonValueKind.Array &&
            openAiMessages[2].GetProperty("role").GetString() == "assistant" &&
            openAiMessages[2].GetProperty("content").GetString() == "こ こ は  通[とお]れないよ\n---TRANSLATION---\n这里过不去哦" &&
            openAiMessages[3].GetProperty("role").GetString() == "user" &&
            openAiMessages[3].GetProperty("content").GetString() == PromptTemplates.DefaultExplainUserPrompt);

        using var openAiResponsesMsg = openAiProvider.CreateResponsesRequest(stage2Request);
        using var openAiRespDoc = JsonDocument.Parse(openAiResponsesMsg.Content!.ReadAsStringAsync().Result);
        var openAiInputs = openAiRespDoc.RootElement.GetProperty("input");
        Check(
            "OpenAI responses request creates 4 input elements with output_text for assistant turn",
            openAiInputs.GetArrayLength() == 4 &&
            openAiInputs[0].GetProperty("role").GetString() == "system" &&
            openAiInputs[1].GetProperty("role").GetString() == "user" &&
            openAiInputs[2].GetProperty("role").GetString() == "assistant" &&
            openAiInputs[3].GetProperty("role").GetString() == "user");

        // 4. 验证 Claude Provider 生成多轮 messages（user / assistant / user）
        var claudeProfile = new ProviderProfile { Id = "claude", ApiType = "messages", BaseUrl = "https://api.anthropic.com", Model = "claude-3-5-sonnet-20241022" };
        var claudeProvider = new ClaudeProvider(claudeProfile, "test_key", new PromptConfig());
        using var claudeMsg = claudeProvider.CreateRequest(stage2Request);
        using var claudeDoc = JsonDocument.Parse(claudeMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Claude request preserves top-level system and alternates user/assistant messages",
            claudeDoc.RootElement.GetProperty("system").GetString() == "system_test_prompt" &&
            claudeDoc.RootElement.GetProperty("messages").GetArrayLength() == 3 &&
            claudeDoc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "user" &&
            claudeDoc.RootElement.GetProperty("messages")[1].GetProperty("role").GetString() == "assistant" &&
            claudeDoc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "こ こ は  通[とお]れないよ\n---TRANSLATION---\n这里过不去哦" &&
            claudeDoc.RootElement.GetProperty("messages")[2].GetProperty("role").GetString() == "user");

        // 5. 验证 Gemini Provider 生成 contents（user / model / user）
        var geminiProfile = new ProviderProfile { Id = "gemini", ApiType = "gemini", BaseUrl = "https://generativelanguage.googleapis.com", Model = "gemini-1.5-pro" };
        var geminiProvider = new GeminiProvider(geminiProfile, "test_key", new PromptConfig());
        using var geminiMsg = geminiProvider.CreateRequest(stage2Request);
        using var geminiDoc = JsonDocument.Parse(geminiMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Gemini request preserves system_instruction and formats contents with user and model roles",
            geminiDoc.RootElement.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString() == "system_test_prompt" &&
            geminiDoc.RootElement.GetProperty("contents").GetArrayLength() == 3 &&
            geminiDoc.RootElement.GetProperty("contents")[0].GetProperty("role").GetString() == "user" &&
            geminiDoc.RootElement.GetProperty("contents")[1].GetProperty("role").GetString() == "model" &&
            geminiDoc.RootElement.GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("text").GetString() == "こ こ は  通[とお]れないよ\n---TRANSLATION---\n这里过不去哦" &&
            geminiDoc.RootElement.GetProperty("contents")[2].GetProperty("role").GetString() == "user");
    }
}

