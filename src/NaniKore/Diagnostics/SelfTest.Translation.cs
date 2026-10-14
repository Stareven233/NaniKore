using System.Net;
using System.Net.Http;
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
        TestTextPromptDecoupling();
        TestVisionPromptDecoupling();
        TestPromptSectionDescription();
        TestLanguageDetector();
        TestRubyTextParser();
        TestSseReader();
        TestSentinelRouter();
        TestNoiseTagCalibration();
        TestProviderTextExtraction();
        TestProviderCapabilitiesAndErrors();
        TestTokenUsageAndMetrics();
        TestTranslationSessionAndMultiTurnReplay();
        TestThinkingStreamSanitizer();
        TestProviderThinkingParameters();
        TestOpenAiThinkingParameterRejectionFallback();
        TestProviderOutOfBandThinkingExtraction();
    }

    /// <summary>
    /// 把流式分片依次灌入分栏路由并收尾，返回（原文通道, 译文通道）的拼接文本。
    /// 需要按 chunk 断言实时性（即时冲刷、滑动窗口）的场景仍直接操作路由器。
    /// </summary>
    private static (string Source, string Translation) RouteChunks(StreamChannelMode mode, params string[] chunks)
    {
        var router = new SectionChannelRouter(mode);
        var deltas = chunks.SelectMany(chunk => router.Push(chunk)).Concat(router.Complete()).ToList();
        return (
            string.Concat(deltas.Where(delta => delta.Channel == DeltaChannel.SourceText).Select(delta => delta.Text)),
            string.Concat(deltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)));
    }

    /// <summary>把分片依次推入思考链清洗器并收尾，返回（干净正文, 思考内容）的拼接文本。</summary>
    private static (string Clean, string Thinking) SanitizeChunks(params string[] chunks)
    {
        var sanitizer = new ThinkingStreamSanitizer();
        var outputs = chunks.SelectMany(chunk => sanitizer.Push(chunk)).Concat(sanitizer.Complete()).ToList();
        return (
            string.Concat(outputs.Select(output => output.CleanText)),
            string.Concat(outputs.Select(output => output.ThinkingText)));
    }

    private static void TestTranslationPromptTemplates()
    {
        var request = new UnderstandRequest(UnderstandKind.Translate, "hello", null, "zh-CN");
        var defaultPrompt = PromptTemplates.GetSystemPrompt(request, new PromptConfig());

        // 文本路径解耦（issue 008 Phase 3）：模型只回译文——无哨兵、无注音规约、无原文回声
        Check(
            "default translation prompt carries no sentinel and no annotation conventions",
            defaultPrompt.Contains("zh-CN", StringComparison.Ordinal) &&
            !defaultPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            !defaultPrompt.Contains("振假名", StringComparison.Ordinal) &&
            !defaultPrompt.Contains("拼音", StringComparison.Ordinal) &&
            !defaultPrompt.Contains("IPA", StringComparison.Ordinal) &&
            !defaultPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var customPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig { Base = "Translate to {{target_language}}; retain {{unknown_placeholder}}." });
        Check(
            "custom translation prompt expands target language without swallowing unknown placeholders",
            customPrompt.StartsWith("Translate to zh-CN; retain {{unknown_placeholder}}.", StringComparison.Ordinal));

        var imageRequest = request with { ImagePng = new byte[] { 1 } };
        var defaultVisionPrompt = PromptTemplates.GetSystemPrompt(imageRequest, new PromptConfig());
        const string expectedVisionShape = "<transcribed_text>\n---TRANSLATION---\n<translation>";
        var normalizedVisionPrompt = defaultVisionPrompt.Replace("\r\n", "\n");
        Check(
            "default vision prompt keeps the transcription-then-translation protocol with the single sentinel",
            normalizedVisionPrompt.Contains("zh-CN", StringComparison.Ordinal) &&
            normalizedVisionPrompt.Contains(expectedVisionShape, StringComparison.Ordinal) &&
            !normalizedVisionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var visionPrompt = PromptTemplates.GetSystemPrompt(
            imageRequest,
            new PromptConfig { VisionBase = "OCR\n---TRANSLATION---\nTranslate to {{target_language}}; retain {{unknown_placeholder}}" });
        Check(
            "custom vision base is the only section and expands only the target language placeholder",
            visionPrompt.StartsWith("OCR\n---TRANSLATION---\nTranslate to zh-CN; retain {{unknown_placeholder}}", StringComparison.Ordinal) &&
            !visionPrompt.Contains(PromptTemplates.DefaultVisionBasePrompt, StringComparison.Ordinal) &&
            !visionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var pureModularPrompt = PromptTemplates.GetSystemPrompt(request, new PromptConfig());
        var expectedBase = PromptTemplates.DefaultTranslationBasePrompt.Replace(
            PromptTemplates.TargetLanguagePlaceholder, request.TargetLanguage, StringComparison.Ordinal);
        Check(
            "empty prompt config strictly builds the translation base without sentinel or explanation directives",
            !pureModularPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            pureModularPrompt.Contains(expectedBase, StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("### [词汇/短语]", StringComparison.Ordinal) &&
            !pureModularPrompt.Contains("### [语法/句型]", StringComparison.Ordinal));
    }

    /// <summary>校验内置提示词字面量的排版契约：标题与协议行顶格、规则条目统一缩进两个空格，零 CR、零首尾与行尾空白。</summary>
    private static void TestPromptLiteralFormatting()
    {
        var literals = new (string Name, string Value)[]
        {
            (nameof(PromptTemplates.DefaultTranslationBasePrompt), PromptTemplates.DefaultTranslationBasePrompt),
            (nameof(PromptTemplates.DefaultVisionBasePrompt), PromptTemplates.DefaultVisionBasePrompt),
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

        // 协议一致性（issue 008 Phase 3）：声明两段式输出的提示词必须且只能使用唯一哨兵；
        // 而文本 Base 已与哨兵、注音规约彻底解耦，一旦回潮立即失败
        (string Name, string Value)[] twoPartProtocols =
        [
            (nameof(PromptTemplates.DefaultVisionBasePrompt), PromptTemplates.DefaultVisionBasePrompt),
        ];
        foreach (var (name, value) in twoPartProtocols)
        {
            Check(
                $"two-part protocol prompt {name} declares only the single translation sentinel",
                value.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
                !value.Contains("\n---\n", StringComparison.Ordinal));
        }

        Check(
            "translation base stays decoupled from the sentinel and every annotation convention",
            !PromptTemplates.DefaultTranslationBasePrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            !PromptTemplates.DefaultTranslationBasePrompt.Contains("振假名", StringComparison.Ordinal) &&
            !PromptTemplates.DefaultTranslationBasePrompt.Contains("拼音", StringComparison.Ordinal) &&
            !PromptTemplates.DefaultTranslationBasePrompt.Contains("IPA", StringComparison.Ordinal));

        // 组装后的 system prompt 同样必须干净：无 CR、首尾无空白
        var assembled = new[]
        {
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"), new PromptConfig()),
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

    /// <summary>
    /// 文本路径解耦契约（issue 008 Phase 3）：提示词只负责翻译纪律，注音职责移交本地引擎。
    /// 因此文本 Base 与源语言彻底无关——日/英/中/混排给出同一句 system prompt，
    /// 既消除了按探测注入插件的组合爆炸，也让前缀缓存恒定命中。
    /// </summary>
    private static void TestTextPromptDecoupling()
    {
        var builtin = new PromptConfig();
        var sourceVariants = new[]
        {
            ("japanese", new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）")),
            ("pure-kanji", new UnderstandRequest(UnderstandKind.Translate, "進入禁止", null, "中文（简体）")),
            ("english", new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）")),
            ("chinese", new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, "中文（简体）")),
            ("mixed", new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）")),
            ("triple-mixed", new UnderstandRequest(UnderstandKind.Translate, "这是一个iPhoneの新しい発表会", null, "中文（简体）")),
        };

        // 1. 同一目标语言下，任何源语言都汇聚为同一句 system prompt（无插件、无分支）
        var promptsForChinese = sourceVariants.Select(item => PromptTemplates.GetSystemPrompt(item.Item2, builtin)).ToArray();
        Check(
            "text system prompt is a single source-independent variant with no language plugins",
            promptsForChinese.Distinct(StringComparer.Ordinal).Count() == 1);

        // 2. 差异只来自目标语言本身：换目标即换目标语言从句，别的分毫不动
        var englishTarget = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "English"), builtin);
        Check(
            "text system prompt varies only with the target language clause",
            englishTarget != promptsForChinese[0] &&
            englishTarget == promptsForChinese[0].Replace("中文（简体）", "English", StringComparison.Ordinal));

        // 3. 三个语言插件键即使留有旧值也绝不参与组装（旧配置迁移保留但已失效）
        var legacyPluginValues = new PromptConfig
        {
            Base = string.Empty,
            JapanesePlugin = "[Legacy ja-JP Plugin]",
            EnglishPlugin = "[Legacy en-US Plugin]",
            ChinesePlugin = "[Legacy zh-CN Plugin]",
        };
        var legacyComposed = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"), legacyPluginValues);
        Check(
            "retired language plugin values never re-enter prompt assembly",
            legacyComposed == promptsForChinese[0] &&
            !legacyComposed.Contains("[Legacy", StringComparison.Ordinal));

        // 4. 用户自定义 Base 仍然优先生效（占位符纪律不变）
        var customBase = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "zh-CN"),
            new PromptConfig { Base = "Custom base for {{target_language}}" });
        Check(
            "custom base prompt still takes priority on the text path",
            customBase == "Custom base for zh-CN");

        // 5. 精讲路径不受影响：仍复用唯一精讲提示词，且不含任何注音规约（精讲注记禁令里出现的「振假名」字样不算规约）
        var explainPrompt = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）"), builtin);
        Check(
            "explain path keeps the single explain prompt without annotation conventions",
            explainPrompt.Contains(PromptTemplates.DefaultExplainUserPrompt, StringComparison.Ordinal) &&
            !explainPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            !explainPrompt.Contains("标注振假名", StringComparison.Ordinal) &&
            !explainPrompt.Contains("标注拼音", StringComparison.Ordinal) &&
            !explainPrompt.Contains("标注国际音标", StringComparison.Ordinal));
    }

    /// <summary>
    /// 视觉链路解耦契约：视觉 Base 单独组装成完整 system prompt（无语言插件），
    /// 协议形态为「纯转录 + 哨兵 + 译文」；目标语言只改写译文从句，仍是单一缓存友好变体。
    /// </summary>
    private static void TestVisionPromptDecoupling()
    {
        var imageRequest = new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "中文（简体）");
        var visionPrompt = PromptTemplates.GetSystemPrompt(imageRequest, new PromptConfig());

        var expectedVisionBase = PromptTemplates.DefaultVisionBasePrompt.Replace(
            PromptTemplates.TargetLanguagePlaceholder, imageRequest.TargetLanguage, StringComparison.Ordinal);

        // 1. 视觉 Base 单独成段：转录协议、哨兵与目标语言齐备，无任何注音规约
        Check(
            "default vision prompt is the vision base alone with transcription protocol and sentinel",
            visionPrompt == expectedVisionBase &&
            visionPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            !visionPrompt.Contains("振假名", StringComparison.Ordinal) &&
            !visionPrompt.Contains("拼音", StringComparison.Ordinal) &&
            !visionPrompt.Contains("IPA", StringComparison.Ordinal));

        // 2. 视觉 system prompt 与源文本无关：空文本 / 英文 / 日文请求给出同一句（恒定单变体）
        var visionVariants = new[]
        {
            PromptTemplates.GetSystemPrompt(imageRequest, new PromptConfig()),
            PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "Hello world", new byte[] { 1 }, "中文（简体）"),
                new PromptConfig()),
            PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", new byte[] { 1 }, "中文（简体）"),
                new PromptConfig()),
        };
        Check(
            "vision system prompt stays one cache-friendly variant regardless of the source text",
            visionVariants.Distinct(StringComparer.Ordinal).Count() == 1);

        // 3. 退役插件键的旧值不会借视觉路径复活；自定义视觉 Base 优先生效
        var customVision = PromptTemplates.GetSystemPrompt(
            imageRequest,
            new PromptConfig
            {
                VisionBase = "[Custom vision base]",
                JapanesePlugin = "[Legacy ja-JP Plugin]",
                EnglishPlugin = "[Legacy en-US Plugin]",
            });
        Check(
            "custom vision base takes priority and retired plugin values stay inert on the vision path",
            customVision == "[Custom vision base]" &&
            !customVision.Contains("[Legacy", StringComparison.Ordinal));

        // 4. 目标语言互斥已成历史：各目标下视觉 prompt 仍是唯一变体，只差目标语言从句
        var targetVariants = new[] { "英语", "日本語", "中文（简体）", "韩语" }
            .Select(target => PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, target), new PromptConfig()))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Check(
            "vision prompt has exactly one variant per target language after plugin retirement",
            targetVariants == 4);
    }

    /// <summary>
    /// 段描述反漂移闸门：DescribeSections 只是 GetSystemPrompt 的「不拼接版本」，
    /// 是 --dump-prompt 与 prompt-preview 的唯一真值源。两者一旦漂移，预览页与 CLI
    /// 展示的就不再是真实下发的内容，因此这里钉住三条路径、自定义覆盖与多轮会话锁定值。
    /// </summary>
    private static void TestPromptSectionDescription()
    {
        var builtin = new PromptConfig();
        var cases = new (string Name, UnderstandRequest Request, PromptConfig Prompts)[]
        {
            ("text/japanese", new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）"), builtin),
            ("text/english", new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"), builtin),
            ("text/simplified-chinese", new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, "中文（简体）"), builtin),
            ("text/mixed", new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"), builtin),
            ("vision", new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "中文（简体）"), builtin),
            ("explain", new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）"), builtin),
            ("text/custom", new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"),
                new PromptConfig { Base = "Custom base for {{target_language}}" }),
            ("vision/custom", new UnderstandRequest(UnderstandKind.Translate, "text", new byte[] { 1 }, "中文（简体）"),
                new PromptConfig { VisionBase = "[Custom vision base]" }),
            ("explain/custom", new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）"),
                new PromptConfig { Explain = "Custom explain for {{target_language}}" }),
        };

        foreach (var (name, request, prompts) in cases)
        {
            var sections = PromptTemplates.DescribeSections(request, prompts);
            var joined = string.Join("\n\n", sections.Select(section => section.Text));
            Check(
                $"prompt sections for {name} rejoin into exactly the dispatched system prompt",
                sections.Count > 0 &&
                sections.All(section => !string.IsNullOrWhiteSpace(section.Text)) &&
                joined.Replace(PromptTemplates.TargetLanguagePlaceholder, request.TargetLanguage, StringComparison.Ordinal) ==
                PromptTemplates.GetSystemPrompt(request, prompts));
        }

        // 来源标注：未定制项回落 Builtin，定制项标 Custom——预览页的徽章全靠这一位。
        // 注音插件键退役后（issue 008 Phase 3），文本路径只剩 base 一段，来源徽章只在这一段上翻转。
        Check(
            "prompt sections distinguish built-in fallbacks from custom overrides",
            PromptTemplates.DescribeSections(
                    new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）"), builtin)
                .Select(section => (section.Name, section.Source))
                .SequenceEqual(new[] { ("base", PromptSectionSource.Builtin) }) &&
            PromptTemplates.DescribeSections(
                    new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"),
                    new PromptConfig { Base = "Custom base" })
                .Select(section => (section.Name, section.Source))
                .SequenceEqual(new[] { ("base", PromptSectionSource.Custom) }));

        // 多轮会话：系统提示词原样沿用阶段一锁定值，既不分拆也不二次展开占位符。
        var session = new TranslationSession("locked {{target_language}} prompt", "通れない", null);
        var stage2Request = new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）", session);
        var lockedSections = PromptTemplates.DescribeSections(stage2Request, builtin);
        Check(
            "session-locked system prompt is reported as a single session section without re-expansion",
            lockedSections.Count == 1 &&
            lockedSections[0].Source == PromptSectionSource.Session &&
            lockedSections[0].Text == "locked {{target_language}} prompt" &&
            PromptTemplates.GetSystemPrompt(stage2Request, builtin) == "locked {{target_language}} prompt");

        // 路径分派顺序必须与 GetSystemPrompt 一致：精讲压过视觉，视觉压过文本。
        var dispatch = new (PromptPath Actual, PromptPath Expected)[]
        {
            (PromptTemplates.ResolvePath(new UnderstandRequest(UnderstandKind.Translate, "text", null, "中文（简体）")), PromptPath.Text),
            (PromptTemplates.ResolvePath(new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "中文（简体）")), PromptPath.Vision),
            (PromptTemplates.ResolvePath(new UnderstandRequest(UnderstandKind.Explain, "text", null, "中文（简体）")), PromptPath.Explain),
            (PromptTemplates.ResolvePath(new UnderstandRequest(UnderstandKind.Explain, "text", new byte[] { 1 }, "中文（简体）")), PromptPath.Explain),
        };
        Check(
            "prompt path resolution mirrors GetSystemPrompt dispatch order (explain over vision over text)",
            dispatch.All(item => item.Actual == item.Expected));
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

        var entryForbiddenEn = LanguageDetector.Detect("進入禁止", "English");
        Check(
            "detector identifies pure-kanji Japanese phrases and recommends ruby when target is English",
            entryForbiddenEn.HasJapanese && entryForbiddenEn.NeedsJapaneseRuby && entryForbiddenEn.IsPureHanziJapaneseCandidate);

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

        // 6. 日英混排（NeedsEnglishIpa 维持「纯拉丁」口径，混排句的英语规约由 HasEnglish 门控携带）
        var japaneseEnglish = LanguageDetector.Detect("Appleの新しいiPhone発表会");
        Check(
            "detector identifies mixed Japanese and English text",
            japaneseEnglish.HasJapanese && japaneseEnglish.HasEnglish && japaneseEnglish.NeedsJapaneseRuby &&
            !japaneseEnglish.NeedsEnglishIpa);

        // 7. 日英中三混排：简体独占字特征独立于假名置位，中文拼音门控据此纳入拼音规约
        var tripleMixed = LanguageDetector.Detect("这是一个iPhoneの新しい発表会");
        Check(
            "detector flags simplified chinese independently of kana for triple mixed text",
            tripleMixed.HasJapanese && tripleMixed.HasEnglish && tripleMixed.HasSimplifiedChinese &&
            tripleMixed.NeedsJapaneseRuby && !tripleMixed.NeedsEnglishIpa);
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
            RubyTextParser.ToReadingText(sample1) == "こ こ は  とおれないよ");

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

    /// <summary>
    /// 分栏路由契约（issue 008 Phase 3）：通道归属由 <see cref="StreamChannelMode"/> 显式给出——
    /// 两段式（Segmenter）按哨兵拆转录/译文；直通（Passthrough）无原文通道，哨兵字面量也不过界。
    /// 曾经的「显式纯译文判定 / 首字内容猜测」已退役，不得以任何形式回归。
    /// </summary>
    private static void TestSentinelRouter()
    {
        // ── 两段式：视觉链路协议 ────────────────────────────────
        var (legacySource, legacyTranslation) = RouteChunks(StreamChannelMode.Segmenter, "私[わたし]は\n---\n学生[がくせい]です。");
        Check(
            "section router no longer recognizes the legacy bare markdown rule as a separator",
            legacySource == "私[わたし]は\n---\n学生[がくせい]です。" && legacyTranslation.Length == 0);

        var (crlfSource, crlfTranslation) = RouteChunks(StreamChannelMode.Segmenter, "原文\r\n---TRANS", "LATION---\r\n译文");
        Check(
            "section router splits the translation sentinel across frames and absorbs CRLF padding",
            crlfSource == "原文" && crlfTranslation == "译文");

        var (paddedSource, paddedTranslation) = RouteChunks(StreamChannelMode.Segmenter, "原文\n\n---TRANS", "LATION---\n\n译文");
        Check(
            "section router consumes the blank lines around the translation sentinel",
            paddedSource == "原文" && paddedTranslation == "译文");

        var (fallbackSource, fallbackTranslation) = RouteChunks(StreamChannelMode.Segmenter, "\n\nplain translation");
        Check(
            "section router keeps unseparated vision output as a trimmed translation",
            fallbackSource.Length == 0 && fallbackTranslation == "plain translation");

        var (markerSource, markerTranslation) = RouteChunks(StreamChannelMode.Segmenter, "原文\n---TRANS", "LATION---\n我是");
        Check(
            "section router handles ---TRANSLATION--- marker split across chunks",
            markerSource == "原文" && markerTranslation == "我是");

        var (caseSource, caseTranslation) = RouteChunks(StreamChannelMode.Segmenter, "<transcribed_text>\r\nここは通[とお]れないよ\r\n</transcribed_text>\r\n---translation---\r\n<translation>\r\n这里过不去哦\r\n</translation>");
        Check(
            "section router handles CRLF, case-insensitive sentinel, and strips pseudo XML tags",
            caseSource == "ここは通[とお]れないよ" && caseTranslation == "这里过不去哦");

        // 现行视觉 Base 提示词声明的协议形态必须能被路由层无损拆分（提示词与路由层不得漂移）
        var (protocolSource, protocolTranslation) = RouteChunks(
            StreamChannelMode.Segmenter,
            """
            <transcribed_text>
            ここは通れないよ
            </transcribed_text>
            ---TRANSLATION---
            <translation>
            我是学生。
            </translation>
            """);
        Check(
            "section router splits the exact two-part shape declared by the default vision prompt",
            protocolSource == "ここは通れないよ" && protocolTranslation == "我是学生。");

        var immediateStreamRouter = new SectionChannelRouter(StreamChannelMode.Segmenter);
        _ = immediateStreamRouter.Push("原文\n---TRANSLATION---\n").ToList();
        var immediateChunk = immediateStreamRouter.Push("短译文").ToList();
        Check(
            "section router flushes translation tokens immediately in real-time without buffering",
            immediateChunk.Count == 1 &&
            immediateChunk[0].Channel == DeltaChannel.Translation &&
            immediateChunk[0].Text == "短译文");

        var sourceStreamRouter = new SectionChannelRouter(StreamChannelMode.Segmenter);
        var earlySourceDeltas = sourceStreamRouter.Push("ここは通れないよ").ToList();
        Check(
            "section router flushes transcribed tokens in real-time before sentinel arrives",
            earlySourceDeltas.Count > 0 &&
            earlySourceDeltas.All(delta => delta.Channel == DeltaChannel.SourceText) &&
            string.Concat(earlySourceDeltas.Select(delta => delta.Text)) == "ここは通れないよ");

        var splitSentinelDeltas = sourceStreamRouter.Push("\n---TRANS").ToList();
        Check(
            "section router holds trailing sentinel prefix in sliding window without leaking to source channel",
            splitSentinelDeltas.Count == 0);

        var continuationDeltas = sourceStreamRouter.Push("LATION---\n我是学生。").Concat(sourceStreamRouter.Complete()).ToList();
        Check(
            "section router completes sentinel match and streams translation seamlessly",
            string.Concat(continuationDeltas.Where(delta => delta.Channel == DeltaChannel.Translation).Select(delta => delta.Text)) == "我是学生。");

        var (leakSource, leakTranslation) = RouteChunks(
            StreamChannelMode.Segmenter,
            "<transcribed_text>\r\nここは通れないよ。",
            "\r\n</transcribed_text>\r\n",
            "---TRANSLATION---\r\n<translation>\r\n我是学生。\r\n</translation>");
        Check(
            "section router strictly prevents any pseudo XML closing tags from leaking into source channel",
            !leakSource.Contains("</transcribed_text>") &&
            !leakSource.Contains("<transcribed_text>") &&
            leakSource == "ここは通れないよ。" &&
            leakTranslation == "我是学生。");

        var (bracketSource, bracketTranslation) = RouteChunks(
            StreamChannelMode.Segmenter,
            "[transcribed_text]\n亡なくなった\n[/transcribed_text]\n---TRANSLATION---\n<translation>\n逝去了\n</translation>\n\n### [词汇/短语]\n- 亡くなる：逝世");
        Check(
            "section router strips square bracket tags, trailing </translation> and truncates rogue explanation markdown",
            !bracketSource.Contains("[transcribed_text]") &&
            !bracketSource.Contains("[/transcribed_text]") &&
            bracketSource == "亡なくなった" &&
            !bracketTranslation.Contains("</translation>") &&
            !bracketTranslation.Contains("###") &&
            bracketTranslation == "逝去了");

        var (rogueSource, rogueTranslation) = RouteChunks(
            StreamChannelMode.Segmenter,
            "<transcribed_text>\n新歓で気が大きくなってしまう話\n</transcribed_text>\n---TRANSLATION---\n在迎新活动中变得胆大妄为的故事\n\n## 深度精讲拆解\n- 新歓：迎新活动");
        Check(
            "section router truncates ## 深度精讲拆解 and rogue explanation sections from translation channel",
            !rogueTranslation.Contains("## 深度精讲拆解") &&
            !rogueTranslation.Contains("新歓") &&
            rogueTranslation == "在迎新活动中变得胆大妄为的故事");

        var (longSource, longTranslation) = RouteChunks(StreamChannelMode.Segmenter, "这是一段非常长的纯译文，完全没有包含任何原文分段标记，长度肯定超过了三十二个字符的安全窗口预算。");
        Check(
            "section router preserves long pure translation without sentinel as translation channel without premature source flush",
            longSource.Length == 0 &&
            longTranslation == "这是一段非常长的纯译文，完全没有包含任何原文分段标记，长度肯定超过了三十二个字符的安全窗口预算。");

        var (pureBracketSource, pureBracketTranslation) = RouteChunks(StreamChannelMode.Segmenter, "这是【限量版】[原创]周边商品，制作非常方便。");
        Check(
            "section router does not confuse bracketed words in pure translation with ruby annotations",
            pureBracketSource.Length == 0 &&
            pureBracketTranslation == "这是【限量版】[原创]周边商品，制作非常方便。");

        // ── 直通：文本路径只回译文，哨兵也只是译文里的字面量 ────
        var (passthroughSource, passthroughTranslation) = RouteChunks(StreamChannelMode.Passthrough, "私[わたし]は\n---TRANSLATION---\n我是学生。");
        Check(
            "passthrough router keeps the sentinel as literal translation text without opening a source channel",
            passthroughSource.Length == 0 &&
            passthroughTranslation == "私[わたし]は\n---TRANSLATION---\n我是学生。");

        // 直通路径的实时性：译文增量立即流出，不做任何原文特征的挂起等待
        var passthroughStreamRouter = new SectionChannelRouter(StreamChannelMode.Passthrough);
        var passthroughChunks = passthroughStreamRouter.Push("这是一段译文").ToList();
        Check(
            "passthrough router streams translation tokens immediately without source-signal buffering",
            passthroughChunks.Count > 0 &&
            passthroughChunks.All(delta => delta.Channel == DeltaChannel.Translation) &&
            string.Concat(passthroughChunks.Select(delta => delta.Text)) == "这是一段译文");

        // 直通路径仍守住两道守门：思考链越界截断 + 噪声标签剥离
        var (guardSource, guardTranslation) = RouteChunks(StreamChannelMode.Passthrough, "【译文】\n这里过不去哦。\n\n### [词汇/短语]\n- 通れない：不能通过");
        Check(
            "passthrough router strips translation noise labels and truncates rogue explanation sections",
            guardSource.Length == 0 && guardTranslation == "这里过不去哦。");

    }

    /// <summary>
    /// 噪声标签清单校准契约（阶段二遗留项 A）：兜底标签必须与现行两段式协议的产生源一一对应。
    /// 现行视觉 Base 声明 &lt;transcribed_text&gt; 占位符，必须全覆盖；
    /// 旧版英文视觉提示词的 &lt;transcription&gt; 系列与旧注音协议的 annotated_source 外壳不得回归。
    /// </summary>
    private static void TestNoiseTagCalibration()
    {
        // 现行提示词声明的结构占位符必须保留兜底（含方括号与冒号变体）
        Check(
            "noise tag lists keep covering the placeholders declared by the current two-part protocol",
            SectionChannelRouter.SourceLeadingTags.Contains("<transcribed_text>") &&
            SectionChannelRouter.SourceLeadingTags.Contains("[transcribed_text]") &&
            SectionChannelRouter.SourceLeadingTags.Contains("transcribed_text:") &&
            SectionChannelRouter.SourceCloseTags.Contains("</transcribed_text>") &&
            SectionChannelRouter.SourceCloseTags.Contains("[/transcribed_text]") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("<translation>") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("[translation]") &&
            SectionChannelRouter.TranslationLeadingTags.Contains("translation:") &&
            SectionChannelRouter.TranslationCloseTags.Contains("</translation>") &&
            SectionChannelRouter.TranslationCloseTags.Contains("[/translation]"));

        // 旧注音协议的外壳标签降级为「模型惯性兜底」：仍收纳，但已无提示词产生源（回归即协议漂移警报）
        Check(
            "noise tag lists keep the retired annotated_source shells as model-inertia fallbacks only",
            SectionChannelRouter.SourceLeadingTags.Contains("<annotated_source>") &&
            SectionChannelRouter.SourceLeadingTags.Contains("annotated_source:") &&
            SectionChannelRouter.SourceCloseTags.Contains("</annotated_source>"));

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
        // null 与零长度图像负载都等同“未提供图像”，不得触发视觉能力校验
        var nullImageRequest = new UnderstandRequest(UnderstandKind.Translate, "text", null, "zh-CN");
        var zeroLengthImageRequest = new UnderstandRequest(UnderstandKind.Translate, "text", new ReadOnlyMemory<byte>([]), "zh-CN");
        var nullOrZeroLengthImageAcceptedAsText = true;
        try
        {
            ProviderFactory.ValidateCapabilities(nullImageRequest, capabilities);
            ProviderFactory.ValidateCapabilities(zeroLengthImageRequest, capabilities);
        }
        catch (ProviderCapabilityException)
        {
            nullOrZeroLengthImageAcceptedAsText = false;
        }

        Check("null and zero-length image payloads are treated as absent", nullOrZeroLengthImageAcceptedAsText);
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
        var session = new TranslationSession("system_test_prompt", "こ こ は 通れないよ", imageBytes);
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
        var openAiProfile = new ProviderProfile { ApiType = "chat_completions", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" };
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
        var claudeProfile = new ProviderProfile { ApiType = "messages", BaseUrl = "https://api.anthropic.com", Model = "claude-3-5-sonnet-20241022" };
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
        var geminiProfile = new ProviderProfile { ApiType = "gemini", BaseUrl = "https://generativelanguage.googleapis.com", Model = "gemini-1.5-pro" };
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

    private static void TestThinkingStreamSanitizer()
    {
        // 1. 基础隔离：单 chunk 内包含完整 <think> 思考链
        var (inlineClean, inlineThinking) = SanitizeChunks("<think>思考词性与语法</think>私[わたし]は 学生[がくせい]です。");
        Check(
            "ThinkingStreamSanitizer separates full inline <think> tags into thinking and clean text",
            inlineClean == "私[わたし]は 学生[がくせい]です。" &&
            inlineThinking == "思考词性与语法");

        // 2. 跨 chunk 标签拆分（<thi + nk> 与 </thi + nk>）
        var (chunkClean, chunkThinking) = SanitizeChunks("<thi", "nk>深度思考中", "</thi", "nk>\n你好世界");
        Check(
            "ThinkingStreamSanitizer buffers cross-chunk open and close tags correctly",
            chunkClean == "你好世界" &&
            chunkThinking == "深度思考中");

        // 3. 思考流异常中断未闭合：不向干净正文通道泄漏任何字符
        var (unclosedClean, unclosedThinking) = SanitizeChunks("<think>未完成的思考");
        Check(
            "ThinkingStreamSanitizer preserves unclosed thinking content and never leaks to clean channel",
            string.IsNullOrEmpty(unclosedClean) &&
            unclosedThinking == "未完成的思考");

        // 4. 思考内部包含 '<' 符号（如 1 < 2）不被误判为闭标签
        var (mathClean, mathThinking) = SanitizeChunks("<think>比较 1 < 2 与 3 > 2</think>正文");
        Check(
            "ThinkingStreamSanitizer tolerates less-than signs inside thinking block",
            mathClean == "正文" &&
            mathThinking == "比较 1 < 2 与 3 > 2");

        // 5. 非思考标签前缀退回（如 <annotated_source>）原样输出为干净正文
        var (normalClean, normalThinking) = SanitizeChunks("<annotated_source>原文</annotated_source>");
        Check(
            "ThinkingStreamSanitizer passes through non-thinking xml tags as clean text",
            normalClean == "<annotated_source>原文</annotated_source>" &&
            string.IsNullOrEmpty(normalThinking));

        // 6. 孤立闭合标签（如以 </think> 开头的模型流）静默吞除，且吞掉随后的换行符
        var (isolatedClean, isolatedThinking) = SanitizeChunks("</think>\n\n你好世界");
        Check(
            "ThinkingStreamSanitizer silently swallows isolated close tag and trailing newlines",
            isolatedClean == "你好世界" &&
            string.IsNullOrEmpty(isolatedThinking));

        // 7. 跨 chunk 切分的孤立闭合标签（如 </thi + nk> + \n\n + 纯译文）
        var (splitCloseClean, splitCloseThinking) = SanitizeChunks("</thi", "nk>\n\n", "纯译文内容");
        Check(
            "ThinkingStreamSanitizer handles cross-chunk isolated close tag and newlines cleanly",
            splitCloseClean == "纯译文内容" &&
            string.IsNullOrEmpty(splitCloseThinking));

        // 8. 结合 SectionChannelRouter 验证孤立闭合标签不破坏纯译文 fallback
        var router = new SectionChannelRouter(StreamChannelMode.Passthrough);
        var testSanitizer = new ThinkingStreamSanitizer();
        var d1 = TranslationProviderBase.Route(router, testSanitizer, "</think>", UnderstandKind.Translate);
        var d2 = TranslationProviderBase.Route(router, testSanitizer, "\n\n这是纯译文结果。", UnderstandKind.Translate);
        var dEnd = TranslationProviderBase.CompleteStream(router, testSanitizer, UnderstandKind.Translate);
        var allDeltas = d1.Concat(d2).Concat(dEnd).ToList();
        Check(
            "Isolated close tag with plain translation successfully fallbacks to Translation channel without leaking to SourceText",
            allDeltas.All(d => d.Channel == DeltaChannel.Translation) &&
            string.Concat(allDeltas.Select(d => d.Text)) == "这是纯译文结果。");
    }

    /// <summary>
    /// 各 Provider 的思考参数接线：官方端点与第三方兼容端点口径不同（reasoning_effort / enable_thinking / thinking.type），
    /// Claude 关闭时严禁下发 thinking，Gemini 把 thinkingBudget 收在 generationConfig 内。
    /// 「构造 Provider → 发请求 → 解析请求体」的重复动作收敛为四个本地函数。
    /// </summary>
    private static void TestProviderThinkingParameters()
    {
        var request = new UnderstandRequest(UnderstandKind.Translate, "测试", null, "zh-CN");

        JsonDocument ParseBody(HttpRequestMessage message) =>
            JsonDocument.Parse(message.Content!.ReadAsStringAsync().Result);

        JsonDocument OpenAiCompletions(ProviderProfile profile) =>
            ParseBody(new OpenAiProvider(profile, "key", new PromptConfig()).CreateCompletionsRequest(request));

        JsonDocument OpenAiResponses(ProviderProfile profile) =>
            ParseBody(new OpenAiProvider(profile, "key", new PromptConfig()).CreateResponsesRequest(request));

        JsonDocument ClaudeBody(ProviderProfile profile) =>
            ParseBody(new ClaudeProvider(profile, "key", new PromptConfig()).CreateRequest(request));

        JsonDocument GeminiBody(ProviderProfile profile) =>
            ParseBody(new GeminiProvider(profile, "key", new PromptConfig()).CreateRequest(request));

        // 1. 官方 OpenAI端点：completions 模式
        using var offDoc = OpenAiCompletions(new ProviderProfile
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            EnableThinking = false,
        });
        Check(
            "Official OpenAI with EnableThinking=false does not inject enable_thinking or reasoning_effort",
            !offDoc.RootElement.TryGetProperty("enable_thinking", out _) &&
            !offDoc.RootElement.TryGetProperty("reasoning_effort", out _));

        using var onO1Doc = OpenAiCompletions(new ProviderProfile
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "o1-mini",
            EnableThinking = true,
        });
        Check(
            "Official OpenAI with EnableThinking=true and o1 model injects reasoning_effort=low but never enable_thinking",
            !onO1Doc.RootElement.TryGetProperty("enable_thinking", out _) &&
            onO1Doc.RootElement.GetProperty("reasoning_effort").GetString() == "low");

        // 2. 官方 OpenAI端点：responses 模式
        using var respDoc = OpenAiResponses(new ProviderProfile
        {
            ApiType = "responses",
            BaseUrl = "https://api.openai.com/v1",
            Model = "o3-mini",
            EnableThinking = true,
        });
        Check(
            "OpenAI Responses API with EnableThinking=true injects reasoning object and never enable_thinking",
            !respDoc.RootElement.TryGetProperty("enable_thinking", out _) &&
            respDoc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "low");

        // 3. 第三方兼容端点（如 SiliconFlow / Qwen / DeepSeek）
        using var compatOffDoc = OpenAiCompletions(new ProviderProfile
        {
            BaseUrl = "https://api.siliconflow.cn/v1",
            Model = "Qwen/Qwen2.5-7B",
            EnableThinking = false,
        });
        Check(
            "OpenAI compatible provider with EnableThinking=false injects enable_thinking=false and thinking.type=disabled",
            compatOffDoc.RootElement.GetProperty("enable_thinking").GetBoolean() == false &&
            compatOffDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled");

        using var compatOnDoc = OpenAiCompletions(new ProviderProfile
        {
            BaseUrl = "https://api.siliconflow.cn/v1",
            Model = "deepseek-ai/DeepSeek-R1",
            EnableThinking = true,
        });
        Check(
            "OpenAI compatible provider with EnableThinking=true injects enable_thinking=true and thinking.type=enabled",
            compatOnDoc.RootElement.GetProperty("enable_thinking").GetBoolean() == true &&
            compatOnDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled");

        // 3.1 Homura 模型提示词直译对齐测试
        var homuraProvider = new OpenAiProvider(
            new ProviderProfile
            {
                BaseUrl = "https://index-translate.bilibili.com/v1",
                Model = "index-homura-9b",
            },
            "key",
            new PromptConfig());
        var homuraRequest = new UnderstandRequest(UnderstandKind.Translate, "オリジナルグッズが簡単につくれるサービスです。", null, "中文（简体）");
        using var homuraMsg = homuraProvider.CreateCompletionsRequest(homuraRequest);
        using var homuraDoc = JsonDocument.Parse(homuraMsg.Content!.ReadAsStringAsync().Result);
        var homuraSystemContent = homuraDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Check(
            "OpenAI provider with homura model aligns prompt to direct translation",
            homuraSystemContent != null &&
            homuraSystemContent.Contains("请将以下日语文本翻译为中文（简体）") &&
            homuraSystemContent.Contains("直接输出翻译结果"));

        // 4. Claude Provider：关闭时严禁下发 thinking，开启时注入 budget_tokens
        using var claudeOffDoc = ClaudeBody(new ProviderProfile
        {
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-3-5-sonnet-20241022",
            EnableThinking = false,
        });
        Check(
            "Claude provider with EnableThinking=false completely omits thinking parameter to avoid 400 rejection",
            !claudeOffDoc.RootElement.TryGetProperty("thinking", out _));

        using var claudeOnDoc = ClaudeBody(new ProviderProfile
        {
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-3-7-sonnet-20250219",
            EnableThinking = true,
        });
        Check(
            "Claude provider with EnableThinking=true injects enabled thinking with budget_tokens 1024",
            claudeOnDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
            claudeOnDoc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32() == 1024);

        // 5. Gemini Provider：嵌套在 generationConfig 内
        using var geminiOffDoc = GeminiBody(new ProviderProfile
        {
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.0-flash",
            EnableThinking = false,
        });
        Check(
            "Gemini provider with EnableThinking=false nests thinkingBudget=0 inside generationConfig",
            geminiOffDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32() == 0);

        using var geminiOnDoc = GeminiBody(new ProviderProfile
        {
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.0-flash",
            EnableThinking = true,
        });
        Check(
            "Gemini provider with EnableThinking=true nests thinkingBudget=-1 inside generationConfig",
            geminiOnDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32() == -1);
    }

    /// <summary>
    /// 严格校验端点（NVIDIA）把非标思考参数整单打回（issue 022）：provider 必须去掉 enable_thinking / thinking
    /// 重试一次并正常出流，且记住该端点方言，让后续请求一开始就不带这两个参数；与思考无关的 400 不得重试。
    /// </summary>
    private static void TestOpenAiThinkingParameterRejectionFallback()
    {
        var profile = new ProviderProfile
        {
            BaseUrl = "https://integrate.api.nvidia.com/v1",
            Model = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",
            EnableThinking = false,
        };
        var request = new UnderstandRequest(UnderstandKind.Translate, "こんにちは", null, "中文（简体）");

        // 首请求带非标参数被 NVIDIA 打回，第二次去掉后正常流式，第三次应一开始就不带。
        var handler = new ScriptedHandler(
            Rejected("Validation: Unsupported parameter(s): `enable_thinking`, `thinking`."),
            Streaming("你好世界"),
            Streaming("第二次请求"));
        using var provider = new OpenAiProvider(profile, "key", new PromptConfig(), handler);

        var first = Drain(provider, request);
        var second = Drain(provider, request);

        Check(
            "Endpoint rejecting thinking parameters retries once without them and streams translation",
            handler.Requests.Count == 3 &&
            first.Any(delta => delta.Channel == DeltaChannel.Translation && delta.Text == "你好世界") &&
            second.Any(delta => delta.Channel == DeltaChannel.Translation && delta.Text == "第二次请求"));

        Check(
            "Retry and follow-up requests omit enable_thinking, thinking and reasoning_effort",
            handler.Requests[0].Contains("enable_thinking", StringComparison.Ordinal) &&
            handler.Requests[1].Contains("enable_thinking", StringComparison.Ordinal) == false &&
            handler.Requests[1].Contains("\"thinking\"", StringComparison.Ordinal) == false &&
            handler.Requests[1].Contains("reasoning_effort", StringComparison.Ordinal) == false &&
            handler.Requests[2].Contains("enable_thinking", StringComparison.Ordinal) == false);

        // 与思考参数无关的 400（坏 Key、模型不存在）必须原样抛出，不做无谓重试。
        var unrelatedHandler = new ScriptedHandler(Rejected("model not found"));
        using var unrelatedProvider = new OpenAiProvider(profile, "key", new PromptConfig(), unrelatedHandler);
        string? error = null;
        try
        {
            Drain(unrelatedProvider, request);
        }
        catch (ProviderHttpException ex)
        {
            error = ex.ServerMessage;
        }

        Check(
            "Unrelated 400 rejection surfaces without retrying",
            unrelatedHandler.Requests.Count == 1 && error == "model not found");

        // 只点名 reasoning_effort 的严格端点同样走去参重试（重试会连它一起省掉）。
        var effortHandler = new ScriptedHandler(
            Rejected("Unsupported parameter(s): `reasoning_effort`"),
            Streaming("思考参数全去掉"));
        var effortProfile = profile.Clone();
        effortProfile.Model = "o1-mini";
        effortProfile.EnableThinking = true;
        using var effortProvider = new OpenAiProvider(effortProfile, "key", new PromptConfig(), effortHandler);
        var effortDeltas = Drain(effortProvider, request);

        Check(
            "Endpoint rejecting reasoning_effort retries without the whole thinking parameter family",
            effortHandler.Requests.Count == 2 &&
            effortHandler.Requests[0].Contains("reasoning_effort", StringComparison.Ordinal) &&
            effortHandler.Requests[1].Contains("reasoning_effort", StringComparison.Ordinal) == false &&
            effortDeltas.Any(delta => delta.Channel == DeltaChannel.Translation && delta.Text == "思考参数全去掉"));
    }

    /// <summary>同步收干 provider 的流式增量；自测主循环是同步的。</summary>
    private static List<UnderstandDelta> Drain(ITranslationProvider provider, UnderstandRequest request)
    {
        var deltas = new List<UnderstandDelta>();
        var enumerator = provider.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                deltas.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return deltas;
    }

    /// <summary>服务端 400 回包；报文走 error.message，与 ProviderHttpException 的解析口径一致。</summary>
    private static HttpResponseMessage Rejected(string message) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } })),
    };

    /// <summary>一段成功的 SSE 流：单个 content 增量后 [DONE]。</summary>
    private static HttpResponseMessage Streaming(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\ndata: [DONE]\n\n"),
    };

    /// <summary>按脚本依次回包的假 handler：记录请求体，用于复现服务端 400 与成功流（issue 022）。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = [];

        public ScriptedHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        /// <summary>按发送顺序记录的请求体。</summary>
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return _responses.Dequeue();
        }
    }

    private static void TestProviderOutOfBandThinkingExtraction()
    {
        // 1. DeepSeek / OpenAI 兼容 completions：choices[0].delta.reasoning_content
        const string deepSeekFrame = """
        {
            "choices": [
                {
                    "delta": {
                        "reasoning_content": "DeepSeek思考过程片段"
                    }
                }
            ]
        }
        """;
        var (dsText, dsThinking) = OpenAiProvider.ExtractCompletionsDelta(deepSeekFrame);
        Check(
            "ExtractCompletionsDelta captures reasoning_content into Thinking stream",
            string.IsNullOrEmpty(dsText) && dsThinking == "DeepSeek思考过程片段");

        // 2. OpenAI responses：response.reasoning_text.delta
        var respFrame = new SseEvent(
            "response.reasoning_text.delta",
            """{"type":"response.reasoning_text.delta","delta":"OpenAI思考中"}""");
        var (respText, respThinking) = OpenAiProvider.ExtractResponsesDelta(respFrame);
        Check(
            "ExtractResponsesDelta captures response.reasoning_text.delta into Thinking stream",
            string.IsNullOrEmpty(respText) && respThinking == "OpenAI思考中");

        // 3. Claude：thinking_delta 事件
        var claudeFrame = new SseEvent(
            "content_block_delta",
            """{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"Claude思考过程"}}""");
        var (claudeText, claudeThinking) = ClaudeProvider.ExtractDelta(claudeFrame);
        Check(
            "Claude ExtractDelta captures thinking_delta into Thinking stream",
            string.IsNullOrEmpty(claudeText) && claudeThinking == "Claude思考过程");

        // 4. Gemini：candidates[0].content.parts[0].thought == true
        const string geminiFrame = """
        {
            "candidates": [
                {
                    "content": {
                        "parts": [
                            {
                                "text": "Gemini思考过程",
                                "thought": true
                            },
                            {
                                "text": "正式译文"
                            }
                        ]
                    }
                }
            ]
        }
        """;
        var (geminiText, geminiThinking) = GeminiProvider.ExtractDelta(geminiFrame);
        Check(
            "Gemini ExtractDelta separates thought parts from regular text parts",
            geminiText == "正式译文" && geminiThinking == "Gemini思考过程");
    }
}

