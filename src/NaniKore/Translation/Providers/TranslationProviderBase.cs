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
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException($"请求超过 {Profile.TimeoutSeconds} 秒超时。", innerException: null, cancellationToken);
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
        var current = root;
        foreach (var segment in path)
        {
            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
                {
                    return string.Empty;
                }

                current = current[index];
            }
            else if (!current.TryGetProperty(segment, out current))
            {
                return string.Empty;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }

    protected static void SetBearerToken(HttpRequestMessage request, string apiKey) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
}

