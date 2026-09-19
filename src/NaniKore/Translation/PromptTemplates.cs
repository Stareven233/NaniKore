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
    internal const string DefaultExplainUserPrompt = "请对上述内容进行深度精讲拆解。注意：只分析N3及以上难度的重点词汇、短语与语法句型，过滤掉N4/N5等初级简单基础词与基础句型，太简单的不要分析。";

    /// <summary>设置页始终展示的默认基础翻译提示词模版；主人可直接在此基础上修改。</summary>
    internal const string DefaultTranslationPrompt = DefaultTranslationBasePrompt;

    /// <summary>模块化 Base 提示词：规定两阶段输出格式与严格翻译纪律（严禁与用户对话、反问或补全）。</summary>
    internal const string DefaultTranslationBasePrompt = """
        You are an accurate, natural language translator and linguistic assistant.
        [Translation Phase]
        Translate the supplied text into {{target_language}}.
        Return the result in exactly this two-part structure:
        <annotated_source>
        ---TRANSLATION---
        <translation>

        [Strict Translation Discipline]
        - Strictly translate the input text as-is.
        - Even if the input text is an incomplete sentence, fragmented clause, question, command, greeting, code, or conversational dialogue, NEVER engage in conversation, NEVER answer questions in the text, NEVER ask questions back, NEVER offer to complete, continue, or clarify the sentence, and NEVER explain missing context.
        - Faithfully translate ONLY what is present. If the sentence is cut off or ends with ellipses, faithfully reflect that same truncated structure in the translation.
        - For <annotated_source> and <translation>, output the text directly (with ruby annotations where applicable); do NOT output literal XML tags like `<annotated_source>`, `</annotated_source>`, `<translation>`, or `</translation>`.
        - Strictly output only the two parts separated by `---TRANSLATION---`. Do not include conversational filler, pleasantries, apologies, markdown title headers, notes, or follow-up remarks in this phase.
        """;

    /// <summary>日语特化插件：约束汉字注音方括号语法、辞书形原形，摒弃陈旧文法术语。</summary>
    internal const string JapanesePlugin = """
        [Japanese Language Plugin]
        - For <annotated_source>: Annotate all kanji readings strictly using ruby bracket format `漢字[かな]` (e.g. `私[わたし]は 学生[がくせい]です。`).
        - Never put brackets around kana, punctuation, digits, or non-kanji words.
        - Even if the input text is a fragment, clause, or dialogue ending with particles (e.g. `て...`, `けど`), annotate kanji ruby readings faithfully for all kanji present.
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
        - **词汇** | 假名读音或IPA | 词性 | 详细释义与在句中的语境分析
        ### [语法/句型]
        - **句型** | 语法功能与释义
        ### [语境与地道表达]
        语境分析与自然表达说明

        [Part of Speech & Educational Grammar - STRICT]
        - For Japanese vocabulary, the part of speech (3rd column) MUST strictly adhere to Japanese Educational Grammar (日本語教育文法).
        - Verbs MUST be classified strictly as: `一类动词`, `二类动词`, or `三类动词`. Strictly NEVER output traditional terms or compound terms like `一段动词`, `五段动词`, `他动词`, `自动词`.
        - Adjectives MUST be classified strictly as: `一类形容词` or `二类形容词`.
        - Other parts of speech MUST be concise: `名词`, `副词`, `连词`, `短语`. Keep the tag strictly concise (part of speech only).

        [Difficulty & Focus Filter - CRITICAL]
        - Focus strictly on non-trivial vocabulary, idioms, and grammatical patterns at JLPT N3 level and above (or CEFR B1+ / intermediate and advanced level).
        - Strictly DO NOT list elementary, high-frequency basic words or elementary grammar points (e.g. absolutely DO NOT include N5/N4 basics such as 「私」「これ」「行く」「食べる」「する」「ある」「～です」「～ます」「～ている」「～ない」 unless they possess advanced, uncommon, or highly contextual idiomatic usage in this specific sentence).
        - If the sentence contains only basic elementary words and grammar, explain the contextual nuances in the [语境与地道表达] section rather than polluting the vocabulary/grammar cards with trivial entries.
        """;

    /// <summary>视觉一条龙的默认模板；直接展示分栏协议，用户可按实际模型自由修改。</summary>
    internal const string DefaultVisionOneshotPrompt = """
        Transcribe all readable text in the supplied image, then translate it into {{target_language}}.
        Return exactly this shape:
        <transcription>

        ---

        <translation>

        [Strict Translation Discipline]
        - For Japanese kanji in <transcription>, annotate readings strictly using ruby bracket format `漢字[かな]` (e.g. `私[わたし]は 学生[がくせい]です。`). Never bracket kana, punctuation, digits, or non-kanji words.
        - Strictly translate the input text as-is. Even if the text is incomplete, fragmented, or dialogue, NEVER engage in conversation, NEVER ask questions back, and NEVER offer to complete the sentence.
        - Do not include labels, markdown headers, commentary, notes, or extra sections.
        """;

    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts)
    {
        if (request.Session?.SystemPrompt is { Length: > 0 } sessionPrompt)
        {
            return sessionPrompt;
        }

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

        // 彻底拥抱纯粹模块化架构（Base + 语言特化插件），绝无旧单体兼容历史包袱
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
    private const int MaxSeparatorLength = 32;

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
                    // 原文阶段：通过滑动窗口安全实时输出带注音原文，打破整段阻塞卡顿
                    _pending = StripSourceLeadingTags(_pending);
                    var flushLength = GetSafeSourceFlushLength(_pending);
                    if (flushLength == 0)
                    {
                        yield break;
                    }

                    foreach (var delta in Emit(DeltaChannel.SourceText, _pending[..flushLength]))
                    {
                        yield return delta;
                    }

                    _pending = _pending[flushLength..];
                    continue;
                }

                // 译文阶段已无后续段落分隔标记，实时输出正文，消除 32 字符缓冲阻滞导致的短译文流式卡顿。
                _pending = StripTranslationLeadingTags(_pending);
                var transFlushLength = GetSafeTranslationFlushLength(_pending);
                if (transFlushLength == 0)
                {
                    yield break;
                }

                foreach (var delta in Emit(_channel, _pending[..transFlushLength]))
                {
                    yield return delta;
                }

                _pending = _pending[transFlushLength..];
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
                    sourceSlice = StripSourceTags(sourceSlice);
                    foreach (var delta in Emit(channel, sourceSlice))
                    {
                        yield return delta;
                    }
                }
            }

            _channel = marker.Value.Channel;
            _awaitingFirstSection = false;
            _pending = _pending[(marker.Value.Index + marker.Value.Length)..];
            _pending = StripTranslationLeadingTags(_pending);
        }
    }

    public IEnumerable<UnderstandDelta> Complete()
    {
        if (_pending.Length > 0)
        {
            var text = _pending;
            if (text.EndsWith("</translation>", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^"</translation>".Length].TrimEnd();
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^"```".Length].TrimEnd();
            }

            // 若从未发射过 SourceText 且无分隔符，保留完整输出作为译文（兼容视觉纯译文输出 fallback）；
            // 若已发射过 SourceText，剩余文本归属 SourceText。
            var channel = _awaitingFirstSection
                ? (_channelsWithContent.Contains(DeltaChannel.SourceText) ? DeltaChannel.SourceText : DeltaChannel.Translation)
                : _channel;

            if (_awaitingFirstSection)
            {
                text = StripSourceTags(text);
            }

            foreach (var delta in Emit(channel, text))
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
            if (channel == DeltaChannel.Translation && text.StartsWith("<translation>", StringComparison.OrdinalIgnoreCase))
            {
                text = text["<translation>".Length..].TrimStart();
            }
            else if (channel == DeltaChannel.SourceText)
            {
                text = StripSourceLeadingTags(text);
            }

            if (text.Length == 0)
            {
                yield break;
            }

            _channelsWithContent.Add(channel);
        }

        if (channel == DeltaChannel.SourceText)
        {
            // 原文区三重防线：出口处过滤所有可能残留的标签或闭合标记
            if (text.Contains('<', StringComparison.Ordinal) || text.Contains('`', StringComparison.Ordinal))
            {
                text = StripSourceTags(text);
            }
        }
        else if (channel == DeltaChannel.Translation)
        {
            if (text.EndsWith("</translation>", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^"</translation>".Length].TrimEnd();
            }
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^"```".Length].TrimEnd();
        }

        if (text.Length > 0)
        {
            yield return new UnderstandDelta(channel, text);
        }
    }

    private static string StripSourceTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();

        bool changed;
        do
        {
            changed = false;
            if (text.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase))
            {
                text = text["```markdown".Length..].TrimStart();
                changed = true;
            }
            else if (text.StartsWith("```text", StringComparison.OrdinalIgnoreCase))
            {
                text = text["```text".Length..].TrimStart();
                changed = true;
            }
            else if (text.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                text = text["```".Length..].TrimStart();
                changed = true;
            }

            if (text.StartsWith("<annotated_source>", StringComparison.OrdinalIgnoreCase))
            {
                text = text["<annotated_source>".Length..].TrimStart();
                changed = true;
            }
            else if (text.StartsWith("<transcription>", StringComparison.OrdinalIgnoreCase))
            {
                text = text["<transcription>".Length..].TrimStart();
                changed = true;
            }

            if (text.EndsWith("</annotated_source>", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^"</annotated_source>".Length].TrimEnd();
                changed = true;
            }
            else if (text.EndsWith("</transcription>", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^"</transcription>".Length].TrimEnd();
                changed = true;
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^"```".Length].TrimEnd();
                changed = true;
            }
        } while (changed);

        // 彻底清除可能嵌入或残留的标签文本
        text = text.Replace("</annotated_source>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("<annotated_source>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("</transcription>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("<transcription>", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();

        return text;
    }

    private string StripSourceLeadingTags(string text)
    {
        if (_channelsWithContent.Contains(DeltaChannel.SourceText))
        {
            return text;
        }

        text = text.TrimStart('\r', '\n', ' ');
        if (text.StartsWith("<annotated_source>", StringComparison.OrdinalIgnoreCase))
        {
            text = text["<annotated_source>".Length..].TrimStart('\r', '\n', ' ');
        }
        else if (text.StartsWith("<transcription>", StringComparison.OrdinalIgnoreCase))
        {
            text = text["<transcription>".Length..].TrimStart('\r', '\n', ' ');
        }
        else if (text.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase))
        {
            text = text["```markdown".Length..].TrimStart('\r', '\n', ' ');
        }
        else if (text.StartsWith("```text", StringComparison.OrdinalIgnoreCase))
        {
            text = text["```text".Length..].TrimStart('\r', '\n', ' ');
        }
        else if (text.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            text = text["```".Length..].TrimStart('\r', '\n', ' ');
        }

        return text;
    }

    /// <summary>
    /// 计算当前待输出原文文本中可以安全即时刷新到 UI 的长度。
    /// 保留可能处于哨兵标记或闭合标签中间的滑动窗口（不超过 32 字符），其余正文即时全速输出。
    /// </summary>
    private int GetSafeSourceFlushLength(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        // 1. 若原文尚未开始输出过有效正文，且开头属于前导标签的可能前缀，等待完整标签到达并剥离
        if (!_channelsWithContent.Contains(DeltaChannel.SourceText))
        {
            var trimmed = text.TrimStart('\r', '\n', ' ');
            if (trimmed.Length == 0)
            {
                return 0;
            }

            if (IsOpenTagPrefix(trimmed, "<annotated_source>") ||
                IsOpenTagPrefix(trimmed, "<transcription>") ||
                IsOpenTagPrefix(trimmed, "```markdown") ||
                IsOpenTagPrefix(trimmed, "```text") ||
                IsOpenTagPrefix(trimmed, "```"))
            {
                return 0;
            }

            // 若无原文特征（假名/注音），且长度尚在滑动窗口预算内，暂缓输出以兼容纯译文 fallback
            if (!HasSourceSignal(text) && text.Length < MaxSeparatorLength)
            {
                return 0;
            }
        }

        // 2. 检查 text 中是否已完整出现任何闭合标签，确保正文安全输出绝不越过闭合标签
        foreach (var tag in SourceCloseTags)
        {
            var tagIndex = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (tagIndex >= 0)
            {
                var safeLength = tagIndex;
                while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
                {
                    safeLength--;
                }

                return safeLength;
            }
        }

        // 3. 检查尾部是否可能截断在哨兵标记或闭合标签前缀中（未完结前缀滑动保护）
        var potentialSuffixIndex = FindPotentialSeparatorOrTagPrefixIndex(text);
        if (potentialSuffixIndex >= 0)
        {
            var safeLength = potentialSuffixIndex;
            while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
            {
                safeLength--;
            }

            return safeLength;
        }

        // 4. 无潜在标记前缀截断，全部即时刷新
        return text.Length;
    }

    private static bool IsOpenTagPrefix(string text, string fullTag) =>
        fullTag.StartsWith(text, StringComparison.OrdinalIgnoreCase) && text.Length < fullTag.Length;

    private static bool HasSourceSignal(string text)
    {
        foreach (var c in text)
        {
            if (c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF') or '[' or ']')
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] SourceCloseTags =
    [
        "</annotated_source>",
        "</transcription>",
        "```markdown",
        "```text",
        "```",
    ];

    private static int FindPotentialSeparatorOrTagPrefixIndex(string text)
    {
        var scanStart = Math.Max(0, text.Length - MaxSeparatorLength);
        for (var i = scanStart; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n' or '-' or '<' or '`'))
            {
                continue;
            }

            var suffix = text[i..];

            foreach (var marker in CandidateMarkers)
            {
                if (marker.Marker.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            foreach (var tag in SourceCloseTags)
            {
                if (tag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string StripTranslationLeadingTags(string text)
    {
        text = text.TrimStart('\r', '\n', ' ');
        if (text.StartsWith("<translation>", StringComparison.OrdinalIgnoreCase))
        {
            text = text["<translation>".Length..].TrimStart('\r', '\n', ' ');
        }

        return text;
    }

    /// <summary>
    /// 计算当前待输出译文文本中可以安全即时刷新到 UI 的长度。
    /// 仅在尾部可能处于未完结标签（如 &lt;/translation&gt; 或代码块栅栏）中间时保留前缀，其余正文即时全速输出。
    /// </summary>
    private int GetSafeTranslationFlushLength(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        // 1. 若译文尚未开始输出过有效正文，且开头属于 <translation> 的可能前缀，等待完整标签到达并剥离
        if (!_channelsWithContent.Contains(DeltaChannel.Translation))
        {
            var trimmed = text.TrimStart('\r', '\n', ' ');
            if (trimmed.Length == 0)
            {
                return 0;
            }

            const string openTag = "<translation>";
            if (openTag.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
        }

        // 2. 检查尾部是否可能截断在 </translation> 标签中间
        const string closeTag = "</translation>";
        var lastLt = text.LastIndexOf('<');
        if (lastLt >= 0)
        {
            var potentialTag = text[lastLt..];
            if (closeTag.StartsWith(potentialTag, StringComparison.OrdinalIgnoreCase))
            {
                // 将标签及其紧邻的前置换行保留在 pending 中，避免将闭合标签的换行误判为正文提前发出
                var safeLength = lastLt;
                while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
                {
                    safeLength--;
                }
                return safeLength;
            }
        }

        // 3. 检查尾部是否可能截断在 ```markdown 或 ``` 栅栏中间
        var lastBacktick = text.LastIndexOf('`');
        if (lastBacktick >= 0)
        {
            var potentialFence = text[lastBacktick..];
            if ("```markdown".StartsWith(potentialFence, StringComparison.OrdinalIgnoreCase) ||
                "```text".StartsWith(potentialFence, StringComparison.OrdinalIgnoreCase) ||
                "```".StartsWith(potentialFence, StringComparison.OrdinalIgnoreCase))
            {
                var safeLength = lastBacktick;
                while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
                {
                    safeLength--;
                }
                return safeLength;
            }
        }

        // 4. 无潜在标签前缀截断，全部即时刷新
        return text.Length;
    }

    private static readonly (string Marker, DeltaChannel Channel, DeltaChannel? PrefixChannel)[] CandidateMarkers =
    [
        ("\r\n\r\n--- TRANSLATION ---\r\n\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\n\n--- TRANSLATION ---\n\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\r\n--- TRANSLATION ---\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\n--- TRANSLATION ---\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("--- TRANSLATION ---", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\r\n\r\n---TRANSLATION---\r\n\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\n\n---TRANSLATION---\n\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\r\n---TRANSLATION---\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\n---TRANSLATION---\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        (PromptTemplates.TranslationSentinel, DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\r\n\r\n---\r\n\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\n\n---\n\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        ("\r\n---\r\n", DeltaChannel.Translation, DeltaChannel.SourceText),
        (PromptTemplates.SectionSeparator, DeltaChannel.Translation, DeltaChannel.SourceText),
    ];

    private static SentinelMatch? FindNextMarker(string text)
    {
        SentinelMatch? result = null;
        foreach (var candidate in CandidateMarkers)
        {
            var index = text.IndexOf(candidate.Marker, StringComparison.OrdinalIgnoreCase);
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
