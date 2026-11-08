namespace NaniKore.Translation;

/// <summary>一次云端理解任务的类别；精讲通道在本阶段只保留契约。</summary>
internal enum UnderstandKind
{
    Translate,
    Explain,
}

/// <summary>请求可独立携带 OCR 文本、选区 PNG，或同时携带两者；可携带 TranslationSession 实现两阶段上下文复用。</summary>
internal sealed record UnderstandRequest(
    UnderstandKind Kind,
    string? SourceText,
    ReadOnlyMemory<byte>? ImagePng,
    string TargetLanguage,
    TranslationSession? Session = null);

internal enum DeltaChannel
{
    SourceText,
    Translation,
    Explanation,
    Thinking,
}

/// <summary>一次大模型调用所消耗的实际或估算 Token 计数。</summary>
internal readonly record struct TokenUsage(
    int InputTokens,
    int OutputTokens,
    bool IsEstimated = false)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>流式增量；可携带文本增量或在流结束时携带 Token 开销信息。</summary>
internal readonly record struct UnderstandDelta(
    DeltaChannel Channel,
    string Text,
    TokenUsage? Usage = null);

/// <summary>本次调用的综合性能与开销度量指标。</summary>
internal readonly record struct TranslationMetrics(
    int InputTokens,
    int OutputTokens,
    long TimeToFirstTokenMs,
    long TotalDurationMs,
    double TokensPerSecond,
    bool IsEstimated = false)
{
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>格式化展示在结果卡底部的单行指标文案。</summary>
    public string FormatSummary()
    {
        var inputPrefix = IsEstimated ? "~" : string.Empty;
        var outputPrefix = IsEstimated ? "~" : string.Empty;
        var speedText = TokensPerSecond > 0 ? $"{TokensPerSecond:F0}" : "0";
        return $"Input: {inputPrefix}{InputTokens} tokens · Output: {outputPrefix}{OutputTokens} tokens · TTFT: {TimeToFirstTokenMs}ms · TPS: {speedText} t/s";
    }
}


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
