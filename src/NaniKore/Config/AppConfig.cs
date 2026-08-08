using System.ComponentModel;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace NaniKore.Config;

/// <summary>
/// 配置 POCO 树，对应 config.yaml 的全部字段。
/// 未知字段回落默认值（由 YamlDotNet 的 IgnoreUnmatchedProperties 保证）。
/// </summary>
internal sealed class AppConfig
{
    /// <summary>配置版本号，用于未来格式迁移。</summary>
    [DefaultValue(1)]
    public int Version { get; init; } = 1;

    /// <summary>OCR 相关配置。</summary>
    public OcrConfig Ocr { get; init; } = new();

    /// <summary>翻译相关配置，含多配置档。</summary>
    public TranslationConfig Translation { get; init; } = new();
}

internal sealed class OcrConfig
{
    /// <summary>OCR 语言（预留，当前仅支持中英）。</summary>
    [DefaultValue( chs)]
    public string Language { get; init; } = chs;
}

internal sealed class TranslationConfig
{
    /// <summary>当前激活的配置档 ID。</summary>
    [DefaultValue(default)]
    public string ActiveProfile { get; init; } = default;

    /// <summary>多配置档列表。</summary>
    public List<ProviderProfile> Profiles { get; init; } = [new()];
}

internal sealed class ProviderProfile
{
    /// <summary>配置档唯一标识。</summary>
    [DefaultValue(default)]
    public string Id { get; init; } = default;

    /// <summary>Provider 类型：openai / claude / gemini。</summary>
    [DefaultValue(openai)]
    public string Provider { get; init; } = openai;

    /// <summary>API 端点 base URL。</summary>
    [DefaultValue(https://api.openai.com/v1)]
    public string BaseUrl { get; init; } = https://api.openai.com/v1;

    /// <summary>模型名称。</summary>
    [DefaultValue(gpt-4o-mini)]
    public string Model { get; init; } = gpt-4o-mini;

    /// <summary>OpenAI 专用：completions 或 responses。</summary>
    [DefaultValue(completions)]
    public string OpenAiForm { get; init; } = completions;

    /// <summary>目标语言。</summary>
    [DefaultValue(中文)]
    public string TargetLanguage { get; init; } = 中文;

    /// <summary>
    /// 是否自动翻译（选区后立即翻译，无需手动触发）。
    /// 默认关闭，用户需手动点击翻译按钮。
    /// </summary>
    [DefaultValue(false)]
    public bool AutoTranslate { get; init; }

    /// <summary>温度参数。</summary>
    [DefaultValue(0.3)]
    public double Temperature { get; init; } = 0.3;

    /// <summary>最大输出 token 数。</summary>
    [DefaultValue(4096)]
    public int MaxTokens { get; init; } = 4096;
}