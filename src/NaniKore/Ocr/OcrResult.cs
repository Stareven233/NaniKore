using System.Text.Json.Serialization;
using SkiaSharp;

namespace NaniKore.Ocr;

/// <summary>OCR 识别单行文本框体与置信度。</summary>
internal sealed record OcrLine(SKRectI Bounds, string Text, float Confidence);

/// <summary>整图 OCR 识别结果集。</summary>
internal sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TimeSpan Elapsed)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));

    public OcrResponseDto ToDto() => new(
        Text,
        Math.Round(Elapsed.TotalMilliseconds, 1),
        Lines.Select(l => new OcrLineDto(
            l.Text,
            MathF.Round(l.Confidence, 4),
            new OcrLineBoundsDto(l.Bounds.Left, l.Bounds.Top, l.Bounds.Width, l.Bounds.Height)
        )).ToArray());
}

internal sealed record OcrLineBoundsDto(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

internal sealed record OcrLineDto(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("bounds")] OcrLineBoundsDto Bounds);

internal sealed record OcrResponseDto(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("lines")] IReadOnlyList<OcrLineDto> Lines);

internal sealed record OcrStatusDto(
    [property: JsonPropertyName("status")] string Status);

internal sealed record OcrErrorDto(
    [property: JsonPropertyName("error")] string Error);
