using NaniKore.Config;
using NaniKore.Translation.Providers;

namespace NaniKore.Translation;

/// <summary>以配置档为粒度缓存 HttpClient/provider，配置热应用时整体替换此工厂即可释放旧连接。</summary>
internal sealed class ProviderFactory : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, ITranslationProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ProviderFactory(AppConfig config)
    {
        _config = config;
    }

    public ITranslationProvider GetProvider(string profileId, UnderstandRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profile = _config.Profiles.FirstOrDefault(item =>
            string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProviderConfigurationException($"找不到配置档“{profileId}”。");

        var capabilities = GetCapabilities(profile);
        ValidateCapabilities(request, capabilities);
        if (_providers.TryGetValue(profile.Id, out var existing))
        {
            return existing;
        }

        var apiKey = SecretStore.Load(profile.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderConfigurationException($"配置档“{profile.Id}”尚未设置可用的 API Key，请先打开设置保存密钥。");
        }

        var provider = CreateProvider(profile, apiKey, _config.Prompts);
        _providers.Add(profile.Id, provider);
        return provider;
    }

    public static async Task TestConnectionAsync(ProviderProfile profile, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderConfigurationException("请先输入 API Key 再测试连接。");
        }

        using var provider = CreateProvider(profile, apiKey, new PromptConfig());
        var request = new UnderstandRequest(UnderstandKind.Translate, "connection test", null, "English");
        await using var enumerator = provider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("服务已连接，但没有返回任何文本。");
        }
    }

    public static ProviderCapabilities GetCapabilities(ProviderProfile profile) =>
        ProviderCapabilities.Text | ProviderCapabilities.Streaming |
        (profile.SupportsVision ? ProviderCapabilities.Vision : ProviderCapabilities.None);

    public static void ValidateCapabilities(UnderstandRequest request, ProviderCapabilities capabilities)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceText) && !capabilities.HasFlag(ProviderCapabilities.Text))
        {
            throw new ProviderCapabilityException("该配置档未声明文本输入能力。");
        }

        if (request.ImagePng is { Length: > 0 } && !capabilities.HasFlag(ProviderCapabilities.Vision))
        {
            throw new ProviderCapabilityException("该配置档未声明图像输入能力，请改用 local_ocr 模式或在设置里勾选此模型支持图像。");
        }

        if (string.IsNullOrWhiteSpace(request.SourceText) && request.ImagePng is null)
        {
            throw new ProviderCapabilityException("翻译请求必须包含文本或图像。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }

        _providers.Clear();
        _disposed = true;
    }

    private static ITranslationProvider CreateProvider(ProviderProfile profile, string apiKey, PromptConfig prompts) =>
        profile.Kind.ToLowerInvariant() switch
        {
            "openai" => new OpenAiProvider(profile, apiKey, prompts),
            "claude" => new ClaudeProvider(profile, apiKey, prompts),
            "gemini" => new GeminiProvider(profile, apiKey, prompts),
            _ => throw new ProviderConfigurationException($"不支持的 provider 类型“{profile.Kind}”。"),
        };
}
