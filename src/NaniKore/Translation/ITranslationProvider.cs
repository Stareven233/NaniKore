namespace NaniKore.Translation;

internal interface ITranslationProvider : IDisposable
{
    /// <summary>提供者所属配置档的档名（配置档唯一身份）。</summary>
    string Name { get; }

    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        CancellationToken cancellationToken);
}
