using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using NaniKore.Config;

namespace NaniKore.Translation.Providers;

internal sealed class GeminiProvider : TranslationProviderBase
{
    public GeminiProvider(ProviderProfile profile, string apiKey, PromptConfig prompts, HttpMessageHandler? transport = null)
        : base(profile, apiKey, prompts, transport) { }

    public override async IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProviderFactory.ValidateCapabilities(request, Capabilities);
        using var message = CreateRequest(request);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var router = CreateRouter(request);
        var sanitizer = new ThinkingStreamSanitizer();
        var inputTokens = 0;
        var outputTokens = 0;

        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            ExtractGeminiTokens(frame.Data, ref inputTokens, ref outputTokens);
            var (text, thinking) = ExtractDelta(frame.Data);

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
        var contents = new JsonArray();
        if (request.Session is { Messages.Count: > 1 } session)
        {
            foreach (var m in session.Messages)
            {
                var role = m.Role == ChatRole.Assistant ? "model" : "user";
                var parts = NodeArray(new JsonObject { ["text"] = m.Text ?? string.Empty });
                if (m.ImagePng is { Length: > 0 } image)
                {
                    AppendNode(parts, new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = "image/png",
                            ["data"] = Base64(image)
                        }
                    });
                }
                AppendNode(contents, new JsonObject { ["role"] = role, ["parts"] = parts });
            }
        }
        else
        {
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(request.SourceText))
            {
                AppendNode(parts, new JsonObject { ["text"] = request.SourceText });
            }
            if (request.ImagePng is { Length: > 0 } image)
            {
                AppendNode(parts, new JsonObject
                {
                    ["inline_data"] = new JsonObject
                    {
                        ["mime_type"] = "image/png",
                        ["data"] = Base64(image)
                    }
                });
            }
            AppendNode(contents, new JsonObject { ["role"] = "user", ["parts"] = parts });
        }

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = NodeArray(new JsonObject { ["text"] = system })
            },
            ["contents"] = contents,
        };

        if (Profile.EnableThinking)
        {
            body["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject
                {
                    ["thinkingBudget"] = -1
                }
            };
        }
        else if (Profile.Model.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
                 Profile.Model.Contains("2.5", StringComparison.OrdinalIgnoreCase) ||
                 Profile.Model.Contains("2.0", StringComparison.OrdinalIgnoreCase))
        {
            body["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject
                {
                    ["thinkingBudget"] = 0
                }
            };
        }

        var endpoint = $"models/{Uri.EscapeDataString(Profile.Model)}:streamGenerateContent?alt=sse";
        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, endpoint), body);
        message.Headers.Add("x-goog-api-key", ApiKey);
        return message;
    }

    internal static (string Text, string Thinking) ExtractDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            {
                return (string.Empty, string.Empty);
            }

            var textBuilder = new System.Text.StringBuilder();
            var thinkingBuilder = new System.Text.StringBuilder();

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        var isThought = part.TryGetProperty("thought", out var thoughtElem) &&
                                        thoughtElem.ValueKind == JsonValueKind.True;

                        if (part.TryGetProperty("text", out var textElem) && textElem.ValueKind == JsonValueKind.String)
                        {
                            var str = textElem.GetString() ?? string.Empty;
                            if (isThought)
                            {
                                thinkingBuilder.Append(str);
                            }
                            else
                            {
                                textBuilder.Append(str);
                            }
                        }
                    }
                }
            }

            return (textBuilder.ToString(), thinkingBuilder.ToString());
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
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
