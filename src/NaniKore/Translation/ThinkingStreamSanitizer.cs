using System.Text;

namespace NaniKore.Translation;

/// <summary>
/// 流式思考链状态机：实时过滤模型流式输出中的 &lt;think&gt;...&lt;/think&gt; 与 &lt;thought&gt;...&lt;/thought&gt; 标签；
/// 将思考内容剥离重定向为 DeltaChannel.Thinking，确保下游业务通道（分栏路由器与精讲解析器）绝不泄漏思考链。
/// </summary>
internal sealed class ThinkingStreamSanitizer
{
    private static readonly string[] OpenTags = ["<think>", "<thought>", "[think]", "[thought]"];
    private static readonly string[] CloseTags = ["</think>", "</thought>", "[/think]", "[/thought]"];

    private readonly StringBuilder _tagBuffer = new();
    private bool _insideThinking;
    private bool _skipNextNewline;

    /// <summary>
    /// 推入新的流式文本块，返回解析后的干净正文或思考内容片段。
    /// </summary>
    public IEnumerable<(string? CleanText, string? ThinkingText)> Push(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var index = 0;
        if (_skipNextNewline)
        {
            while (index < text.Length && (text[index] == '\r' || text[index] == '\n'))
            {
                index++;
            }

            if (index < text.Length)
            {
                _skipNextNewline = false;
            }
        }

        while (index < text.Length)
        {
            if (!_insideThinking)
            {
                if (_tagBuffer.Length > 0)
                {
                    _tagBuffer.Append(text[index++]);
                    var current = _tagBuffer.ToString();
                    if (MatchesAny(current, OpenTags))
                    {
                        _insideThinking = true;
                        _tagBuffer.Clear();
                        continue;
                    }

                    if (MatchesAny(current, CloseTags))
                    {
                        // 孤立闭合标签（如以 </think> 开头的模型流）：静默吞掉并跳过紧随的换行
                        _insideThinking = false;
                        _tagBuffer.Clear();
                        SkipNewline(text, ref index);
                        continue;
                    }

                    if (IsPrefixOfAny(current, OpenTags) || IsPrefixOfAny(current, CloseTags))
                    {
                        continue;
                    }

                    // 遇到不匹配前缀，通过滑动窗口逐字排出非标签字符
                    foreach (var output in FlushInvalidPrefix(insideThinking: false))
                    {
                        yield return output;
                    }

                    if (_insideThinking)
                    {
                        continue;
                    }

                    continue;
                }

                var nextTagIdx = text.IndexOfAny(['<', '['], index);
                if (nextTagIdx < 0)
                {
                    yield return (text[index..], null);
                    break;
                }

                if (nextTagIdx > index)
                {
                    yield return (text[index..nextTagIdx], null);
                }

                _tagBuffer.Append(text[nextTagIdx]);
                index = nextTagIdx + 1;

                var buf = _tagBuffer.ToString();
                if (MatchesAny(buf, OpenTags))
                {
                    _insideThinking = true;
                    _tagBuffer.Clear();
                }
                else if (MatchesAny(buf, CloseTags))
                {
                    _insideThinking = false;
                    _tagBuffer.Clear();
                    SkipNewline(text, ref index);
                }
            }
            else
            {
                if (_tagBuffer.Length > 0)
                {
                    _tagBuffer.Append(text[index++]);
                    var current = _tagBuffer.ToString();
                    if (MatchesAny(current, CloseTags))
                    {
                        _insideThinking = false;
                        _tagBuffer.Clear();
                        SkipNewline(text, ref index);
                        continue;
                    }

                    if (IsPrefixOfAny(current, CloseTags))
                    {
                        continue;
                    }

                    // 遇到不匹配前缀，通过滑动窗口逐字排出非标签字符作为思考内容
                    foreach (var output in FlushInvalidPrefix(insideThinking: true))
                    {
                        yield return output;
                    }

                    if (!_insideThinking)
                    {
                        SkipNewline(text, ref index);
                        continue;
                    }

                    continue;
                }

                var nextCloseIdx = text.IndexOfAny(['<', '['], index);
                if (nextCloseIdx < 0)
                {
                    yield return (null, text[index..]);
                    break;
                }

                if (nextCloseIdx > index)
                {
                    yield return (null, text[index..nextCloseIdx]);
                }

                _tagBuffer.Append(text[nextCloseIdx]);
                index = nextCloseIdx + 1;

                var buf = _tagBuffer.ToString();
                if (MatchesAny(buf, CloseTags))
                {
                    _insideThinking = false;
                    _tagBuffer.Clear();
                    SkipNewline(text, ref index);
                }
            }
        }
    }

    private void SkipNewline(string text, ref int index)
    {
        while (index < text.Length && (text[index] == '\r' || text[index] == '\n'))
        {
            index++;
        }

        if (index >= text.Length)
        {
            _skipNextNewline = true;
        }
    }

    /// <summary>
    /// 流结束时刷新可能残留的缓冲字符。
    /// </summary>
    public IEnumerable<(string? CleanText, string? ThinkingText)> Complete()
    {
        if (_tagBuffer.Length > 0)
        {
            var remaining = _tagBuffer.ToString();
            _tagBuffer.Clear();

            // 若残留内容是思考标签或孤立闭合标签，静默丢弃，绝不泄漏给正文
            if (MatchesAny(remaining, OpenTags) || MatchesAny(remaining, CloseTags))
            {
                yield break;
            }

            if (_insideThinking)
            {
                yield return (null, remaining);
            }
            else
            {
                yield return (remaining, null);
            }
        }
    }

    private IEnumerable<(string? CleanText, string? ThinkingText)> FlushInvalidPrefix(bool insideThinking)
    {
        while (_tagBuffer.Length > 0)
        {
            var current = _tagBuffer.ToString();
            if (insideThinking)
            {
                if (MatchesAny(current, CloseTags))
                {
                    _insideThinking = false;
                    _tagBuffer.Clear();
                    _skipNextNewline = true;
                    yield break;
                }

                if (IsPrefixOfAny(current, CloseTags))
                {
                    yield break;
                }
            }
            else
            {
                if (MatchesAny(current, OpenTags))
                {
                    _insideThinking = true;
                    _tagBuffer.Clear();
                    yield break;
                }

                if (MatchesAny(current, CloseTags))
                {
                    _insideThinking = false;
                    _tagBuffer.Clear();
                    _skipNextNewline = true;
                    yield break;
                }

                if (IsPrefixOfAny(current, OpenTags) || IsPrefixOfAny(current, CloseTags))
                {
                    yield break;
                }
            }

            var ch = _tagBuffer[0].ToString();
            _tagBuffer.Remove(0, 1);
            yield return insideThinking ? (null, ch) : (ch, null);
        }
    }

    private static bool MatchesAny(string text, string[] tags)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(text, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPrefixOfAny(string prefix, string[] tags)
    {
        foreach (var tag in tags)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

