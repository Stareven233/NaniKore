using System.Net.Http;
using System.Text.Json;
using NaniKore.Config;

namespace NaniKore.Translation.Providers;

internal sealed class OpenAiProvider : TranslationProviderBase
{
    public OpenAiProvider(ProviderProfile profile, string apiKey, PromptConfig prompts) : base(profile, apiKey, prompts) { }

    public override async IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProviderFactory.ValidateCapabilities(request, Capabilities);
        var isResponses = string.Equals(Profile.ApiType, "responses", StringComparison.OrdinalIgnoreCase);
        using var message = isResponses ? CreateResponsesRequest(request) : CreateCompletionsRequest(request);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = (request.Kind == UnderstandKind.Translate || request.ImagePng is { Length: > 0 })
            ? new SectionChannelRouter()
            : null;
        TokenUsage? usage = null;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data == "[DONE]")
            {
                break;
            }

            var frameUsage = isResponses
                ? ExtractResponsesUsage(frame.Data)
                : ExtractCompletionsUsage(frame.Data);
            if (frameUsage.HasValue)
            {
                usage = frameUsage;
            }

            var text = isResponses
                ? ExtractResponsesDelta(frame)
                : ExtractCompletionsDelta(frame.Data);
            foreach (var delta in Route(router, text, request.Kind))
            {
                yield return delta;
            }
        }

        if (router is not null)
        {
            foreach (var delta in router.Complete())
            {
                yield return delta;
            }
        }

        if (usage.HasValue)
        {
            yield return new UnderstandDelta(DeltaChannel.Translation, string.Empty, usage.Value);
        }
    }

    private HttpRequestMessage CreateCompletionsRequest(UnderstandRequest request)
    {
        var system = PromptTemplates.GetSystemPrompt(request, Prompts);
        object userContent = request.ImagePng is { Length: > 0 } image
            ? new object[]
            {
                new { type = "text", text = request.SourceText ?? "" },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{Base64(image)}" } },
            }
            : request.SourceText ?? string.Empty;
        var body = new
        {
            model = Profile.Model,
            stream = true,
            stream_options = new { include_usage = true },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent },
            },
        };
        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "chat/completions"), body);
        SetBearerToken(message, ApiKey);
        return message;
    }

    private HttpRequestMessage CreateResponsesRequest(UnderstandRequest request)
    {
        var system = PromptTemplates.GetSystemPrompt(request, Prompts);
        var content = new List<object> { new { type = "input_text", text = request.SourceText ?? string.Empty } };
        if (request.ImagePng is { Length: > 0 } image)
        {
            content.Add(new { type = "input_image", image_url = $"data:image/png;base64,{Base64(image)}" });
        }

        var body = new
        {
            model = Profile.Model,
            stream = true,
            input = new object[]
            {
                new { role = "system", content = new object[] { new { type = "input_text", text = system } } },
                new { role = "user", content },
            },
        };
        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "responses"), body);
        SetBearerToken(message, ApiKey);
        return message;
    }

    private static string ExtractCompletionsDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var delta = GetTextContent(root, "choices", "0", "delta", "content");
            if (delta.Length > 0)
            {
                return delta;
            }

            delta = GetTextContent(root, "choices", "0", "delta");
            return delta.Length > 0
                ? delta
                : GetTextContent(root, "choices", "0", "message", "content");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string ExtractResponsesDelta(SseEvent frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var eventType = GetText(root, "type");
            if (!string.Equals(frame.Event, "response.output_text.delta", StringComparison.Ordinal) &&
                !string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal))
            {
                if (frame.Event is null && string.IsNullOrEmpty(eventType))
                {
                    var outputText = GetTextContent(root, "output_text");
                    return outputText.Length > 0 ? outputText : GetTextContent(root, "output");
                }

                return string.Empty;
            }

            return GetTextContent(root, "delta");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<UnderstandDelta> Route(SectionChannelRouter? router, string text, UnderstandKind kind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (router is not null)
        {
            return router.Push(text);
        }

        var channel = kind == UnderstandKind.Explain
            ? DeltaChannel.Explanation
            : DeltaChannel.Translation;
        return [new UnderstandDelta(channel, text)];
    }

    internal static TokenUsage? ExtractCompletionsUsage(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
            {
                var input = usageElem.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : 0;
                var output = usageElem.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv) ? ctv : 0;
                if (input > 0 || output > 0)
                {
                    return new TokenUsage(input, output);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static TokenUsage? ExtractResponsesUsage(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            JsonElement usageElem;
            if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out usageElem))
            {
                var input = usageElem.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                var output = usageElem.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
                if (input > 0 || output > 0)
                {
                    return new TokenUsage(input, output);
                }
            }
            else if (root.TryGetProperty("usage", out usageElem))
            {
                var input = usageElem.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                var output = usageElem.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
                if (input > 0 || output > 0)
                {
                    return new TokenUsage(input, output);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
