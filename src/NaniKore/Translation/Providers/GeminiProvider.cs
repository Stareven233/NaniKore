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
        var requestId = TranslationDiagnostics.GetRequestId(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = request.ImagePng is { Length: > 0 } ? new SentinelChannelRouter() : null;
        var frameCount = 0;
        var extractedTextLength = 0;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            frameCount++;
            var text = ExtractDelta(frame.Data);
            TranslationDiagnostics.ResponseFrame(
                Profile,
                requestId,
                "gemini",
                frame.Event,
                frame.Data,
                text.Length);
            extractedTextLength += text.Length;
            foreach (var delta in Route(router, text))
            {
                yield return delta;
            }
        }

        TranslationDiagnostics.ResponseStreamCompleted(Profile, requestId, "gemini", frameCount, extractedTextLength);

        if (router is not null)
        {
            foreach (var delta in router.Complete())
            {
                yield return delta;
            }
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

    private static IEnumerable<UnderstandDelta> Route(SentinelChannelRouter? router, string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : router is null ? [new UnderstandDelta(DeltaChannel.Translation, text)] : router.Push(text);
}
