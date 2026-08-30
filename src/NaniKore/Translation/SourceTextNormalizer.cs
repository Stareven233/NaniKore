using System.Text;

namespace NaniKore.Translation;

/// <summary>整理 OCR 或视觉模型原文，让后续翻译更容易判断词语边界。</summary>
internal static class SourceTextNormalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var collapsed = CollapseWhitespace(text);
        if (collapsed.Count == 0)
        {
            return string.Empty;
        }

        var punctuationNormalized = NormalizePunctuationSpacing(collapsed);
        return AddScriptBoundaries(punctuationNormalized);
    }

    private static List<Rune> CollapseWhitespace(string text)
    {
        var result = new List<Rune>(text.Length);
        var pendingWhitespace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingWhitespace = result.Count > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                result.Add(new Rune(' '));
                pendingWhitespace = false;
            }

            result.Add(rune);
        }

        return result;
    }

    private static List<Rune> NormalizePunctuationSpacing(IReadOnlyList<Rune> text)
    {
        var result = new List<Rune>(text.Count);
        var suppressWhitespace = false;
        for (var index = 0; index < text.Count; index++)
        {
            var rune = text[index];
            if (rune.Value == ' ')
            {
                var next = index + 1 < text.Count ? text[index + 1] : default;
                var previousIsCjkPunctuation = result.Count > 0 && IsCjkClosingPunctuation(result[^1]);
                if (IsCjk(result[^1]) && IsCjk(next))
                {
                    // OCR 常把连续的中日韩文字断成多段；两侧都是 CJK 时空格和换行都不应保留。
                    continue;
                }

                if (!suppressWhitespace && !(previousIsCjkPunctuation && IsCjk(next)))
                {
                    result.Add(rune);
                }

                continue;
            }

            // OCR 换行经常落在标点前；中日韩标点不应被前置空格切断。
            if (IsCjkClosingPunctuation(rune) && result.Count > 0 && result[^1].Value == ' ')
            {
                result.RemoveAt(result.Count - 1);
            }

            result.Add(rune);
            suppressWhitespace = IsCjkOpeningPunctuation(rune);
        }

        if (result.Count > 0 && result[^1].Value == ' ')
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static string AddScriptBoundaries(IReadOnlyList<Rune> text)
    {
        var result = new StringBuilder(text.Count + 8);
        for (var index = 0; index < text.Count; index++)
        {
            var current = text[index];
            if (index > 0)
            {
                var previous = text[index - 1];
                if (IsCjk(previous) && IsAsciiWord(current) ||
                    IsAsciiWord(previous) && IsCjk(current))
                {
                    result.Append(' ');
                }
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static bool IsAsciiWord(Rune rune) =>
        rune.Value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static bool IsCjk(Rune rune) => rune.Value switch
    {
        >= 0x3400 and <= 0x4DBF => true,
        >= 0x4E00 and <= 0x9FFF => true,
        >= 0xF900 and <= 0xFAFF => true,
        >= 0x3040 and <= 0x30FF => true,
        >= 0xAC00 and <= 0xD7AF => true,
        _ => false,
    };

    private static bool IsCjkOpeningPunctuation(Rune rune) => rune.Value is
        '\u3008' or '\u300A' or '\u300C' or '\u300E' or '\u3010' or '\u3014' or
        '\u3016' or '\u3018' or '\u301D' or '\uFF08' or '\uFF3B' or '\u2018' or '\u201C';

    private static bool IsCjkClosingPunctuation(Rune rune) => rune.Value is
        '\u3001' or '\u3002' or '\u3009' or '\u300B' or '\u300D' or '\u300F' or
        '\u3011' or '\u3015' or '\u3017' or '\u3019' or '\u301E' or '\u301F' or
        '\uFF09' or '\uFF3D' or '\uFF0C' or '\uFF01' or '\uFF1F' or '\uFF1A' or
        '\uFF1B' or '\u2019' or '\u201D';
}
