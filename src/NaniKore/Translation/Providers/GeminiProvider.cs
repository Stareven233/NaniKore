using System.Net.Http;
using System.Text.Json;
using NaniKore.Config;

namespace NaniKore.Translation.Providers;

internal sealed class GeminiProvider : TranslationProviderBase
{
    public GeminiProvider(ProviderProfile profile, string apiKey, PromptConfig prompts) : base(profile, apiKey, prompts) { }

    public override async IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProviderFactory.ValidateCapabilities(request, Capabilities);
        using var message = CreateRequest(request);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = (request.Kind == UnderstandKind.Translate || request.ImagePng is { Length: > 0 })
            ? new SectionChannelRouter()
            : null;
        var inputTokens = 0;
        var outputTokens = 0;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            ExtractGeminiTokens(frame.Data, ref inputTokens, ref outputTokens);
            var text = ExtractDelta(frame.Data);
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

        if (inputTokens > 0 || outputTokens > 0)
        {
            yield return new UnderstandDelta(DeltaChannel.Translation, string.Empty, new TokenUsage(inputTokens, outputTokens));
        }
    }

    private HttpRequestMessage CreateRequest(UnderstandRequest request)
    {
        var parts = new List<object> { new { text = request.SourceText ?? string.Empty } };
        if (request.ImagePng is { Length: > 0 } image)
        {
            parts.Add(new { inline_data = new { mime_type = "image/png", data = Base64(image) } });
        }

        var body = new
        {
            system_instruction = new { parts = new[] { new { text = PromptTemplates.GetSystemPrompt(request, Prompts) } } },
            contents = new[] { new { role = "user", parts } },
        };
        var endpoint = $"models/{Uri.EscapeDataString(Profile.Model)}:streamGenerateContent?alt=sse";
        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, endpoint), body);
        message.Headers.Add("x-goog-api-key", ApiKey);
        return message;
    }

    private static string ExtractDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return GetTextContent(document.RootElement, "candidates");
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

    internal static void ExtractGeminiTokens(string data, ref int inputTokens, ref int outputTokens)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("usageMetadata", out var usageMeta))
            {
                if (usageMeta.TryGetProperty("promptTokenCount", out var pt) && pt.TryGetInt32(out var ptv))
                {
                    inputTokens = ptv;
                }
                if (usageMeta.TryGetProperty("candidatesTokenCount", out var ct) && ct.TryGetInt32(out var ctv))
                {
                    outputTokens = ctv;
                }
            }
        }
        catch (JsonException)
        {
        }
    }
}
