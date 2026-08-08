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
        var router = request.ImagePng is { Length: > 0 } ? new SentinelChannelRouter() : null;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(frame.Event, "content_block_delta", StringComparison.Ordinal))
            {
                continue;
            }

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
        object content = request.ImagePng is { Length: > 0 } image
            ? new object[]
            {
                new { type = "image", source = new { type = "base64", media_type = "image/png", data = Base64(image) } },
                new { type = "text", text = request.SourceText ?? string.Empty },
            }
            : request.SourceText ?? string.Empty;
        var body = new
        {
            model = Profile.Model,
            stream = true,
            max_tokens = 4096,
            system = PromptTemplates.GetSystemPrompt(request, Prompts),
            messages = new[] { new { role = "user", content } },
        };
        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "v1/messages"), body);
        message.Headers.Add("x-api-key", ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
    }

    private static string ExtractDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return GetText(document.RootElement, "delta", "text");
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
