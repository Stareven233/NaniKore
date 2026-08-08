namespace NaniKore.Translation;

internal interface ITranslationProvider : IDisposable
{
    string Id { get; }

    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        CancellationToken cancellationToken);
}
