using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>
/// 内置提示词工场：下列变量即最终下发给模型的全部内容，按实际拼接顺序排列，
/// 段与段之间以空行相接，{{target_language}} 展开为请求的目标语言。
///
/// 路径一 · 文本翻译：
///   必填  {DefaultTranslationBasePrompt}
///   条件注入（探测到日语时）          {JapanesePlugin}
///   条件注入（探测到英语且无日语时）  {EnglishPlugin}
///   必填  {LanguageAlignmentRule}
///
/// 路径二 · 视觉一条龙（请求携带图片时整体替代路径一，协议自包含，不注入插件）：
///   必填  {DefaultVisionOneshotPrompt}
///
/// 路径三 · 精讲（阶段二追问消息为 {DefaultExplainUserPrompt}）：
///   必填  {ExplainRolePrompt}
///   必填  {ExplainStructureRule}
///   条件注入（探测到日语时）          {JapanesePlugin}
///   条件注入（探测到英语且无日语时）  {EnglishPlugin}
///   必填  {ExplainLanguageAlignmentRule}
///
/// 设置页填写的对应提示词优先，留空则回落到上述内置变量；
/// 翻译阶段严禁混入精讲指令，保证首字延迟与前缀缓存稳定命中。
/// </summary>
internal static class PromptTemplates
{
    /// <summary>两阶段流式输出唯一的分段标记：区分标注原文与目标语言译文。</summary>
    internal const string TranslationSentinel = "---TRANSLATION---";

    /// <summary>目标语言占位符；仅展开已声明的这一个，未知占位符原样保留，避免悄悄改变用户提示词语义。</summary>
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    // ── 路径一 · 文本翻译：Base 协议 ─────────────────────────────────

    /// <summary>模块化 Base：两段式输出协议与翻译纪律；设置页「翻译提示词」展示与恢复默认即此变量。</summary>
    internal static readonly string DefaultTranslationBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，所有的输入都是需要翻译的文本，你需要按照要求将其准确、自然地翻译为{{target_language}}，并将注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实地按文本原有格式进行翻译，追求信达雅
        """);

    // ── 语言特化插件：日语在前、英语在后 ─────────────────────────────

    /// <summary>日语插件：约束汉字振假名注音格式；只服务原文标注，不携带精讲文法。</summary>
    internal static readonly string JapanesePlugin = PromptLiteral("""
        [日语注音规范]
          - 对汉字标注振假名，格式为 漢字[かな]（例：私[わたし]は 学生[がくせい]です。）
          - 即使输入是片段、从句，或以终助词结尾的对话（如 て…、けど），也为其中所有汉字忠实标注读音
        """);

    /// <summary>英语插件：约束国际音标（IPA）标注规范。</summary>
    internal static readonly string EnglishPlugin = PromptLiteral("""
        [英语音标规范]
          - 为原文里少见、困难的单词用方括号标注国际音标（IPA），如 apple[ˈæpl]
        """);

    // ── 译文对齐 ─────────────────────────────────────────────────────

    /// <summary>目标语言对齐：强制译文使用当前目标语言。</summary>
    internal static readonly string LanguageAlignmentRule = PromptLiteral("""
        [目标语言约束]
        全部译文必须使用{{target_language}}撰写。
        """);

    // ── 路径二 · 视觉一条龙 ───────────────────────────────────────────

    /// <summary>视觉一条龙：转录图中文字并直接给出标注原文与译文，自包含全部协议。</summary>
    internal static readonly string DefaultVisionOneshotPrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，所有输入图片都是需翻译文本的截图，你需要按照要求将其准确、自然地翻译为{{target_language}}，并将转录并完成注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实呈现图片中实际出现的内容，维持原有顺序与分行
        """);

    // ── 路径三 · 精讲：职责 → 结构规约 → 语言对齐 ────────────────────

    /// <summary>精讲职责声明。</summary>
    internal const string ExplainRolePrompt = "你是严谨的外语精讲导师，擅长拆解句法、提炼重点并给出地道的解释。";

    /// <summary>精讲微结构化规约：三小节卡片协议 + 日语教育文法 + N3 及以上难度过滤。</summary>
    internal static readonly string ExplainStructureRule = PromptLiteral("""
        按以下三个小节输出结构化 Markdown：

        ### [词汇/短语]
          **词汇** | 假名读音或IPA | 词性 | 详细释义与在句中的语境分析

        ### [语法/句型]
          **句型** | 语法功能与释义

        ### [翻译精讲]
          结合语境与背景/语法知识分析解释为何如此翻译

        [约束]
          - 动词只给辞書形（原形），严格采用日本語教育文法术语
          - 仅列出 JLPT N3 及以上难度的重点词汇、短语与语法句型
        """);

    /// <summary>精讲语言对齐：强制释义、语法与语境说明使用当前目标语言。</summary>
    internal static readonly string ExplainLanguageAlignmentRule = PromptLiteral("""
        [精讲语言约束]
        全部释义、语法剖析与语境说明必须使用{{target_language}}撰写。
        """);

    /// <summary>两阶段精讲的默认追问消息（复用前缀历史以命中 Prompt Cache）。</summary>
    internal static readonly string DefaultExplainUserPrompt = PromptLiteral("""
        按以下三个小节输出结构化 Markdown：

        ### [词汇/短语]
          - **词汇** | 假名读音或IPA | 词性 | 详细释义与在句中的语境分析

        ### [语法/句型]
          - **句型** | 语法功能与释义

        ### [翻译精讲]
          结合语境与背景/语法知识分析解释为何如此翻译

        注：全部使用教育语法术语，仅列出 JLPT N3 及以上难度的重点词汇、短语与语法句型
        """);

    /// <summary>行尾归一化闸门：仓库按 core.autocrlf=true 检出，原始字符串字面量会继承 \r\n，统一收敛为 \n 后再下发。</summary>
    private static string PromptLiteral(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
    /// 翻译阶段严格零精讲提示词，确保首字延迟与前缀缓存稳定命中。
    /// 段落之间统一以 \n\n 连接，避免 AppendLine 引入随平台漂移的 CRLF 污染 Payload。
    /// </summary>
    internal static string BuildModularSystemPrompt(UnderstandRequest request, PromptConfig? prompts = null)
    {
        var detection = LanguageDetector.Detect(request.SourceText, request.TargetLanguage);

        var basePrompt = !string.IsNullOrWhiteSpace(prompts?.Base)
            ? prompts.Base.Trim()
            : DefaultTranslationBasePrompt;

        var sections = new List<string> { basePrompt };

        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            sections.Add(!string.IsNullOrWhiteSpace(prompts?.JapanesePlugin)
                ? prompts.JapanesePlugin.Trim()
                : JapanesePlugin);
        }

        if (detection.NeedsEnglishIpa || (detection.HasEnglish && !detection.HasJapanese))
        {
            sections.Add(!string.IsNullOrWhiteSpace(prompts?.EnglishPlugin)
                ? prompts.EnglishPlugin.Trim()
                : EnglishPlugin);
        }

        sections.Add(LanguageAlignmentRule);

        return ExpandTargetLanguage(string.Join("\n\n", sections), request.TargetLanguage);
    }

    /// <summary>
    /// 为独立的精讲任务组装结构化系统提示词：职责声明 + 精讲规约 + 语言插件 + 目标语言对齐。
    /// </summary>
    internal static string BuildExplainSystemPrompt(UnderstandRequest request, PromptConfig? prompts = null)
    {
        var detection = LanguageDetector.Detect(request.SourceText, request.TargetLanguage);

        var sections = new List<string> { ExplainRolePrompt, ExplainStructureRule };

        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            sections.Add(!string.IsNullOrWhiteSpace(prompts?.JapanesePlugin)
                ? prompts.JapanesePlugin.Trim()
                : JapanesePlugin);
        }

        if (detection.NeedsEnglishIpa || (detection.HasEnglish && !detection.HasJapanese))
        {
            sections.Add(!string.IsNullOrWhiteSpace(prompts?.EnglishPlugin)
                ? prompts.EnglishPlugin.Trim()
                : EnglishPlugin);
        }

        sections.Add(ExplainLanguageAlignmentRule);

        return ExpandTargetLanguage(string.Join("\n\n", sections), request.TargetLanguage);
    }

    private static string ExpandTargetLanguage(string prompt, string targetLanguage) =>
        // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
        prompt.Replace(TargetLanguagePlaceholder, targetLanguage, StringComparison.Ordinal);
}

/// <summary>
/// 流式输出按唯一的分段标记 <c>---TRANSLATION---</c> 拆成原文与译文两个通道；
/// 标记前后的空白与噪声标签由路由器剥离，无标记时保留完整输出作为译文。
/// </summary>
internal sealed class SectionChannelRouter
{
    private const int MaxSeparatorLength = 32;

    private DeltaChannel _channel = DeltaChannel.Translation;
    private string _pending = string.Empty;
    private bool _awaitingFirstSection = true;
    private bool _explanationTruncated = false;
    private readonly HashSet<DeltaChannel> _channelsWithContent = [];

    public IEnumerable<UnderstandDelta> Push(string text)
    {
        if (string.IsNullOrEmpty(text) || _explanationTruncated)
        {
            yield break;
        }

        _pending += text;
        while (_pending.Length > 0)
        {
            var markerIndex = _pending.IndexOf(PromptTemplates.TranslationSentinel, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
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

                // 译文阶段已无后续段落分隔标记，实时输出正文
                _pending = StripTranslationLeadingTags(_pending);

                // 检查是否提前开始输出精讲 markdown，若是则立即截断，绝不允许混杂在译文中
                var expMarkerIdx = FindExplanationPrefixIndex(_pending);
                if (expMarkerIdx >= 0)
                {
                    var validTranslation = _pending[..expMarkerIdx];
                    foreach (var delta in Emit(_channel, validTranslation))
                    {
                        yield return delta;
                    }
                    _pending = string.Empty;
                    _explanationTruncated = true;
                    yield break;
                }

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

            if (markerIndex > 0)
            {
                // 首个标记之前的内容归属原文通道；重复出现的标记直接吞掉，不产生空段
                var precedingChannel = _awaitingFirstSection ? DeltaChannel.SourceText : _channel;
                var sourceSlice = _pending[..markerIndex].TrimEnd('\r', '\n');
                sourceSlice = StripSourceTags(sourceSlice);
                foreach (var delta in Emit(precedingChannel, sourceSlice))
                {
                    yield return delta;
                }
            }

            _channel = DeltaChannel.Translation;
            _awaitingFirstSection = false;
            _pending = _pending[(markerIndex + PromptTemplates.TranslationSentinel.Length)..];
            _pending = StripTranslationLeadingTags(_pending);
        }
    }

    public IEnumerable<UnderstandDelta> Complete()
    {
        if (_pending.Length > 0 && !_explanationTruncated)
        {
            var text = _pending;

            // 若从未发射过 SourceText 且无分隔符，保留完整输出作为译文；若已发射过 SourceText，剩余文本归属当前通道
            var channel = _awaitingFirstSection
                ? (_channelsWithContent.Contains(DeltaChannel.SourceText) ? DeltaChannel.SourceText : DeltaChannel.Translation)
                : _channel;

            if (channel == DeltaChannel.SourceText)
            {
                text = StripSourceTags(text);
            }
            else
            {
                var expMarkerIdx = FindExplanationPrefixIndex(text);
                if (expMarkerIdx >= 0)
                {
                    text = text[..expMarkerIdx];
                }
                text = StripTranslationTags(text);
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
            if (channel == DeltaChannel.Translation)
            {
                text = StripTranslationLeadingTags(text);
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
            text = StripSourceTags(text);
        }
        else if (channel == DeltaChannel.Translation)
        {
            var expIdx = FindExplanationPrefixIndex(text);
            if (expIdx >= 0)
            {
                text = text[..expIdx];
            }
            text = StripTranslationTags(text);
        }

        if (text.Length > 0)
        {
            yield return new UnderstandDelta(channel, text);
        }
    }

    /// <summary>
    /// 噪声标签清单（按现行两段式协议校准）：仅收录在现行协议下仍有真实产生源的兜底条目——
    /// 1. 提示词显式声明的结构占位符（&lt;annotated_source&gt; / &lt;translation&gt;，含方括号与冒号变体）；
    /// 2. 模型自发的中文分区标签（提示词将第一部分描述为「完成注音标注的原文」，故 原文/注音/译文 均有产生源）；
    /// 3. Markdown 代码围栏习惯。
    /// 旧版英文视觉提示词的 &lt;transcription&gt; 占位符系列随协议废弃已整体移除，不再兜底。
    /// 新增条目前先确认产生源，并由 TestNoiseTagCalibration 守住该契约。
    /// </summary>
    internal static readonly string[] SourceLeadingTags =
    [
        "<annotated_source>",
        "[annotated_source]",
        "annotated_source:",
        "```markdown",
        "```text",
        "```",
        "【原文】",
        "【注音】",
        "原文:",
        "原文："
    ];

    internal static readonly string[] TranslationLeadingTags =
    [
        "<translation>",
        "[translation]",
        "```markdown",
        "```text",
        "```",
        "【译文】",
        "译文:",
        "译文：",
        "translation:"
    ];

    internal static readonly string[] SourceCloseTags =
    [
        "</annotated_source>",
        "[/annotated_source]",
        "```markdown",
        "```text",
        "```"
    ];

    internal static readonly string[] TranslationCloseTags =
    [
        "</translation>",
        "[/translation]",
        "```markdown",
        "```text",
        "```"
    ];

    private static readonly string[] ExplanationSectionPrefixes =
    [
        "\r\n### [词汇",
        "\n### [词汇",
        "\r\n### 重点词汇",
        "\n### 重点词汇",
        "\r\n### 词汇",
        "\n### 词汇",
        "\r\n### [语法",
        "\n### [语法",
        "\r\n### 语法",
        "\n### 语法",
        "\r\n### [语境",
        "\n### [语境",
        "\r\n### 语境",
        "\n### 语境",
        "\r\n## [词汇",
        "\n## [词汇",
        "\r\n## 重点词汇",
        "\n## 重点词汇",
        "\r\n## 词汇",
        "\n## 词汇",
        "\r\n## [语法",
        "\n## [语法",
        "\r\n## 语法",
        "\n## 语法",
        "\r\n## 深度精讲",
        "\n## 深度精讲",
        "\r\n## [深度精讲",
        "\n## [深度精讲",
        "\r\n## 精讲",
        "\n## 精讲",
        "\r\n## [精讲",
        "\n## [精讲",
        "\r\n### 深度精讲",
        "\n### 深度精讲",
        "\r\n### [深度精讲",
        "\n### [深度精讲",
        "\r\n### 精讲",
        "\n### 精讲",
        "\r\n### 翻译精讲",
        "\n### 翻译精讲",
        "\r\n### [翻译精讲",
        "\n### [翻译精讲",
        "\r\n# 深度精讲",
        "\n# 深度精讲",
        "\r\n# 精讲",
        "\n# 精讲",
        "\r\n【精讲",
        "\n【精讲",
        "\r\n【词汇",
        "\n【词汇",
        "\r\n【语法",
        "\n【语法",
    ];

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
            foreach (var tag in SourceLeadingTags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[tag.Length..].TrimStart();
                    changed = true;
                    break;
                }
            }

            foreach (var tag in SourceCloseTags)
            {
                if (text.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^tag.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }
        } while (changed);

        // 彻底清除可能残留或嵌入的标签文本（支持尖括号、方括号与反引号）
        text = text.Replace("</annotated_source>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("<annotated_source>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("[/annotated_source]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("[annotated_source]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("【原文】", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("【注音】", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```markdown", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```text", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();

        return text;
    }

    private static string StripTranslationTags(string text)
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
            foreach (var tag in TranslationLeadingTags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[tag.Length..].TrimStart();
                    changed = true;
                    break;
                }
            }

            foreach (var tag in TranslationCloseTags)
            {
                if (text.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^tag.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }
        } while (changed);

        text = text.Replace("</translation>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("<translation>", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("[/translation]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("[translation]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("【译文】", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```markdown", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```text", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("```", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();

        return text;
    }

    private string StripSourceLeadingTags(string text)
    {
        if (_channelsWithContent.Contains(DeltaChannel.SourceText))
        {
            return text;
        }

        bool changed;
        do
        {
            changed = false;
            text = text.TrimStart('\r', '\n', ' ');
            foreach (var tag in SourceLeadingTags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[tag.Length..].TrimStart('\r', '\n', ' ');
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return text;
    }

    private static string StripTranslationLeadingTags(string text)
    {
        bool changed;
        do
        {
            changed = false;
            text = text.TrimStart('\r', '\n', ' ');
            foreach (var tag in TranslationLeadingTags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[tag.Length..].TrimStart('\r', '\n', ' ');
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return text;
    }

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

            if (IsOpenTagPrefix(trimmed, SourceLeadingTags))
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

    private int GetSafeTranslationFlushLength(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        // 1. 若译文尚未开始输出过有效正文，等待前导标签完整到达并剥离
        if (!_channelsWithContent.Contains(DeltaChannel.Translation))
        {
            var trimmed = text.TrimStart('\r', '\n', ' ');
            if (trimmed.Length == 0)
            {
                return 0;
            }

            if (IsOpenTagPrefix(trimmed, TranslationLeadingTags))
            {
                return 0;
            }
        }

        // 2. 检查是否出现精讲标记
        var expIdx = FindExplanationPrefixIndex(text);
        if (expIdx >= 0)
        {
            return expIdx;
        }

        // 3. 检查是否完整出现闭合标签
        foreach (var tag in TranslationCloseTags)
        {
            var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var safeLength = idx;
                while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
                {
                    safeLength--;
                }
                return safeLength;
            }
        }

        // 4. 检查尾部是否截断在闭合标签前缀中间
        var potentialCloseIndex = FindPotentialSuffixIndex(text, TranslationCloseTags);
        if (potentialCloseIndex >= 0)
        {
            var safeLength = potentialCloseIndex;
            while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
            {
                safeLength--;
            }
            return safeLength;
        }

        // 5. 检查尾部是否截断在精讲标记前缀中间
        var potentialExpIndex = FindPotentialExplanationPrefixIndex(text);
        if (potentialExpIndex >= 0)
        {
            var safeLength = potentialExpIndex;
            while (safeLength > 0 && (text[safeLength - 1] == '\r' || text[safeLength - 1] == '\n'))
            {
                safeLength--;
            }
            return safeLength;
        }

        return text.Length;
    }

    private static bool IsOpenTagPrefix(string text, string[] fullTags)
    {
        foreach (var fullTag in fullTags)
        {
            if (fullTag.StartsWith(text, StringComparison.OrdinalIgnoreCase) && text.Length < fullTag.Length)
            {
                return true;
            }
        }
        return false;
    }

    private static int FindExplanationPrefixIndex(string text)
    {
        var earliest = -1;
        foreach (var prefix in ExplanationSectionPrefixes)
        {
            var idx = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (earliest < 0 || idx < earliest))
            {
                earliest = idx;
            }
        }
        return earliest;
    }

    private static int FindPotentialExplanationPrefixIndex(string text)
    {
        var scanStart = Math.Max(0, text.Length - MaxSeparatorLength);
        for (var i = scanStart; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n' or '#'))
            {
                continue;
            }

            var suffix = text[i..];
            foreach (var prefix in ExplanationSectionPrefixes)
            {
                if (prefix.StartsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix.Length < prefix.Length)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static int FindPotentialSuffixIndex(string text, string[] candidateTags)
    {
        var scanStart = Math.Max(0, text.Length - MaxSeparatorLength);
        for (var i = scanStart; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n' or '<' or '[' or '`'))
            {
                continue;
            }

            var suffix = text[i..];
            foreach (var tag in candidateTags)
            {
                if (tag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix.Length < tag.Length)
                {
                    return i;
                }
            }
        }
        return -1;
    }

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

    private static int FindPotentialSeparatorOrTagPrefixIndex(string text)
    {
        var scanStart = Math.Max(0, text.Length - MaxSeparatorLength);
        for (var i = scanStart; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n' or '-' or '<' or '[' or '`'))
            {
                continue;
            }

            var suffix = text[i..];

            if (PromptTemplates.TranslationSentinel.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
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
}
