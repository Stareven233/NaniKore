namespace NaniKore.Translation;

/// <summary>一次云端理解任务的类别；精讲通道在本阶段只保留契约。</summary>
internal enum UnderstandKind
{
    Translate,
    Explain,
}

/// <summary>请求可独立携带 OCR 文本、选区 PNG，或同时携带两者。</summary>
internal sealed record UnderstandRequest(
    UnderstandKind Kind,
    string? SourceText,
    ReadOnlyMemory<byte>? ImagePng,
    string TargetLanguage);

internal enum DeltaChannel
{
    SourceText,
    Translation,
    Explanation,
}

internal readonly record struct UnderstandDelta(DeltaChannel Channel, string Text);

[Flags]
internal enum ProviderCapabilities
{
    None = 0,
    Text = 1,
    Vision = 2,
    Streaming = 4,
}

internal sealed class ProviderCapabilityException : InvalidOperationException
{
    public ProviderCapabilityException(string message) : base(message) { }
}

internal sealed class ProviderConfigurationException : InvalidOperationException
{
    public ProviderConfigurationException(string message) : base(message) { }
}
