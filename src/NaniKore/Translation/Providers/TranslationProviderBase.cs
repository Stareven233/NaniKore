using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

    protected TranslationProviderBase(ProviderProfile profile, string apiKey, PromptConfig prompts)
    {
        Profile = profile.Clone();
        ApiKey = apiKey;
        Prompts = prompts;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds),
        };
    }

    public string Id => Profile.Id;

    public ProviderCapabilities Capabilities => ProviderFactory.GetCapabilities(Profile);

    public abstract IAsyncEnumerable<UnderstandDelta> StreamAsync(UnderstandRequest request, CancellationToken cancellationToken);

    public void Dispose() => _client.Dispose();

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestId = TranslationDiagnostics.RequestStarted(Profile, request);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            TranslationDiagnostics.ResponseReceived(Profile, requestId, response);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var exception = new TaskCanceledException($"请求超过 {Profile.TimeoutSeconds} 秒超时。", innerException: null, cancellationToken);
            TranslationDiagnostics.RequestFailed(Profile, requestId, exception);
            throw exception;
        }
        catch (Exception exception)
        {
            TranslationDiagnostics.RequestFailed(Profile, requestId, exception);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var exception = await ProviderHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
                TranslationDiagnostics.ResponseRejected(Profile, requestId, response, exception);
                throw exception;
            }
        }

        return response;
    }

    protected static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object body)
    {
        return new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
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

