using System.Text.Json.Serialization;
using NaniKore.Ocr;

namespace NaniKore.Config;

/// <summary>
/// AOT 友好的全局 JSON 序列化源生成上下文：
/// 涵盖 SecretStore 密钥映射、OcrHttpServer 交互响应、CLI DTO 与 PromptDump 序列化，杜绝任何运行期反射。
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(OcrResponseDto))]
[JsonSerializable(typeof(OcrLineDto))]
[JsonSerializable(typeof(OcrLineBoundsDto))]
[JsonSerializable(typeof(OcrStatusDto))]
[JsonSerializable(typeof(OcrErrorDto))]
[JsonSerializable(typeof(PromptDumpReportDto))]
[JsonSerializable(typeof(PromptDumpDetectionDto))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(PromptDumpSectionDto))]
[JsonSerializable(typeof(PromptDumpSectionDto[]))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal partial class NaniKoreJsonSerializerContext : JsonSerializerContext
{
}

internal sealed record PromptDumpReportDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("source_preview")] string SourcePreview,
    [property: JsonPropertyName("detection")] PromptDumpDetectionDto Detection,
    [property: JsonPropertyName("sections")] PromptDumpSectionDto[] Sections,
    [property: JsonPropertyName("system_prompt")] string SystemPrompt,
    [property: JsonPropertyName("estimated_tokens")] int EstimatedTokens);

internal sealed record PromptDumpDetectionDto(
    [property: JsonPropertyName("features")] string[] Features,
    [property: JsonPropertyName("needs_japanese_ruby")] bool NeedsJapaneseRuby,
    [property: JsonPropertyName("needs_english_ipa")] bool NeedsEnglishIpa,
    [property: JsonPropertyName("is_pure_hanzi_japanese_candidate")] bool IsPureHanziJapaneseCandidate);

internal sealed record PromptDumpSectionDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("text")] string Text);
