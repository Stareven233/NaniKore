namespace NaniKore.Translation;

/// <summary>
/// 流式输出的通道路由模式：由上游组装路径显式决定，路由器不再靠内容特征猜通道（issue 008 Phase 3）。
/// </summary>
internal enum StreamChannelMode
{
    /// <summary>
    /// 文本路径直通：模型只回译文（提示词已解耦），全部增量归译文通道，
    /// 仅保留思考链越界截断与噪声标签剥离两道守门；不识别任何原文回声。
    /// </summary>
    Passthrough,

    /// <summary>
    /// 视觉链路两段式：按唯一分段标记 <c>---TRANSLATION---</c> 拆成纯转录与译文两个通道；
    /// 无标记时保留完整输出作为译文（协议降级兜底）。
    /// </summary>
    Segmenter,
}

/// <summary>
/// 流式输出分栏路由器：按 <see cref="StreamChannelMode"/> 显式分流。
///   <see cref="StreamChannelMode.Passthrough"/> 文本路径——译文直通，无原文通道；
///   <see cref="StreamChannelMode.Segmenter"/>   视觉路径——哨兵前归纯转录通道、哨兵后归译文通道。
/// 两条路径共用同一套通道发射（首段前导标签剥离、空段抑制、思考链越界截断），
/// 差别只在「哨兵是否存在」——曾经的「显式纯译文判定 / 首字内容猜测」幽灵逻辑已退役：
/// 通道归属必须由路由模式显式给出，绝不靠输出内容反推。
/// </summary>
internal sealed class SectionChannelRouter
{
    private const int MaxSeparatorLength = 32;

    private readonly StreamChannelMode _mode;
    private DeltaChannel _channel = DeltaChannel.Translation;
    private string _pending = string.Empty;
    private bool _awaitingFirstSection = true;
    private bool _explanationTruncated = false;
    private readonly HashSet<DeltaChannel> _channelsWithContent = [];

    public SectionChannelRouter(StreamChannelMode mode)
    {
        _mode = mode;
    }

    public IEnumerable<UnderstandDelta> Push(string text)
    {
        if (string.IsNullOrEmpty(text) || _explanationTruncated)
        {
            yield break;
        }

        _pending += text;

        if (_mode == StreamChannelMode.Passthrough)
        {
            // 文本路径：无哨兵可拆，译文通道恒定直通
            foreach (var delta in PushPassthrough())
            {
                yield return delta;
            }

            yield break;
        }

        foreach (var delta in PushSegmenter())
        {
            yield return delta;
        }
    }

    /// <summary>直通模式：全部增量归译文通道，只做思考链越界截断与噪声标签剥离。</summary>
    private IEnumerable<UnderstandDelta> PushPassthrough()
    {
        while (_pending.Length > 0)
        {
            // 模型若越界开始输出精讲 markdown，立即截断，绝不允许混杂在译文中
            var expMarkerIdx = FindExplanationPrefixIndex(_pending);
            if (expMarkerIdx >= 0)
            {
                foreach (var delta in Emit(DeltaChannel.Translation, _pending[..expMarkerIdx]))
                {
                    yield return delta;
                }

                _pending = string.Empty;
                _explanationTruncated = true;
                yield break;
            }

            var flushLength = GetSafeTranslationFlushLength(_pending);
            if (flushLength == 0)
            {
                yield break;
            }

            foreach (var delta in Emit(DeltaChannel.Translation, _pending[..flushLength]))
            {
                yield return delta;
            }

            _pending = _pending[flushLength..];
        }
    }

    /// <summary>两段式：哨兵前归纯转录通道（先到的假名/汉字可实时流出），哨兵后归译文通道。</summary>
    private IEnumerable<UnderstandDelta> PushSegmenter()
    {
        while (_pending.Length > 0)
        {
            var markerIndex = _pending.IndexOf(PromptTemplates.TranslationSentinel, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                if (_awaitingFirstSection)
                {
                    _pending = StripSourceLeadingTags(_pending);

                    // 原文阶段：通过滑动窗口安全实时输出纯转录，打破整段阻塞卡顿
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

            // 两段式协议降级兜底：从未收到过哨兵且未发射过转录时，保留完整输出作为译文；
            // 已发射过转录则剩余文本归属当前通道；直通模式恒为译文通道。
            var channel = _awaitingFirstSection && _mode == StreamChannelMode.Segmenter
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
    /// 噪声标签清单（按现行协议校准）：仅收录在现行协议下仍有真实产生源的兜底条目——
    /// 1. 提示词显式声明的结构占位符（&lt;transcribed_text&gt; / &lt;translation&gt;，含方括号与冒号变体）；
    /// 2. 模型自发的中文分区标签（提示词将第一部分描述为「转录」，故 原文/译文 均有产生源）；
    /// 3. 思考链标签（由 Sanitizer 之外的兜底路径产生）。
    /// </summary>
    private static readonly string[] ThinkingNoiseTags =
    [
        "<think>",
        "</think>",
        "<thought>",
        "</thought>",
        "[think]",
        "[/think]",
        "[thought]",
        "[/thought]"
    ];

    internal static readonly string[] SourceLeadingTags =
    [
        .. ThinkingNoiseTags,
        "<transcribed_text>",
        "[transcribed_text]",
        "transcribed_text:",
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
        .. ThinkingNoiseTags,
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
        .. ThinkingNoiseTags,
        "</transcribed_text>",
        "[/transcribed_text]",
        "</annotated_source>",
        "[/annotated_source]",
        "```markdown",
        "```text",
        "```"
    ];

    internal static readonly string[] TranslationCloseTags =
    [
        .. ThinkingNoiseTags,
        "</translation>",
        "[/translation]",
        "```markdown",
        "```text",
        "```"
    ];

    // 源通道尾部滑动保护候选：分段哨兵与源闭合标签共用同一个后缀检测窗口
    private static readonly string[] SourceTailSentinelTags = [PromptTemplates.TranslationSentinel, .. SourceCloseTags];

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

    private static readonly string[] SourceResidualNoise =
    [
        .. ThinkingNoiseTags,
        "</transcribed_text>",
        "<transcribed_text>",
        "[/transcribed_text]",
        "[transcribed_text>",
        "</annotated_source>",
        "<annotated_source>",
        "[/annotated_source]",
        "[annotated_source>",
        "【原文】",
        "【注音】",
        "```markdown",
        "```text",
        "```"
    ];

    private static readonly string[] TranslationResidualNoise =
    [
        .. ThinkingNoiseTags,
        "</translation>",
        "<translation>",
        "[/translation]",
        "[translation>",
        "【译文】",
        "```markdown",
        "```text",
        "```"
    ];

    /// <summary>段级噪声标签清洗：原文/译文两通道共用唯一实现，差别仅在各自的标签与残留清单。</summary>
    private static string StripSectionTags(string text, string[] leadingTags, string[] closeTags, string[] residualNoise)
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
            foreach (var tag in leadingTags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[tag.Length..].TrimStart();
                    changed = true;
                    break;
                }
            }

            foreach (var tag in closeTags)
            {
                if (text.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^tag.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }
        } while (changed);

        foreach (var noise in residualNoise)
        {
            text = text.Replace(noise, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }

    private static string StripSourceTags(string text) =>
        StripSectionTags(text, SourceLeadingTags, SourceCloseTags, SourceResidualNoise);

    private static string StripTranslationTags(string text) =>
        StripSectionTags(text, TranslationLeadingTags, TranslationCloseTags, TranslationResidualNoise);

    /// <summary>
    /// 循环剥离前导噪声标签，首部 CR/LF/空格顺带清掉；两通道共用同一核心循环。
    /// 原文通道在已产出正文后不再回剥（内容是真实转录），译文通道沿用恒定行为。
    /// </summary>
    private static string TrimLeadingTagsCore(string text, string[] tags)
    {
        bool changed;
        do
        {
            changed = false;
            text = text.TrimStart('\r', '\n', ' ');
            foreach (var tag in tags)
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

    private string StripSourceLeadingTags(string text) =>
        _channelsWithContent.Contains(DeltaChannel.SourceText)
            ? text
            : TrimLeadingTagsCore(text, SourceLeadingTags);

    private static string StripTranslationLeadingTags(string text) =>
        TrimLeadingTagsCore(text, TranslationLeadingTags);

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

            // 无假名的转录（如纯汉字图）先挂起，等哨兵到达再整段归位，避免被误当译文提前流出
            if (!HasSourceSignal(text))
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
        var potentialSuffixIndex = FindPotentialSuffixIndex(text, SourceTailSentinelTags);
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
            if (ch is not ('\r' or '\n' or '-' or '<' or '[' or '`'))
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

    /// <summary>转录通道的实时流出信号：假名（可先行流式）或转录里的方括号内容；无信号的纯汉字转录等哨兵归位。</summary>
    private static bool HasSourceSignal(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
            {
                return true;
            }

            // 转录文本自身带方括号注释（如原著中的 注[ちゅう]）同样构成可提前流出的信号
            if (c == '[' && i > 0 && char.IsLetterOrDigit(text[i - 1]))
            {
                var closeIdx = text.IndexOf(']', i + 1);
                if (closeIdx > i + 1)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
