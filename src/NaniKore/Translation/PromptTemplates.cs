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
    private const string LegacySourceSentinel = "<<<SOURCE>>";
    private const string LegacyTranslationSentinel = "<<<TRANSLATION>>";
    private const int MaxSentinelLength = 17;

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
                    // 首个 SOURCE/TRANSLATION 哨兵之前的内容是模型格式化前导，不应进入译文框。
                    yield break;
                }

                // 保留最长哨兵长度减一的尾部，兼容标记恰好跨 SSE 包切断。
                var flushLength = Math.Max(0, _pending.Length - MaxSentinelLength + 1);
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

            if (marker.Value.Index > 0 && !_awaitingFirstSection)
            {
                foreach (var delta in Emit(_channel, _pending[..marker.Value.Index]))
                {
                    yield return delta;
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
            (Marker: PromptTemplates.SourceSentinel, Channel: DeltaChannel.SourceText, IsLegacy: false),
            (Marker: PromptTemplates.TranslationSentinel, Channel: DeltaChannel.Translation, IsLegacy: false),
            (Marker: LegacySourceSentinel, Channel: DeltaChannel.SourceText, IsLegacy: true),
            (Marker: LegacyTranslationSentinel, Channel: DeltaChannel.Translation, IsLegacy: true),
        })
        {
            var index = text.IndexOf(candidate.Marker, StringComparison.Ordinal);
            if (index < 0 ||
                candidate.IsLegacy && index + candidate.Marker.Length == text.Length ||
                (result is not null &&
                 (index > result.Value.Index ||
                  index == result.Value.Index && candidate.Marker.Length <= result.Value.Length)))
            {
                // 短标记落在增量末尾时先等待下一包，避免把三右尖括号标记拆包误识别成短标记。
                continue;
            }

            result = new SentinelMatch(index, candidate.Channel, candidate.Marker.Length);
        }

        return result;
    }

    private readonly record struct SentinelMatch(int Index, DeltaChannel Channel, int Length);
}
