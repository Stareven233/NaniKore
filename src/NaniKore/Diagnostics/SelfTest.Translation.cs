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
        TestVisionPromptComposition();
        TestMixedLanguagePluginGating();
        TestTargetLanguagePluginMutex();
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
        TestProviderOutOfBandThinkingExtraction();
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
            new PromptConfig { VisionBase = "OCR\n---TRANSLATION---\nTranslate to {{target_language}}; retain {{unknown_placeholder}}" });
        Check(
            "custom vision base composes with both language plugins and expands only the target language placeholder",
            visionPrompt.StartsWith("OCR\n---TRANSLATION---\nTranslate to zh-CN; retain {{unknown_placeholder}}", StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal) &&
            !visionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        var pureModularPrompt = PromptTemplates.GetSystemPrompt(
            request,
            new PromptConfig());
        var expectedBase = PromptTemplates.DefaultTranslationBasePrompt.Replace(
            PromptTemplates.TargetLanguagePlaceholder, request.TargetLanguage, StringComparison.Ordinal);
        Check(
            "empty prompt config strictly builds modular prompt with sentinel and zero explanation directives",
            pureModularPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
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
            (nameof(PromptTemplates.JapanesePlugin), PromptTemplates.JapanesePlugin),
            (nameof(PromptTemplates.EnglishPlugin), PromptTemplates.EnglishPlugin),
            (nameof(PromptTemplates.ChinesePlugin), PromptTemplates.ChinesePlugin),
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
            (nameof(PromptTemplates.DefaultVisionBasePrompt), PromptTemplates.DefaultVisionBasePrompt),
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

    private static void TestModularPromptTemplates()
    {
        // 1. 日文输入触发 JapanesePlugin（只注入振假名规约，不注入精讲文法与辞书形）
        var japaneseReq = new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）");
        var japanesePrompt = PromptTemplates.GetSystemPrompt(japaneseReq, new PromptConfig());
        Check(
            "system prompt for Japanese input injects ruby format brackets and excludes explanation grammar terms",
            japanesePrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !japanesePrompt.Contains(PromptTemplates.DefaultExplainUserPrompt, StringComparison.Ordinal) &&
            japanesePrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            japanesePrompt.Contains("中文（简体）", StringComparison.Ordinal));

        // 2. 纯汉字日文词条触发 JapanesePlugin
        var pureKanjiReq = new UnderstandRequest(UnderstandKind.Translate, "進入禁止", null, "中文（简体）");
        var pureKanjiPrompt = PromptTemplates.GetSystemPrompt(pureKanjiReq, new PromptConfig());
        Check(
            "system prompt for pure kanji Japanese phrase injects Japanese plugin",
            pureKanjiPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            pureKanjiPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal));

        // 2.5 纯汉字日文词条在目标语言为英文时依然注入 JapanesePlugin，且不误注入 EnglishPlugin
        var pureKanjiEnReq = new UnderstandRequest(UnderstandKind.Translate, "進入禁止", null, "English");
        var pureKanjiEnPrompt = PromptTemplates.GetSystemPrompt(pureKanjiEnReq, new PromptConfig());
        Check(
            "system prompt for pure kanji Japanese phrase with target English injects Japanese plugin",
            pureKanjiEnPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !pureKanjiEnPrompt.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal) &&
            pureKanjiEnPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal));

        // 3. 纯简体中文输入不注入 JapanesePlugin
        var chineseReq = new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, "English");
        var chinesePrompt = PromptTemplates.GetSystemPrompt(chineseReq, new PromptConfig());
        Check(
            "system prompt for simplified Chinese excludes Japanese ruby plugin",
            !chinesePrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            chinesePrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            chinesePrompt.Contains("English", StringComparison.Ordinal));

        // 4. 纯英文输入触发 EnglishPlugin
        var englishReq = new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）");
        var englishPrompt = PromptTemplates.GetSystemPrompt(englishReq, new PromptConfig());
        Check(
            "system prompt for English input injects English plugin with IPA and excludes Japanese ruby",
            englishPrompt.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal) &&
            !englishPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal));

        // 5. 独立精讲请求（不带 TranslationSession，只有 CLI / 自测会走到）复用唯一那条精讲提示词；
        //    应用内的精讲是阶段二追问消息，system 通道已被阶段一的翻译提示词锁定，不再另设精讲 Base。
        var explainReq = new UnderstandRequest(UnderstandKind.Explain, "通れない", null, "中文（简体）");
        var explainPrompt = PromptTemplates.GetSystemPrompt(explainReq, new PromptConfig());
        Check(
            "explain system prompt reuses the single explain prompt with structured sections and difficulty filter",
            explainPrompt.Contains(PromptTemplates.DefaultExplainUserPrompt, StringComparison.Ordinal));

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

    /// <summary>
    /// 视觉路径组装契约：视觉 Base + 语言插件恒定同拼（中文目标下拼音插件被互斥剔除，与两个内置插件的旧版等价）。
    /// 图为唯一输入、语言探测无从做起，而三条插件规则本身是条件式的（「对汉字标注振假名」/
    /// 「为原文中拉丁字母拼写的单词标注 IPA」/「为少见难读的汉字标注拼音」），宁全勿缺；附带收益是
    /// 视觉 system prompt 与输入无关、恒定单变体，前缀缓存 100% 命中。
    /// </summary>
    private static void TestVisionPromptComposition()
    {
        var imageRequest = new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "中文（简体）");
        var visionPrompt = PromptTemplates.GetSystemPrompt(imageRequest, new PromptConfig());

        var expectedVisionBase = PromptTemplates.DefaultVisionBasePrompt.Replace(
            PromptTemplates.TargetLanguagePlaceholder, imageRequest.TargetLanguage, StringComparison.Ordinal);

        // 1. 默认视觉提示词 = 视觉 Base + 两个内置插件，两条注音规约齐备（中文目标下拼音插件被互斥剔除）
        Check(
            "default vision prompt composes the vision base with both built-in language plugins",
            visionPrompt.Contains(expectedVisionBase, StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal) &&
            visionPrompt.Contains(PromptTemplates.TranslationSentinel, StringComparison.Ordinal) &&
            visionPrompt.Contains("中文（简体）", StringComparison.Ordinal) &&
            !visionPrompt.Contains(PromptTemplates.TargetLanguagePlaceholder, StringComparison.Ordinal));

        // 2. 视觉 system prompt 与原文无关：空文本 / 英文 / 日文请求给出同一句（恒定单变体）
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

        // 3. 自定义视觉 Base 与两个自定义插件在视觉路径优先生效
        var customVision = PromptTemplates.GetSystemPrompt(
            imageRequest,
            new PromptConfig
            {
                VisionBase = "[Custom vision base]",
                JapanesePlugin = "[Custom ja-JP Plugin]",
                EnglishPlugin = "[Custom en-US Plugin]",
            });
        Check(
            "custom vision base and custom plugins take priority in the vision path",
            customVision.Contains("[Custom vision base]", StringComparison.Ordinal) &&
            customVision.Contains("[Custom ja-JP Plugin]", StringComparison.Ordinal) &&
            customVision.Contains("[Custom en-US Plugin]", StringComparison.Ordinal) &&
            !customVision.Contains(PromptTemplates.DefaultVisionBasePrompt, StringComparison.Ordinal) &&
            !customVision.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !customVision.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        // 4. 两个对齐常量已删除，视觉 Base 自行内联目标语言从句，不拼独立对齐段
        Check(
            "vision path appends no standalone alignment section after the alignment constants were removed",
            !visionPrompt.Contains("[目标语言约束]", StringComparison.Ordinal) &&
            !visionPrompt.Contains("[精讲语言约束]", StringComparison.Ordinal));
    }

    /// <summary>
    /// 组装级插件门控契约：探测器只决定「纳入哪些语言的规约」，适用范围交由插件文案限定，
    /// 故日英混排句同享振仮名与 IPA 两套规约；纯中文/纯日语场景仍守住探测的「排除」价值。
    /// 该盲区在探测层不可见（特征位本就同时置位），只在组装层显形，必须由本测试钉住。
    /// </summary>
    private static void TestMixedLanguagePluginGating()
    {
        // 1. 日英混排：两个门控各自独立命中，两条规约同享
        var mixedTranslate = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "mixed Japanese and English translation prompt shares ruby and IPA conventions",
            mixedTranslate.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            mixedTranslate.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        var mixedExplain = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Explain, "Appleの新しいiPhone発表会", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "explain prompt focuses purely on structured explanation without ruby or IPA plugins",
            mixedExplain.Contains(PromptTemplates.DefaultExplainUserPrompt, StringComparison.Ordinal) &&
            !mixedExplain.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !mixedExplain.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        // 1.5 日英中三混排：三个门控各自独立命中，振仮名、IPA 与拼音三套规约同享
        // （目标取韩语——闭集内既非中日英的语言，三个互斥谓词均不剪枝）
        var tripleMixed = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "这是一个iPhoneの新しい発表会", null, "韩语"),
            new PromptConfig());
        Check(
            "triple mixed japanese english and chinese translation prompt shares ruby ipa and pinyin conventions",
            tripleMixed.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            tripleMixed.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal) &&
            tripleMixed.Contains(PromptTemplates.ChinesePlugin, StringComparison.Ordinal));

        // 2. 少量拉丁混入同样纳入英语规约：规则是条件式的，模型可逐 token 取舍，不误伤
        var sparseLatin = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "ゲームSTART", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "sparse latin borrowing inside japanese text still receives the IPA convention",
            sparseLatin.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            sparseLatin.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        // 3. 无拉丁字母即不注入英语插件
        var pureJapanese = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "ラーメンは美味しい", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "pure japanese text without latin letters keeps ruby and gains no IPA convention",
            pureJapanese.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !pureJapanese.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        var pureChinese = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "pure simplified chinese still receives neither ruby nor IPA convention",
            !pureChinese.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !pureChinese.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));

        // 4. 自定义插件覆盖行为不受门控改动影响：混排句照样优先采用用户文案
        var customMixed = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"),
            new PromptConfig
            {
                JapanesePlugin = "[Custom ja-JP Plugin]",
                EnglishPlugin = "[Custom en-US Plugin]",
            });
        Check(
            "custom plugins keep overriding both conventions for mixed japanese and english text",
            customMixed.Contains("[Custom ja-JP Plugin]", StringComparison.Ordinal) &&
            customMixed.Contains("[Custom en-US Plugin]", StringComparison.Ordinal) &&
            !customMixed.Contains(PromptTemplates.JapanesePlugin, StringComparison.Ordinal) &&
            !customMixed.Contains(PromptTemplates.EnglishPlugin, StringComparison.Ordinal));
    }

    /// <summary>
    /// 目标语言互斥门控契约（talk 003 核心决策）：语言特化插件是「为源语言学习者提供生词阅读辅助」，
    /// 一旦某语言本身就是目标语言，辅助即失效，必须整条剔除。钉住文本与视觉两条路径在目标为日/英语时的
    /// 剪枝行为、中文目标下维持现状，以及剪枝后视觉 prompt 仍是单一缓存友好变体。
    /// </summary>
    private static void TestTargetLanguagePluginMutex()
    {
        var ruby = PromptTemplates.JapanesePlugin;
        var ipa = PromptTemplates.EnglishPlugin;
        var pinyin = PromptTemplates.ChinesePlugin;

        // 1. 日译英：日英混排、目标为英语 → 剔除英语 IPA 插件（不再给 Nintendo Switch 标 IPA），保留日语振假名。
        foreach (var target in new[] { "英语", "English", "en" })
        {
            var jpToEn = PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "Nintendo Switchの最新ゲームをプレイする", null, target),
                new PromptConfig());
            Check(
                $"target english prunes the english plugin but keeps the japanese ruby plugin (target={target})",
                jpToEn.Contains(ruby, StringComparison.Ordinal) &&
                !jpToEn.Contains(ipa, StringComparison.Ordinal));
        }

        // 1.5 纯汉字日文译英：目标为英语 → 纯汉字日文依然识别为日语注入振假名，且剔除英语 IPA 插件
        foreach (var target in new[] { "英语", "English", "en" })
        {
            var pureKanjiToEn = PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "進入禁止", null, target),
                new PromptConfig());
            Check(
                $"target english injects japanese ruby plugin for pure kanji source (target={target})",
                pureKanjiToEn.Contains(ruby, StringComparison.Ordinal) &&
                !pureKanjiToEn.Contains(ipa, StringComparison.Ordinal) &&
                !pureKanjiToEn.Contains(pinyin, StringComparison.Ordinal));
        }

        // 2. 目标为英语、纯英文源 → 两条插件都不注入（英语按目标剔除、源无日语）。
        var enToEn = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "英语"),
            new PromptConfig());
        Check(
            "target english with pure english source injects neither ruby nor ipa plugin",
            !enToEn.Contains(ruby, StringComparison.Ordinal) &&
            !enToEn.Contains(ipa, StringComparison.Ordinal));

        // 3. 英/中译日：源含引述假名、目标为日语 → 剔除日语振假名插件（不再给假名标振假名）。
        foreach (var target in new[] { "日语", "日本語", "ja" })
        {
            var toJa = PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "これは「護美」という名前です", null, target),
                new PromptConfig());
            Check(
                $"target japanese prunes the japanese ruby plugin even when the source quotes kana (target={target})",
                !toJa.Contains(ruby, StringComparison.Ordinal) &&
                !toJa.Contains(ipa, StringComparison.Ordinal));
        }

        // 4. 英译日：日英混排、目标为日语 → 剔除日语插件，英语 IPA 插件保留（源含拉丁、仍需阅读辅助）。
        var enToJa = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Nintendo Switchの最新ゲーム", null, "日语"),
            new PromptConfig());
        Check(
            "target japanese prunes the japanese plugin but keeps the english ipa plugin for latin source",
            !enToJa.Contains(ruby, StringComparison.Ordinal) &&
            enToJa.Contains(ipa, StringComparison.Ordinal));

        // 5. 目标中文维持现状：日英混排照旧同享两套规约（互斥不应误伤默认场景）。
        var toZh = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, "Appleの新しいiPhone発表会", null, "中文（简体）"),
            new PromptConfig());
        Check(
            "chinese target keeps both plugins so the default mixed-language behaviour is untouched",
            toZh.Contains(ruby, StringComparison.Ordinal) &&
            toZh.Contains(ipa, StringComparison.Ordinal));

        // 5.5 中文目标互斥：源命中简体独占字、目标即中文 → 拼音插件整条剔除（给本就属于目标语言的汉字标拼音无价值）。
        foreach (var target in new[] { "中文（简体）", "中文", "Chinese", "zh-CN" })
        {
            var zhToZh = PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, target),
                new PromptConfig());
            Check(
                $"target chinese prunes the chinese pinyin plugin (target={target})",
                !zhToZh.Contains(pinyin, StringComparison.Ordinal) &&
                !zhToZh.Contains(ruby, StringComparison.Ordinal) &&
                !zhToZh.Contains(ipa, StringComparison.Ordinal));
        }

        // 5.6 中译外：源命中简体独占字、目标非中文 → 注入拼音插件；源无假名/拉丁，日英插件不注入。
        foreach (var target in new[] { "英语", "日本語" })
        {
            var zhToForeign = PromptTemplates.GetSystemPrompt(
                new UnderstandRequest(UnderstandKind.Translate, "今天天气很好，我们去散步", null, target),
                new PromptConfig());
            Check(
                $"non-chinese target injects the chinese pinyin plugin for a simplified source (target={target})",
                zhToForeign.Contains(pinyin, StringComparison.Ordinal) &&
                !zhToForeign.Contains(ruby, StringComparison.Ordinal) &&
                !zhToForeign.Contains(ipa, StringComparison.Ordinal));
        }

        // 6. 视觉路径同样施加互斥：目标英语只留日语插件，目标日语只留英语插件。
        var visionToEn = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "英语"),
            new PromptConfig());
        Check(
            "vision path prunes the english plugin when the target language is english",
            visionToEn.Contains(ruby, StringComparison.Ordinal) &&
            visionToEn.Contains(pinyin, StringComparison.Ordinal) &&
            !visionToEn.Contains(ipa, StringComparison.Ordinal));

        var visionToJa = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "日语"),
            new PromptConfig());
        Check(
            "vision path prunes the japanese plugin when the target language is japanese",
            !visionToJa.Contains(ruby, StringComparison.Ordinal) &&
            visionToJa.Contains(ipa, StringComparison.Ordinal) &&
            visionToJa.Contains(pinyin, StringComparison.Ordinal));

        // 6.5 视觉路径对中文目标剔除拼音插件，日英两者俱全——与旧版「恒定同拼」等价。
        var visionToZh = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "中文（简体）"),
            new PromptConfig());
        Check(
            "vision path prunes the chinese plugin when the target language is chinese",
            !visionToZh.Contains(pinyin, StringComparison.Ordinal) &&
            visionToZh.Contains(ruby, StringComparison.Ordinal) &&
            visionToZh.Contains(ipa, StringComparison.Ordinal));

        // 6.6 视觉路径对非日英中目标（如韩语）常驻全部三个插件（宁全勿缺）
        var visionToKo = PromptTemplates.GetSystemPrompt(
            new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "韩语"),
            new PromptConfig());
        Check(
            "vision path retains all three plugins when target language is other",
            visionToKo.Contains(ruby, StringComparison.Ordinal) &&
            visionToKo.Contains(ipa, StringComparison.Ordinal) &&
            visionToKo.Contains(pinyin, StringComparison.Ordinal));

        // 7. 视觉 prompt 仍是单一变体：目标固定为英语时，不同源文本给出完全相同的 system prompt（缓存友好）。
        var visionVariantsEn = new[]
        {
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, null, new byte[] { 1 }, "英语"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", new byte[] { 1 }, "英语"), new PromptConfig()),
            PromptTemplates.GetSystemPrompt(new UnderstandRequest(UnderstandKind.Translate, "Hello", new byte[] { 1 }, "英语"), new PromptConfig()),
        };
        Check(
            "vision system prompt stays a single cache-friendly variant under an english target",
            visionVariantsEn.Distinct(StringComparer.Ordinal).Count() == 1);
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
                new PromptConfig { Base = "Custom base for {{target_language}}", EnglishPlugin = "[Custom en-US Plugin]" }),
            ("vision/custom", new UnderstandRequest(UnderstandKind.Translate, "text", new byte[] { 1 }, "中文（简体）"),
                new PromptConfig { VisionBase = "[Custom vision base]", JapanesePlugin = "[Custom ja-JP Plugin]" }),
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
        Check(
            "prompt sections distinguish built-in fallbacks from custom overrides",
            PromptTemplates.DescribeSections(
                    new UnderstandRequest(UnderstandKind.Translate, "ここは通れないよ", null, "中文（简体）"), builtin)
                .Select(section => (section.Name, section.Source))
                .SequenceEqual(new[] { ("base", PromptSectionSource.Builtin), ("japanese_plugin", PromptSectionSource.Builtin) }) &&
            PromptTemplates.DescribeSections(
                    new UnderstandRequest(UnderstandKind.Translate, "Hello world", null, "中文（简体）"),
                    new PromptConfig { Base = "Custom base", EnglishPlugin = "[Custom en-US Plugin]" })
                .Select(section => (section.Name, section.Source))
                .SequenceEqual(new[] { ("base", PromptSectionSource.Custom), ("english_plugin", PromptSectionSource.Custom) }));

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

    private static void TestThinkingStreamSanitizer()
    {
        // 1. 基础隔离：单 chunk 内包含完整 <think> 思考链
        var sanitizer = new ThinkingStreamSanitizer();
        var outputs = sanitizer.Push("<think>思考词性与语法</think>私[わたし]は 学生[がくせい]です。").ToList();
        var clean = string.Concat(outputs.Select(o => o.CleanText));
        var thinking = string.Concat(outputs.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer separates full inline <think> tags into thinking and clean text",
            clean == "私[わたし]は 学生[がくせい]です。" &&
            thinking == "思考词性与语法");

        // 2. 跨 chunk 标签拆分（<thi + nk> 与 </thi + nk>）
        var chunkSanitizer = new ThinkingStreamSanitizer();
        var c1 = chunkSanitizer.Push("<thi").ToList();
        var c2 = chunkSanitizer.Push("nk>深度思考中").ToList();
        var c3 = chunkSanitizer.Push("</thi").ToList();
        var c4 = chunkSanitizer.Push("nk>\n你好世界").ToList();
        var allChunks = c1.Concat(c2).Concat(c3).Concat(c4).ToList();
        var chunkClean = string.Concat(allChunks.Select(o => o.CleanText));
        var chunkThinking = string.Concat(allChunks.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer buffers cross-chunk open and close tags correctly",
            chunkClean == "你好世界" &&
            chunkThinking == "深度思考中");

        // 3. 思考流异常中断未闭合：不向干净正文通道泄漏任何字符
        var unclosedSanitizer = new ThinkingStreamSanitizer();
        var u1 = unclosedSanitizer.Push("<think>未完成的思考").ToList();
        var uComplete = unclosedSanitizer.Complete().ToList();
        var unclosedClean = string.Concat(u1.Concat(uComplete).Select(o => o.CleanText));
        var unclosedThinking = string.Concat(u1.Concat(uComplete).Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer preserves unclosed thinking content and never leaks to clean channel",
            string.IsNullOrEmpty(unclosedClean) &&
            unclosedThinking == "未完成的思考");

        // 4. 思考内部包含 '<' 符号（如 1 < 2）不被误判为闭标签
        var mathSanitizer = new ThinkingStreamSanitizer();
        var m1 = mathSanitizer.Push("<think>比较 1 < 2 与 3 > 2</think>正文").ToList();
        var mathClean = string.Concat(m1.Select(o => o.CleanText));
        var mathThinking = string.Concat(m1.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer tolerates less-than signs inside thinking block",
            mathClean == "正文" &&
            mathThinking == "比较 1 < 2 与 3 > 2");

        // 5. 非思考标签前缀退回（如 <annotated_source>）原样输出为干净正文
        var normalSanitizer = new ThinkingStreamSanitizer();
        var n1 = normalSanitizer.Push("<annotated_source>原文</annotated_source>").ToList();
        var normalClean = string.Concat(n1.Select(o => o.CleanText));
        var normalThinking = string.Concat(n1.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer passes through non-thinking xml tags as clean text",
            normalClean == "<annotated_source>原文</annotated_source>" &&
            string.IsNullOrEmpty(normalThinking));

        // 6. 孤立闭合标签（如以 </think> 开头的模型流）静默吞除，且吞掉随后的换行符
        var isolatedCloseSanitizer = new ThinkingStreamSanitizer();
        var iso1 = isolatedCloseSanitizer.Push("</think>\n\n你好世界").ToList();
        var isoClean = string.Concat(iso1.Select(o => o.CleanText));
        var isoThinking = string.Concat(iso1.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer silently swallows isolated close tag and trailing newlines",
            isoClean == "你好世界" &&
            string.IsNullOrEmpty(isoThinking));

        // 7. 跨 chunk 切分的孤立闭合标签（如 </thi + nk> + \n\n + 纯译文）
        var splitCloseSanitizer = new ThinkingStreamSanitizer();
        var sc1 = splitCloseSanitizer.Push("</thi").ToList();
        var sc2 = splitCloseSanitizer.Push("nk>\n\n").ToList();
        var sc3 = splitCloseSanitizer.Push("纯译文内容").ToList();
        var scComplete = splitCloseSanitizer.Complete().ToList();
        var scAll = sc1.Concat(sc2).Concat(sc3).Concat(scComplete).ToList();
        var scClean = string.Concat(scAll.Select(o => o.CleanText));
        var scThinking = string.Concat(scAll.Select(o => o.ThinkingText));
        Check(
            "ThinkingStreamSanitizer handles cross-chunk isolated close tag and newlines cleanly",
            scClean == "纯译文内容" &&
            string.IsNullOrEmpty(scThinking));

        // 8. 结合 SectionChannelRouter 验证孤立闭合标签不破坏纯译文 fallback
        var router = new SectionChannelRouter();
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

    private static void TestProviderThinkingParameters()
    {
        var request = new UnderstandRequest(UnderstandKind.Translate, "测试", null, "zh-CN");

        // 1. 官方 OpenAI端点：completions 模式
        var officialProfileOff = new ProviderProfile
        {
            Id = "openai-official",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            EnableThinking = false,
        };
        var officialProviderOff = new OpenAiProvider(officialProfileOff, "key", new PromptConfig());
        using var offMsg = officialProviderOff.CreateCompletionsRequest(request);
        using var offDoc = JsonDocument.Parse(offMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Official OpenAI with EnableThinking=false does not inject enable_thinking or reasoning_effort",
            !offDoc.RootElement.TryGetProperty("enable_thinking", out _) &&
            !offDoc.RootElement.TryGetProperty("reasoning_effort", out _));

        var officialProfileOnO1 = new ProviderProfile
        {
            Id = "openai-o1",
            BaseUrl = "https://api.openai.com/v1",
            Model = "o1-mini",
            EnableThinking = true,
        };
        var officialProviderOnO1 = new OpenAiProvider(officialProfileOnO1, "key", new PromptConfig());
        using var onO1Msg = officialProviderOnO1.CreateCompletionsRequest(request);
        using var onO1Doc = JsonDocument.Parse(onO1Msg.Content!.ReadAsStringAsync().Result);
        Check(
            "Official OpenAI with EnableThinking=true and o1 model injects reasoning_effort=low but never enable_thinking",
            !onO1Doc.RootElement.TryGetProperty("enable_thinking", out _) &&
            onO1Doc.RootElement.GetProperty("reasoning_effort").GetString() == "low");

        // 2. 官方 OpenAI端点：responses 模式
        var responsesProfile = new ProviderProfile
        {
            Id = "openai-resp",
            ApiType = "responses",
            BaseUrl = "https://api.openai.com/v1",
            Model = "o3-mini",
            EnableThinking = true,
        };
        var responsesProvider = new OpenAiProvider(responsesProfile, "key", new PromptConfig());
        using var respMsg = responsesProvider.CreateResponsesRequest(request);
        using var respDoc = JsonDocument.Parse(respMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "OpenAI Responses API with EnableThinking=true injects reasoning object and never enable_thinking",
            !respDoc.RootElement.TryGetProperty("enable_thinking", out _) &&
            respDoc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "low");

        // 3. 第三方兼容端点（如 SiliconFlow / Qwen / DeepSeek）
        var compatProfileOff = new ProviderProfile
        {
            Id = "compat-off",
            BaseUrl = "https://api.siliconflow.cn/v1",
            Model = "Qwen/Qwen2.5-7B",
            EnableThinking = false,
        };
        var compatProviderOff = new OpenAiProvider(compatProfileOff, "key", new PromptConfig());
        using var compatOffMsg = compatProviderOff.CreateCompletionsRequest(request);
        using var compatOffDoc = JsonDocument.Parse(compatOffMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "OpenAI compatible provider with EnableThinking=false injects enable_thinking=false and thinking.type=disabled",
            compatOffDoc.RootElement.GetProperty("enable_thinking").GetBoolean() == false &&
            compatOffDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled");

        var compatProfileOn = new ProviderProfile
        {
            Id = "compat-on",
            BaseUrl = "https://api.siliconflow.cn/v1",
            Model = "deepseek-ai/DeepSeek-R1",
            EnableThinking = true,
        };
        var compatProviderOn = new OpenAiProvider(compatProfileOn, "key", new PromptConfig());
        using var compatOnMsg = compatProviderOn.CreateCompletionsRequest(request);
        using var compatOnDoc = JsonDocument.Parse(compatOnMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "OpenAI compatible provider with EnableThinking=true injects enable_thinking=true and thinking.type=enabled",
            compatOnDoc.RootElement.GetProperty("enable_thinking").GetBoolean() == true &&
            compatOnDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled");

        // 4. Claude Provider：关闭时严禁下发 thinking，开启时注入 budget_tokens
        var claudeProfileOff = new ProviderProfile
        {
            Id = "claude-off",
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-3-5-sonnet-20241022",
            EnableThinking = false,
        };
        var claudeProviderOff = new ClaudeProvider(claudeProfileOff, "key", new PromptConfig());
        using var claudeOffMsg = claudeProviderOff.CreateRequest(request);
        using var claudeOffDoc = JsonDocument.Parse(claudeOffMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Claude provider with EnableThinking=false completely omits thinking parameter to avoid 400 rejection",
            !claudeOffDoc.RootElement.TryGetProperty("thinking", out _));

        var claudeProfileOn = new ProviderProfile
        {
            Id = "claude-on",
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-3-7-sonnet-20250219",
            EnableThinking = true,
        };
        var claudeProviderOn = new ClaudeProvider(claudeProfileOn, "key", new PromptConfig());
        using var claudeOnMsg = claudeProviderOn.CreateRequest(request);
        using var claudeOnDoc = JsonDocument.Parse(claudeOnMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Claude provider with EnableThinking=true injects enabled thinking with budget_tokens 1024",
            claudeOnDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
            claudeOnDoc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32() == 1024);

        // 5. Gemini Provider：嵌套在 generationConfig 内
        var geminiProfileOff = new ProviderProfile
        {
            Id = "gemini-off",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.0-flash",
            EnableThinking = false,
        };
        var geminiProviderOff = new GeminiProvider(geminiProfileOff, "key", new PromptConfig());
        using var geminiOffMsg = geminiProviderOff.CreateRequest(request);
        using var geminiOffDoc = JsonDocument.Parse(geminiOffMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Gemini provider with EnableThinking=false nests thinkingBudget=0 inside generationConfig",
            geminiOffDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32() == 0);

        var geminiProfileOn = new ProviderProfile
        {
            Id = "gemini-on",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.0-flash",
            EnableThinking = true,
        };
        var geminiProviderOn = new GeminiProvider(geminiProfileOn, "key", new PromptConfig());
        using var geminiOnMsg = geminiProviderOn.CreateRequest(request);
        using var geminiOnDoc = JsonDocument.Parse(geminiOnMsg.Content!.ReadAsStringAsync().Result);
        Check(
            "Gemini provider with EnableThinking=true nests thinkingBudget=-1 inside generationConfig",
            geminiOnDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32() == -1);
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

