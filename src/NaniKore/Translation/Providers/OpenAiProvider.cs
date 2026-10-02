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
        var router = request.Kind == UnderstandKind.Translate
            ? new SectionChannelRouter()
            : null;
        var sanitizer = new ThinkingStreamSanitizer();
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

            var (text, thinking) = isResponses
                ? ExtractResponsesDelta(frame)
                : ExtractCompletionsDelta(frame.Data);

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

        if (usage.HasValue)
        {
            var endChannel = request.Kind == UnderstandKind.Explain ? DeltaChannel.Explanation : DeltaChannel.Translation;
            yield return new UnderstandDelta(endChannel, string.Empty, usage.Value);
        }
    }

    internal HttpRequestMessage CreateCompletionsRequest(UnderstandRequest request)
    {
        var system = PromptTemplates.GetSystemPrompt(request, Prompts);
        object[] messages;
        if (request.Session is { Messages.Count: > 1 } session)
        {
            var list = new List<object>
            {
                new { role = "system", content = system }
            };
            foreach (var m in session.Messages)
            {
                var role = m.Role switch
                {
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    _ => "user"
                };
                list.Add(new { role, content = FormatOpenAiContent(m) });
            }
            messages = list.ToArray();
        }
        else
        {
            object userContent = request.ImagePng is { Length: > 0 } image
                ? new object[]
                {
                    new { type = "text", text = request.SourceText ?? "" },
                    new { type = "image_url", image_url = new { url = $"data:image/png;base64,{Base64(image)}" } },
                }
                : request.SourceText ?? string.Empty;

            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent },
            };
        }

        var isOfficialOpenAi = Profile.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
        var isReasoningModel = Profile.Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                               Profile.Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase);

        var bodyDict = new Dictionary<string, object?>
        {
            ["model"] = Profile.Model,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["messages"] = messages,
        };

        if (isOfficialOpenAi)
        {
            if (Profile.EnableThinking && isReasoningModel)
            {
                bodyDict["reasoning_effort"] = "low";
            }
        }
        else
        {
            bodyDict["enable_thinking"] = Profile.EnableThinking;
            bodyDict["thinking"] = new { type = Profile.EnableThinking ? "enabled" : "disabled" };
            if (Profile.EnableThinking && isReasoningModel)
            {
                bodyDict["reasoning_effort"] = "low";
            }
        }

        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "chat/completions"), bodyDict);
        SetBearerToken(message, ApiKey);
        return message;
    }

    private static object FormatOpenAiContent(ChatMessage message)
    {
        if (message.ImagePng is { Length: > 0 } image)
        {
            return new object[]
            {
                new { type = "text", text = message.Text ?? string.Empty },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{Base64(image)}" } },
            };
        }

        return message.Text ?? string.Empty;
    }

    internal HttpRequestMessage CreateResponsesRequest(UnderstandRequest request)
    {
        var system = PromptTemplates.GetSystemPrompt(request, Prompts);
        var inputList = new List<object>
        {
            new { role = "system", content = new object[] { new { type = "input_text", text = system } } }
        };

        if (request.Session is { Messages.Count: > 1 } session)
        {
            foreach (var m in session.Messages)
            {
                if (m.Role == ChatRole.Assistant)
                {
                    inputList.Add(new { role = "assistant", content = new object[] { new { type = "output_text", text = m.Text } } });
                }
                else
                {
                    var content = new List<object> { new { type = "input_text", text = m.Text ?? string.Empty } };
                    if (m.ImagePng is { Length: > 0 } image)
                    {
                        content.Add(new { type = "input_image", image_url = $"data:image/png;base64,{Base64(image)}" });
                    }
                    inputList.Add(new { role = "user", content });
                }
            }
        }
        else
        {
            var content = new List<object> { new { type = "input_text", text = request.SourceText ?? string.Empty } };
            if (request.ImagePng is { Length: > 0 } image)
            {
                content.Add(new { type = "input_image", image_url = $"data:image/png;base64,{Base64(image)}" });
            }
            inputList.Add(new { role = "user", content });
        }

        var isReasoningModel = Profile.Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                               Profile.Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase);

        var bodyDict = new Dictionary<string, object?>
        {
            ["model"] = Profile.Model,
            ["stream"] = true,
            ["input"] = inputList.ToArray(),
        };

        if (Profile.EnableThinking && isReasoningModel)
        {
            bodyDict["reasoning"] = new { effort = "low" };
        }

        var message = CreateJsonRequest(HttpMethod.Post, BuildUri(Profile.BaseUrl, "responses"), bodyDict);
        SetBearerToken(message, ApiKey);
        return message;
    }

    internal static (string Text, string Thinking) ExtractCompletionsDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var thinking = GetTextContent(root, "choices", "0", "delta", "reasoning_content");
            if (string.IsNullOrEmpty(thinking))
            {
                thinking = GetTextContent(root, "choices", "0", "delta", "reasoning");
            }

            var delta = GetTextContent(root, "choices", "0", "delta", "content");
            if (delta.Length > 0)
            {
                return (delta, thinking);
            }

            delta = GetTextContent(root, "choices", "0", "delta");
            var text = delta.Length > 0
                ? delta
                : GetTextContent(root, "choices", "0", "message", "content");
            return (text, thinking);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    internal static (string Text, string Thinking) ExtractResponsesDelta(SseEvent frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var eventType = GetText(root, "type");

            // 识别 OpenAI Responses API 的思考链增量事件
            if (string.Equals(frame.Event, "response.reasoning_text.delta", StringComparison.Ordinal) ||
                string.Equals(eventType, "response.reasoning_text.delta", StringComparison.Ordinal))
            {
                var reasoning = GetTextContent(root, "delta");
                return (string.Empty, reasoning);
            }

            if (!string.Equals(frame.Event, "response.output_text.delta", StringComparison.Ordinal) &&
                !string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal))
            {
                if (frame.Event is null && string.IsNullOrEmpty(eventType))
                {
                    var outputText = GetTextContent(root, "output_text");
                    var text = outputText.Length > 0 ? outputText : GetTextContent(root, "output");
                    return (text, string.Empty);
                }

                return (string.Empty, string.Empty);
            }

            return (GetTextContent(root, "delta"), string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
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
