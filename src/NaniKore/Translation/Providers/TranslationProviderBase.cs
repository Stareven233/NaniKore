using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NaniKore.Config;

namespace NaniKore.Translation.Providers;

/// <summary>Provider 的公共网络与 SSE 处理骨架；子类只负责各厂商请求体与增量字段。</summary>
internal abstract class TranslationProviderBase : ITranslationProvider
{
    private readonly HttpClient _client;
    protected readonly ProviderProfile Profile;
    protected readonly string ApiKey;
    protected readonly PromptConfig Prompts;

    protected TranslationProviderBase(ProviderProfile profile, string apiKey, PromptConfig prompts, HttpMessageHandler? transport = null)
    {
        Profile = profile.Clone();
        ApiKey = apiKey;
        Prompts = prompts;
        // transport 只供自测注入假网络栈复现服务端行为；生产恒为默认 handler。
        _client = new HttpClient(transport ?? new HttpClientHandler())
        {
            Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds),
        };
    }

    public string Name => Profile.Name;

    public ProviderCapabilities Capabilities => ProviderFactory.GetCapabilities(Profile);

    public abstract IAsyncEnumerable<UnderstandDelta> StreamAsync(UnderstandRequest request, CancellationToken cancellationToken);

    public void Dispose() => _client.Dispose();

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 上传结束（或失败）即断开缓冲区引用；仅 Dispose 不会清空 ByteArrayContent 内的数组。
                var content = request.Content;
                request.Content = null;
                content?.Dispose();
            }

            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    throw await ProviderHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }

            return response;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException($"请求超过 {Profile.TimeoutSeconds} 秒超时。", innerException: null, cancellationToken);
        }
    }

    /// <summary>AOT 安全的 JsonArray 构建：显式调用非泛型构造函数，杜绝 C# 集合初始化器解析至带 IL2026/IL3050 的 Add<T> 扩展。</summary>
    protected static System.Text.Json.Nodes.JsonArray NodeArray(params System.Text.Json.Nodes.JsonNode?[] items) => new(items);

    /// <summary>AOT 安全的 JsonArray 追加：显式绑定非泛型 Add(JsonNode?)，杜绝泛型 Add<T>。</summary>
    protected static void AppendNode(System.Text.Json.Nodes.JsonArray array, System.Text.Json.Nodes.JsonNode? item) => array.Add(item);

    /// <summary>构造下游 LLM 请求。载荷基于 JsonNode DOM 直出，零运行期反射，天然免疫 Native AOT 裁剪。</summary>
    protected static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, System.Text.Json.Nodes.JsonNode body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            body.WriteTo(writer);
        }

        // 直接移交有效 UTF-8 区段，避免 UTF-16 JSON 字符串及 ToArray 的整份复制。
        var bytes = buffer.GetBuffer();
        var content = new ByteArrayContent(bytes, 0, checked((int)buffer.Length));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new HttpRequestMessage(method, uri)
        {
            Content = content,
        };
    }

    /// <summary>
    /// 按请求显式选路由模式（issue 008 Phase 3）：视觉链路（携带图片）走两段式，
    /// 按哨兵拆纯转录/译文两通道；文本路径译文直通，无原文通道。
    /// 通道归属由组装路径显式决定，不再靠输出内容猜测。
    /// </summary>
    internal static SectionChannelRouter? CreateRouter(UnderstandRequest request) =>
        request.Kind == UnderstandKind.Translate
            ? new SectionChannelRouter(
                request.ImagePng is { Length: > 0 } ? StreamChannelMode.Segmenter : StreamChannelMode.Passthrough)
            : null;

    /// <summary>
    /// 各厂商共用的增量分派：可选接入 ThinkingStreamSanitizer 进行思考链过滤；
    /// Translate 交给分栏路由器拆原文/译文通道，Explain 直接落到 Explanation 通道。空增量原样短路。
    /// </summary>
    internal static IEnumerable<UnderstandDelta> Route(
        SectionChannelRouter? router,
        ThinkingStreamSanitizer? sanitizer,
        string text,
        UnderstandKind kind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (sanitizer is null)
        {
            return RouteCore(router, text, kind);
        }

        var results = new List<UnderstandDelta>();
        foreach (var (cleanText, thinkingText) in sanitizer.Push(text))
        {
            if (!string.IsNullOrEmpty(thinkingText))
            {
                results.Add(new UnderstandDelta(DeltaChannel.Thinking, thinkingText));
            }

            if (!string.IsNullOrEmpty(cleanText))
            {
                results.AddRange(RouteCore(router, cleanText, kind));
            }
        }

        return results;
    }

    internal static IEnumerable<UnderstandDelta> Route(SectionChannelRouter? router, string text, UnderstandKind kind) =>
        Route(router, null, text, kind);

    private static IEnumerable<UnderstandDelta> RouteCore(SectionChannelRouter? router, string text, UnderstandKind kind)
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

    /// <summary>
    /// 流结束时刷新 Sanitizer 与 Router 中残留的增量。
    /// </summary>
    internal static IEnumerable<UnderstandDelta> CompleteStream(
        SectionChannelRouter? router,
        ThinkingStreamSanitizer? sanitizer,
        UnderstandKind kind)
    {
        if (sanitizer is not null)
        {
            foreach (var (cleanText, thinkingText) in sanitizer.Complete())
            {
                if (!string.IsNullOrEmpty(thinkingText))
                {
                    yield return new UnderstandDelta(DeltaChannel.Thinking, thinkingText);
                }

                if (!string.IsNullOrEmpty(cleanText))
                {
                    foreach (var delta in RouteCore(router, cleanText, kind))
                    {
                        yield return delta;
                    }
                }
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

    protected static Uri BuildUri(string baseUrl, string relative) => new(
        baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/'),
        UriKind.Absolute);

    protected static string Base64(ReadOnlyMemory<byte> image) => Convert.ToBase64String(image.Span);

    protected static string GetText(JsonElement root, params string[] path)
    {
        var current = GetValue(root, path);
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>兼容视觉模型常见的文本字符串、content 数组与 Gemini parts 数组。</summary>
    internal static string GetTextContent(JsonElement root, params string[] path) =>
        ExtractText(GetValue(root, path));

    private static JsonElement GetValue(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
                {
                    return default;
                }

                current = current[index];
            }
            else if (!current.TryGetProperty(segment, out current))
            {
                return default;
            }
        }

        return current;
    }

    private static string ExtractText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(ExtractText)),
            JsonValueKind.Object => ExtractObjectText(value),
            _ => string.Empty,
        };
    }

    private static string ExtractObjectText(JsonElement value)
    {
        foreach (var propertyName in new[] { "text", "content", "parts", "delta", "output_text" })
        {
            if (value.TryGetProperty(propertyName, out var property))
            {
                var text = ExtractText(property);
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    protected static void SetBearerToken(HttpRequestMessage request, string apiKey) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
}

