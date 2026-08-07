namespace NaniKore.Ocr;

internal sealed record OcrLine(Rectangle Bounds, string Text, float Confidence);

internal sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TimeSpan Elapsed)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}