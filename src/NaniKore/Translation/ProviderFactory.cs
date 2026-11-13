using NaniKore.Config;
using NaniKore.Translation.Providers;

namespace NaniKore.Translation;

/// <summary>以配置档为粒度缓存 HttpClient/provider，配置热应用时整体替换此工厂即可释放旧连接。</summary>
internal sealed class ProviderFactory : IDisposable
{
    private readonly AppConfig _config;
    private readonly SecretStore _secrets;
    private readonly Dictionary<string, ITranslationProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    // 进行中的流计数：配置热应用会在翻译途中整体替换工厂，若直接销毁旧 provider 的 HttpClient，
    // 会中断仍在等待响应头的请求（R08 已用延迟响应端点复现）。因此在计数归零前延迟释放底层连接。
    private readonly object _lifetimeLock = new();
    private int _activeStreams;
    private bool _disposePending;

    /// <param name="secrets">密钥来源；缺省为数据目录下的 keys.bin，自测可注入临时目录。</param>
    public ProviderFactory(AppConfig config, SecretStore? secrets = null)
    {
        _config = config;
        _secrets = secrets ?? new SecretStore();
    }

    public ITranslationProvider GetProvider(string profileName, UnderstandRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 档名是配置档唯一身份（issue 017）：查找、缓存与密钥索引全部用它。
        var profile = _config.Profiles.FirstOrDefault(item =>
            string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProviderConfigurationException($"找不到配置档“{profileName}”。");

        var capabilities = GetCapabilities(profile);
        ValidateCapabilities(request, capabilities);
        if (!_providers.TryGetValue(profile.Name, out var provider))
        {
            var apiKey = _secrets.Load(profile.Name);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ProviderConfigurationException($"配置档“{profile.Name}”尚未设置可用的 API Key，请先打开设置保存密钥。");
            }

            provider = CreateProvider(profile, apiKey, _config.Prompts);
            _providers.Add(profile.Name, provider);
        }

        // 返回把底层 provider 包在流租约里的包装：工厂被替换时，只要还有流未结束就不销毁底层 HttpClient，
        // 让进行中的请求（含等待响应头阶段）跑完，新请求则自然改用新工厂（R08）。
        return new LeasedProvider(this, provider);
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
        bool disposeNow;
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // 仍有流在跑就延迟释放，避免配置热应用销毁旧 HttpClient 中断进行中的请求（R08）。
            disposeNow = _activeStreams == 0;
            if (!disposeNow)
            {
                _disposePending = true;
            }
        }

        if (disposeNow)
        {
            DisposeProviders();
        }
    }

    /// <summary>一次流式翻译开始：租约 +1，旧工厂在此期间必须保持存活。</summary>
    private void OnStreamStarted()
    {
        lock (_lifetimeLock)
        {
            _activeStreams++;
        }
    }

    /// <summary>一次流式翻译结束：租约 -1；若期间已请求释放且这是最后一个流，则真正释放底层 provider。</summary>
    private void OnStreamCompleted()
    {
        bool disposeNow;
        lock (_lifetimeLock)
        {
            _activeStreams--;
            disposeNow = _activeStreams == 0 && _disposePending;
        }

        if (disposeNow)
        {
            DisposeProviders();
        }
    }

    private void DisposeProviders()
    {
        if (_providers.Count == 0)
        {
            return;
        }

        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }

    /// <summary>
    /// 把底层 provider 包进流租约：每次 StreamAsync 的完整枚举期间租约 +1、结束 -1。
    /// 包装本身不拥有底层 provider（生命周期由工厂统一管理），因此 Dispose 为空实现。
    /// </summary>
    private sealed class LeasedProvider(ProviderFactory factory, ITranslationProvider inner) : ITranslationProvider
    {
        public string Name => inner.Name;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public IAsyncEnumerable<UnderstandDelta> StreamAsync(UnderstandRequest request, CancellationToken cancellationToken) =>
            StreamCore(request, cancellationToken);

        private async IAsyncEnumerable<UnderstandDelta> StreamCore(
            UnderstandRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            factory.OnStreamStarted();
            try
            {
                await foreach (var delta in inner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    yield return delta;
                }
            }
            finally
            {
                factory.OnStreamCompleted();
            }
        }

        public void Dispose()
        {
        }
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
