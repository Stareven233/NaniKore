using System.Net.Http;
using System.Text.Json;
using NaniKore.Config;

namespace NaniKore.Translation.Providers;

internal sealed class ClaudeProvider : TranslationProviderBase
{
    public ClaudeProvider(ProviderProfile profile, string apiKey, PromptConfig prompts) : base(profile, apiKey, prompts) { }

    public override async IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProviderFactory.ValidateCapabilities(request, Capabilities);
        using var message = CreateRequest(request);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = request.Kind == UnderstandKind.Translate
            ? new SectionChannelRouter()
            : null;
        var sanitizer = new ThinkingStreamSanitizer();
        var inputTokens = 0;
        var outputTokens = 0;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            ExtractClaudeTokens(frame, ref inputTokens, ref outputTokens);
            var (text, thinking) = ExtractDelta(frame);

            if (!string.IsNullOrEmpty(thinking))
            {
                yield return new UnderstandDelta(DeltaChannel.Thinking, thinking);
            }

            if (!string.IsNullOrEmpty(text) && request.Session is not null && request.Kind == UnderstandKind.Translate)
            {
                request.Session.AppendRawAssistantOutput(text);
            }

            foreach (var delta in Route(router, sanitizer, text, request.Kind))
            {
                yield return delta;
            }
        }

        foreach (var delta in CompleteStream(router, sanitizer, request.Kind))
        {
            yield return delta;
        }

        if (inputTokens > 0 || outputTokens > 0)
        {
            var endChannel = request.Kind == UnderstandKind.Explain ? DeltaChannel.Explanation : DeltaChannel.Translation;
            yield return new UnderstandDelta(endChannel, string.Empty, new TokenUsage(inputTokens, outputTokens));
        }
    }

    internal HttpRequestMessage CreateRequest(UnderstandRequest request)
    {
        var system = PromptTemplates.GetSystemPrompt(request, Prompts);
        object messages;
        if (request.Session is { Messages.Count: > 1 } session)
        {
            var list = new List<object>();
            foreach (var m in session.Messages)
            {
                var role = m.Role == ChatRole.Assistant ? "assistant" : "user";
                var content = CreateContent(m.ImagePng, m.Text);
                list.Add(new { role, content });
            }
            messages = list.ToArray();
        }
        else
        {
            var content = CreateContent(request.ImagePng, request.SourceText);
            messages = new[] { new { role = "user", content } };
        }

        var bodyDict = new Dictionary<string, object?>
        {
            ["model"] = Profile.Model,
            ["stream"] = true,
            ["max_tokens"] = 4096,
            ["system"] = system,
            ["messages"] = messages,
        };

        if (Profile.EnableThinking)
        {
            bodyDict["thinking"] = new { type = "enabled", budget_tokens = 1024 };
        }

        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "v1/messages"), bodyDict);
        message.Headers.Add("x-api-key", ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
    }

    internal static object CreateContent(ReadOnlyMemory<byte>? imagePng, string? sourceText)
    {
        if (imagePng is not { Length: > 0 } image)
        {
            return sourceText ?? string.Empty;
        }

        var blocks = new List<object>
        {
            new { type = "image", source = new { type = "base64", media_type = "image/png", data = Base64(image) } },
        };
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            blocks.Add(new { type = "text", text = sourceText });
        }

        return blocks;
    }

    internal static (string Text, string Thinking) ExtractDelta(SseEvent frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = GetText(root, "type");
            var isDelta = string.Equals(frame.Event, "content_block_delta", StringComparison.Ordinal) ||
                          string.Equals(type, "content_block_delta", StringComparison.Ordinal);
            if (isDelta)
            {
                if (root.TryGetProperty("delta", out var deltaElem) && deltaElem.ValueKind == JsonValueKind.Object)
                {
                    var deltaType = GetText(deltaElem, "type");
                    if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal) ||
                        deltaElem.TryGetProperty("thinking", out _))
                    {
                        var thinking = GetTextContent(deltaElem, "thinking");
                        return (string.Empty, thinking);
                    }

                    return (GetTextContent(deltaElem, "text"), string.Empty);
                }

                return (GetTextContent(root, "delta", "text"), string.Empty);
            }

            // 兼容视觉端点忽略 stream=true 后返回的 Anthropic message JSON。
            if (frame.Event is null && (string.IsNullOrEmpty(type) || string.Equals(type, "message", StringComparison.Ordinal)))
            {
                return (GetTextContent(root, "content"), string.Empty);
            }

            return (string.Empty, string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    internal static void ExtractClaudeTokens(SseEvent frame, ref int inputTokens, ref int outputTokens)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = GetText(root, "type");
            if (string.Equals(frame.Event, "message_start", StringComparison.Ordinal) ||
                string.Equals(type, "message_start", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv))
                    {
                        inputTokens = itv;
                    }
                }
            }
            else if (string.Equals(frame.Event, "message_delta", StringComparison.Ordinal) ||
                     string.Equals(type, "message_delta", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv))
                    {
                        outputTokens = otv;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }
}
