using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>内置提示词与视觉一条龙的稳健分段哨兵。</summary>
internal static class PromptTemplates
{
    internal const string SourceSentinel = "<<<SOURCE>>>";
    internal const string TranslationSentinel = "<<<TRANSLATION>>>";
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    /// <summary>设置页始终展示的默认翻译提示词；主人可直接在此基础上修改。</summary>
    internal const string DefaultTranslationPrompt = """
        Translate the supplied text into {{target_language}}.
        Return only the translation in {{target_language}}. Do not include explanations, notes, or source text.
        """;

    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts)
    {
        if (request.Kind == UnderstandKind.Explain)
        {
            var explainPrompt = string.IsNullOrWhiteSpace(prompts.Explain)
                ? "Explain the supplied text clearly in {{target_language}}. Keep the answer concise."
                : prompts.Explain.Trim();
            return ExpandTargetLanguage(explainPrompt, request.TargetLanguage);
        }

        var translationPrompt = string.IsNullOrWhiteSpace(prompts.Translate)
            ? DefaultTranslationPrompt
            : prompts.Translate.Trim();
        translationPrompt = ExpandTargetLanguage(translationPrompt, request.TargetLanguage);

        if (request.ImagePng is not { Length: > 0 })
        {
            return translationPrompt;
        }

        // 图像一条龙仍需要稳定的协议标记供流式结果分栏；这属于传输协议，不限制主人修改翻译要求。
        return $"{translationPrompt}\n\n" +
            $"For an image input, stream the transcription after {SourceSentinel}, then stream only the translation after {TranslationSentinel}. " +
            "Do not add commentary or any extra sections.";
    }

    private static string ExpandTargetLanguage(string prompt, string targetLanguage) =>
        // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
        prompt.Replace(TargetLanguagePlaceholder, targetLanguage, StringComparison.Ordinal);
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
