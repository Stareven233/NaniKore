namespace NaniKore.Translation;

/// <summary>
/// 流式输出按唯一的分段标记 <c>---TRANSLATION---</c> 拆成原文与译文两个通道；
/// 标记前后的空白与噪声标签由路由器剥离，无标记时保留完整输出作为译文。
/// </summary>
internal sealed class SectionChannelRouter
{
    private const int MaxSeparatorLength = 32;

    private readonly string? _expectedSourceText;
    private DeltaChannel _channel = DeltaChannel.Translation;
    private string _pending = string.Empty;
    private bool _awaitingFirstSection = true;
    private bool _explanationTruncated = false;
    private readonly HashSet<DeltaChannel> _channelsWithContent = [];

    public SectionChannelRouter(string? expectedSourceText = null)
    {
        _expectedSourceText = expectedSourceText;
    }

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
                    _pending = StripSourceLeadingTags(_pending);

                    // 快速路径：若识别到当前输出已显式为纯译文（如缺少原文假名/首字符不匹配/译文标签），立即切至译文通道流式输出
                    if (IsObviousPureTranslation(_pending, _expectedSourceText))
                    {
                        _channel = DeltaChannel.Translation;
                        _awaitingFirstSection = false;
                        _pending = StripTranslationLeadingTags(_pending);
                        continue;
                    }

                    // 原文阶段：通过滑动窗口安全实时输出带注音原文，打破整段阻塞卡顿
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
    /// 原文通道在已产出正文后不再回剥（内容是真实注音），译文通道沿用恒定行为。
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

            // 若无原文特征（假名/规范注音），绝不提前作为 SourceText 刷新，等待 sentinel 到达或作为纯译文 fallback
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

    private static bool HasSourceSignal(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
            {
                return true;
            }

            // 振假名/注音格式必须形如 汉字[注音] 或 Word[ipa]，单个孤立方括号不构成原文特征
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

    /// <summary>
    /// 判定待处理文本是否显式为纯译文（模型未遵循两段式协议直接输出译文）。
    /// </summary>
    private static bool IsObviousPureTranslation(string pending, string? expectedSourceText)
    {
        if (string.IsNullOrWhiteSpace(pending))
        {
            return false;
        }

        var trimmed = pending.TrimStart('\r', '\n', ' ');
        if (trimmed.Length == 0)
        {
            return false;
        }

        // 若当前处于前导标签未完结前缀（如 "<anno"），等待完整标签到达，绝不提前误判
        if (IsOpenTagPrefix(trimmed, SourceLeadingTags))
        {
            return false;
        }

        // 1. 显式译文前缀标识（<translation>、[translation]、译文:、translation: 等）
        foreach (var tag in TranslationLeadingTags)
        {
            if (trimmed.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(expectedSourceText))
        {
            return false;
        }

        var trimmedExpected = expectedSourceText.Trim();
        if (trimmedExpected.Length == 0)
        {
            return false;
        }

        // 2. 原文含有假名（日语），而当前输出积累了一定字符且完全无假名，并出现了汉字或拉丁字母，且首字与原文不一致
        var expectedHasKana = HasJapaneseKana(trimmedExpected);
        if (expectedHasKana)
        {
            var pendingHasKana = HasJapaneseKana(trimmed);
            if (!pendingHasKana)
            {
                if (trimmed[0] != trimmedExpected[0] && HasHanOrLatin(trimmed))
                {
                    return true;
                }
            }
        }
        else if (HasLatinOnly(trimmedExpected))
        {
            // 原文是纯拉丁/英语，当前输出出现中文字符且首字符不匹配
            if (trimmed[0] != trimmedExpected[0] && HasHan(trimmed))
            {
                return true;
            }
        }
        else if (HasHan(trimmedExpected) && !expectedHasKana)
        {
            // 原文是中文，当前输出出现假名或纯拉丁且首字不匹配
            if (trimmed[0] != trimmedExpected[0] && (HasJapaneseKana(trimmed) || HasLatinOnly(trimmed)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasJapaneseKana(string str)
    {
        foreach (var c in str)
        {
            if (c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasHan(string str)
    {
        foreach (var c in str)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasHanOrLatin(string str)
    {
        foreach (var c in str)
        {
            if (c is (>= '\u4E00' and <= '\u9FFF') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLatinOnly(string str)
    {
        var hasLetter = false;
        foreach (var c in str)
        {
            if (c is >= '\u4E00' and <= '\u9FFF' or (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
            {
                return false;
            }
            if (char.IsAsciiLetter(c))
            {
                hasLetter = true;
            }
        }
        return hasLetter;
    }
}
