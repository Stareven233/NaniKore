using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>内置提示词与视觉一条龙的轻量分段协议。</summary>
internal static class PromptTemplates
{
    // 两阶段流式输出极简分隔标记：用于区分假名标注原句与目标语言译文
    internal const string TranslationSentinel = "---TRANSLATION---";
    internal const string TranslationSeparator = "\n---TRANSLATION---\n";

    // 兼容历史视觉一条龙分栏标记（保持向下兼容）
    internal const string SectionSeparator = "\n---\n";
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    /// <summary>两阶段精讲默认追问提示词（复用前缀历史以命中 Prompt Cache）。</summary>
    internal const string DefaultExplainUserPrompt = "请对上述内容进行深度精讲拆解。";

    /// <summary>设置页始终展示的默认基础翻译提示词模版；主人可直接在此基础上修改。</summary>
    internal const string DefaultTranslationPrompt = """
        Translate the supplied text into {{target_language}}.
        Return the output in exactly this shape:
        <annotated_source>
        ---TRANSLATION---
        <translation>
        Do not include markdown headers, notes, or commentary.
        """;

    /// <summary>模块化 Base 提示词：规定两阶段输出格式与基础纪律。</summary>
    internal const string DefaultTranslationBasePrompt = """
        You are an accurate, natural language translator and linguistic assistant.
        [Translation Phase]
        Translate the supplied text into {{target_language}}.
        Return the result in exactly this two-part structure:
        <annotated_source>
        ---TRANSLATION---
        <translation>
        Do not include markdown title headers, conversational filler, or commentary in this phase.
        """;

    /// <summary>日语特化插件：约束汉字注音方括号语法、辞书形原形，摒弃陈旧文法术语。</summary>
    internal const string JapanesePlugin = """
        [Japanese Language Plugin]
        - For <annotated_source>: Annotate kanji readings strictly using ruby bracket format `漢字[かな]` (e.g. `私[わたし]は 学生[がくせい]です。`).
        - Never put brackets around kana, punctuation, digits, or non-kanji words.
        - For verbs and vocabulary: Always identify the dictionary form (辞書形, plain base form). Strictly avoid outdated traditional school grammar terms like 未然形 or 連用形.
        """;

    /// <summary>默认日语插件模板，供设置面板展示与一键恢复。</summary>
    internal const string DefaultJapanesePlugin = JapanesePlugin;

    /// <summary>英语特化插件：约束音标 (IPA) 与动词原形规范。</summary>
    internal const string EnglishPlugin = """
        [English Language Plugin]
        - For vocabulary and pronunciation: Annotate with International Phonetic Alphabet (IPA).
        - For verbs: Always use the base infinitive form.
        """;

    /// <summary>默认英语插件模板，供设置面板展示与一键恢复。</summary>
    internal const string DefaultEnglishPlugin = EnglishPlugin;

    /// <summary>目标语言对齐约束：强制所有译文与语法解析均使用当前目标语言。</summary>
    internal const string LanguageAlignmentRule = """
        [Target Language Constraint]
        All translations, vocabulary definitions, grammar explanations, and context notes must strictly be written in {{target_language}}.
        """;

    /// <summary>精讲微结构化格式规约：在系统提示词预置，确保第二阶段 100% 命中前缀缓存。</summary>
    internal const string ExplainStructureRule = """
        [Deep Explanation Specification]
        When requested to explain or break down the text, provide a clean structured markdown response adhering to this format:
        ### [词汇/短语]
        - **词汇** | 读音或IPA | 词性与原形 | 释义与语境分析
        ### [语法/句型]
        - **句型** | 语法功能与释义
        ### [语境与地道表达]
        语境分析与自然表达说明
        """;

    /// <summary>视觉一条龙的默认模板；直接展示分栏协议，用户可按实际模型自由修改。</summary>
    internal const string DefaultVisionOneshotPrompt = """
        Transcribe all readable text in the supplied image, then translate it into {{target_language}}.
        Return exactly this shape:
        <transcription>

        ---

        <translation>
        Do not include labels, markdown, commentary, or extra sections.
        """;

    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts)
    {
        if (request.Kind == UnderstandKind.Explain)
        {
            if (!string.IsNullOrWhiteSpace(prompts.Explain))
            {
                return ExpandTargetLanguage(prompts.Explain.Trim(), request.TargetLanguage);
            }

            return BuildExplainSystemPrompt(request, prompts);
        }

        if (request.ImagePng is { Length: > 0 })
        {
            var visionPrompt = string.IsNullOrWhiteSpace(prompts.VisionOneshot)
                ? DefaultVisionOneshotPrompt
                : prompts.VisionOneshot.Trim();
            return ExpandTargetLanguage(visionPrompt, request.TargetLanguage);
        }

        if (!string.IsNullOrWhiteSpace(prompts.Translate))
        {
            // 用户在配置中显式自定义了旧版翻译提示词，优先遵从用户定制
            return ExpandTargetLanguage(prompts.Translate.Trim(), request.TargetLanguage);
        }

        // 走模块化 Base + Plugins 动态拼装机制（优先读取用户定制的 Base/Plugins）
        return BuildModularSystemPrompt(request, prompts);
    }

    /// <summary>
    /// 根据文本字符探测结果动态组装 Base + 特化 Plugins + 目标语言对齐。
    /// 优先使用用户在 PromptConfig 中自定义的模板，未定制项优雅回退至内置模板。
    /// </summary>
    internal static string BuildModularSystemPrompt(UnderstandRequest request, PromptConfig? prompts = null)
    {
        var detection = LanguageDetector.Detect(request.SourceText, request.TargetLanguage);
        var sb = new System.Text.StringBuilder();

        var basePrompt = !string.IsNullOrWhiteSpace(prompts?.Base)
            ? prompts.Base.Trim()
            : DefaultTranslationBasePrompt;
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        sb.AppendLine(ExplainStructureRule);

        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            var jaPlugin = !string.IsNullOrWhiteSpace(prompts?.JapanesePlugin)
                ? prompts.JapanesePlugin.Trim()
                : JapanesePlugin;
            sb.AppendLine();
            sb.AppendLine(jaPlugin);
        }

        if (detection.NeedsEnglishIpa || (detection.HasEnglish && !detection.HasJapanese))
        {
            var enPlugin = !string.IsNullOrWhiteSpace(prompts?.EnglishPlugin)
                ? prompts.EnglishPlugin.Trim()
                : EnglishPlugin;
            sb.AppendLine();
            sb.AppendLine(enPlugin);
        }

        sb.AppendLine();
        sb.AppendLine(LanguageAlignmentRule);

        return ExpandTargetLanguage(sb.ToString().TrimEnd(), request.TargetLanguage);
    }

    /// <summary>
    /// 为独立的精讲任务组装结构化系统提示词。
    /// </summary>
    internal static string BuildExplainSystemPrompt(UnderstandRequest request, PromptConfig? prompts = null)
    {
        var detection = LanguageDetector.Detect(request.SourceText, request.TargetLanguage);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("You are an expert linguistic tutor and translator.");
        sb.AppendLine(ExplainStructureRule);

        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            var jaPlugin = !string.IsNullOrWhiteSpace(prompts?.JapanesePlugin)
                ? prompts.JapanesePlugin.Trim()
                : JapanesePlugin;
            sb.AppendLine();
            sb.AppendLine(jaPlugin);
        }

        if (detection.NeedsEnglishIpa || (detection.HasEnglish && !detection.HasJapanese))
        {
            var enPlugin = !string.IsNullOrWhiteSpace(prompts?.EnglishPlugin)
                ? prompts.EnglishPlugin.Trim()
                : EnglishPlugin;
            sb.AppendLine();
            sb.AppendLine(enPlugin);
        }

        sb.AppendLine();
        sb.AppendLine(LanguageAlignmentRule);

        return ExpandTargetLanguage(sb.ToString().TrimEnd(), request.TargetLanguage);
    }

    private static string ExpandTargetLanguage(string prompt, string targetLanguage) =>
        // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
        prompt.Replace(TargetLanguagePlaceholder, targetLanguage, StringComparison.Ordinal);
}

/// <summary>
/// 视觉流优先按两侧带空行的分隔符拆成原文和译文，并兼容紧凑分隔符；无分隔符时保留完整输出作为译文。
/// </summary>
internal sealed class SectionChannelRouter
{
    private const string PaddedTranslationSeparator = "\n\n---TRANSLATION---\n\n";
    private const string TranslationSeparator = "\n---TRANSLATION---\n";
    private const string PaddedSectionSeparator = "\n\n---\n\n";
    private const int MaxSeparatorLength = 23;

    private DeltaChannel _channel = DeltaChannel.Translation;
    private string _pending = string.Empty;
    private bool _awaitingFirstSection = true;
    private readonly HashSet<DeltaChannel> _channelsWithContent = [];

    public IEnumerable<UnderstandDelta> Push(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        _pending += text;
        while (_pending.Length > 0)
        {
            var marker = FindNextMarker(_pending);
            if (marker is null)
            {
                if (_awaitingFirstSection)
                {
                    // 首个 SOURCE/TRANSLATION 哨兵之前的内容是带注音原文或前导，等待哨兵到达
                    yield break;
                }

                // 保留最长分隔符长度减一的尾部，兼容分隔符恰好跨 SSE 包切断。
                var flushLength = Math.Max(0, _pending.Length - MaxSeparatorLength + 1);
                if (flushLength == 0)
                {
                    yield break;
                }

                foreach (var delta in Emit(_channel, _pending[..flushLength]))
                {
                    yield return delta;
                }

                _pending = _pending[flushLength..];
                continue;
            }

            if (marker.Value.Index > 0)
            {
                var precedingChannel = _awaitingFirstSection
                    ? marker.Value.PrefixChannel
                    : _channel;
                if (precedingChannel is { } channel)
                {
                    var sourceSlice = _pending[..marker.Value.Index].TrimEnd('\r', '\n');
                    foreach (var delta in Emit(channel, sourceSlice))
                    {
                        yield return delta;
                    }
                }
            }

            _channel = marker.Value.Channel;
            _awaitingFirstSection = false;
            _pending = _pending[(marker.Value.Index + marker.Value.Length)..];
        }
    }

    public IEnumerable<UnderstandDelta> Complete()
    {
        if (_pending.Length > 0)
        {
            // 没有哨兵时保留模型的完整输出作为译文，同时去掉常见的前导空白。
            foreach (var delta in Emit(_awaitingFirstSection ? DeltaChannel.Translation : _channel, _pending))
            {
                yield return delta;
            }

            _pending = string.Empty;
        }
    }

    private IEnumerable<UnderstandDelta> Emit(DeltaChannel channel, string text)
    {
        if (!_channelsWithContent.Contains(channel))
        {
            text = text.TrimStart();
            if (text.Length == 0)
            {
                yield break;
            }

            _channelsWithContent.Add(channel);
        }

        yield return new UnderstandDelta(channel, text);
    }

    private static SentinelMatch? FindNextMarker(string text)
    {
        SentinelMatch? result = null;
        foreach (var candidate in new[]
        {
            (Marker: PaddedTranslationSeparator, Channel: DeltaChannel.Translation, PrefixChannel: DeltaChannel.SourceText),
            (Marker: TranslationSeparator, Channel: DeltaChannel.Translation, PrefixChannel: DeltaChannel.SourceText),
            (Marker: PromptTemplates.TranslationSentinel, Channel: DeltaChannel.Translation, PrefixChannel: DeltaChannel.SourceText),
            (Marker: PaddedSectionSeparator, Channel: DeltaChannel.Translation, PrefixChannel: DeltaChannel.SourceText),
            (Marker: PromptTemplates.SectionSeparator, Channel: DeltaChannel.Translation, PrefixChannel: DeltaChannel.SourceText),
        })
        {
            var index = text.IndexOf(candidate.Marker, StringComparison.Ordinal);
            if (index < 0 ||
                (result is not null &&
                 (index > result.Value.Index ||
                  index == result.Value.Index && candidate.Marker.Length <= result.Value.Length)))
            {
                continue;
            }

            result = new SentinelMatch(index, candidate.Channel, candidate.Marker.Length, candidate.PrefixChannel);
        }

        return result;
    }

    private readonly record struct SentinelMatch(int Index, DeltaChannel Channel, int Length, DeltaChannel? PrefixChannel);
}
