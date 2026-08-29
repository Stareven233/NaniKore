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
        var requestId = TranslationDiagnostics.GetRequestId(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = request.ImagePng is { Length: > 0 } ? new SentinelChannelRouter() : null;
        var frameCount = 0;
        var extractedTextLength = 0;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            frameCount++;
            var text = ExtractDelta(frame);
            TranslationDiagnostics.ResponseFrame(
                Profile,
                requestId,
                "claude",
                frame.Event,
                frame.Data,
                text.Length);
            extractedTextLength += text.Length;
            foreach (var delta in Route(router, text))
            {
                yield return delta;
            }
        }

        TranslationDiagnostics.ResponseStreamCompleted(Profile, requestId, "claude", frameCount, extractedTextLength);

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

    private static string ExtractDelta(SseEvent frame)
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
                return GetTextContent(root, "delta", "text");
            }

            // 兼容视觉端点忽略 stream=true 后返回的 Anthropic message JSON。
            if (frame.Event is null && (string.IsNullOrEmpty(type) || string.Equals(type, "message", StringComparison.Ordinal)))
            {
                return GetTextContent(root, "content");
            }

            return string.Empty;
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
