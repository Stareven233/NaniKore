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
        var router = request.ImagePng is { Length: > 0 } ? new SectionChannelRouter() : null;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            var text = ExtractDelta(frame.Data);
            foreach (var delta in Route(router, text))
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

    private static IEnumerable<UnderstandDelta> Route(SectionChannelRouter? router, string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : router is null ? [new UnderstandDelta(DeltaChannel.Translation, text)] : router.Push(text);
}
