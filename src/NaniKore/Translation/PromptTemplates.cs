using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>内置提示词与视觉一条龙的轻量分段协议。</summary>
internal static class PromptTemplates
{
    // 协议唯一分隔符：换行、三个连字符、换行，共五个字符。
    internal const string SectionSeparator = "\n---\n";
    internal const string TargetLanguagePlaceholder = "{{target_language}}";
    internal const string SectionSeparatorPlaceholder = "{{section_separator}}";

    /// <summary>设置页始终展示的默认翻译提示词；主人可直接在此基础上修改。</summary>
    internal const string DefaultTranslationPrompt = """
        Translate the supplied text into {{target_language}}. 
        Return only the translation in {{target_language}}. Do not include explanations, notes, or source text.
        """;

    /// <summary>视觉一条龙的默认模板；内置模板包含分隔符占位符，供结果面板稳定拆分原文与译文。</summary>
    internal const string DefaultVisionOneshotPrompt = """
        Transcribe all readable text in the supplied image, then translate it into {{target_language}}.
        Return exactly this shape:
        <transcription>{{section_separator}}<translation>
        Do not include labels, markdown, commentary, or extra sections.
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

        if (request.ImagePng is { Length: > 0 })
        {
            var visionPrompt = string.IsNullOrWhiteSpace(prompts.VisionOneshot)
                ? DefaultVisionOneshotPrompt
                : prompts.VisionOneshot.Trim();
            return ExpandPlaceholders(visionPrompt, request.TargetLanguage);
        }

        var translationPrompt = string.IsNullOrWhiteSpace(prompts.Translate)
            ? DefaultTranslationPrompt
            : prompts.Translate.Trim();
        return ExpandTargetLanguage(translationPrompt, request.TargetLanguage);
    }

    private static string ExpandPlaceholders(string prompt, string targetLanguage) =>
        ExpandTargetLanguage(
            prompt.Replace(SectionSeparatorPlaceholder, SectionSeparator, StringComparison.Ordinal),
            targetLanguage);

    private static string ExpandTargetLanguage(string prompt, string targetLanguage) =>
        // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
        prompt.Replace(TargetLanguagePlaceholder, targetLanguage, StringComparison.Ordinal);
}

/// <summary>
/// 视觉流以唯一的五字符分隔符拆成原文和译文；无分隔符时保留完整输出作为译文。
/// </summary>
internal sealed class SectionChannelRouter
{
    private const int SeparatorLength = 5;

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

                // 保留分隔符长度减一的尾部，兼容五字符分隔符恰好跨 SSE 包切断。
                var flushLength = Math.Max(0, _pending.Length - SeparatorLength + 1);
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
                    foreach (var delta in Emit(channel, _pending[..marker.Value.Index]))
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
