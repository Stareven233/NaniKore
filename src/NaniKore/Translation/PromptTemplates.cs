using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>内置提示词与视觉一条龙的稳健分段哨兵。</summary>
internal static class PromptTemplates
{
    internal const string SourceSentinel = "<<<SOURCE>>>";
    internal const string TranslationSentinel = "<<<TRANSLATION>>>";
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts)
    {
        var custom = request.Kind == UnderstandKind.Translate ? prompts.Translate : prompts.Explain;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
            return custom.Trim().Replace(TargetLanguagePlaceholder, request.TargetLanguage, StringComparison.Ordinal);
        }

        return request.Kind switch
        {
            UnderstandKind.Explain => $"Explain the supplied text clearly in {request.TargetLanguage}. Keep the answer concise.",
            _ when request.ImagePng is { Length: > 0 } =>
                $"Read all visible text in the image and translate it into {request.TargetLanguage}. " +
                $"Stream the transcription after {SourceSentinel}, then stream only the translation in {request.TargetLanguage} after {TranslationSentinel}. " +
                "Do not add commentary or any extra sections.",
            _ => $"Translate the supplied text into {request.TargetLanguage}. " +
                $"Return only the translation in {request.TargetLanguage}; do not include explanations, notes, or source text.",
        };
    }
}

/// <summary>
/// 视觉模型的文本流不依赖 JSON 整体完好；哨兵缺失只会造成频道错位，不会令整块结果丢失。
/// </summary>
internal sealed class SentinelChannelRouter
{
    private DeltaChannel _channel = DeltaChannel.Translation;
    private string _pending = string.Empty;

    public IEnumerable<UnderstandDelta> Push(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        _pending += text;
        while (_pending.Length > 0)
        {
            var sourceIndex = _pending.IndexOf(PromptTemplates.SourceSentinel, StringComparison.Ordinal);
            var translationIndex = _pending.IndexOf(PromptTemplates.TranslationSentinel, StringComparison.Ordinal);
            var markerIndex = MinNonNegative(sourceIndex, translationIndex);
            if (markerIndex < 0)
            {
                // 保留最长哨兵长度减一的尾部，兼容标记恰好跨 SSE 包切断。
                var flushLength = Math.Max(0, _pending.Length - PromptTemplates.TranslationSentinel.Length + 1);
                if (flushLength == 0)
                {
                    yield break;
                }

                yield return new UnderstandDelta(_channel, _pending[..flushLength]);
                _pending = _pending[flushLength..];
                continue;
            }

            if (markerIndex > 0)
            {
                yield return new UnderstandDelta(_channel, _pending[..markerIndex]);
            }

            var isSource = markerIndex == sourceIndex;
            var markerLength = isSource ? PromptTemplates.SourceSentinel.Length : PromptTemplates.TranslationSentinel.Length;
            _channel = isSource ? DeltaChannel.SourceText : DeltaChannel.Translation;
            _pending = _pending[(markerIndex + markerLength)..];
        }
    }

    public IEnumerable<UnderstandDelta> Complete()
    {
        if (_pending.Length > 0)
        {
            yield return new UnderstandDelta(_channel, _pending);
            _pending = string.Empty;
        }
    }

    private static int MinNonNegative(int left, int right) => left switch
    {
        < 0 => right,
        _ when right < 0 => left,
        _ => Math.Min(left, right),
    };
}
