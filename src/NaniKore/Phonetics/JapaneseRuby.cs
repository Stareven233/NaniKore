using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 日语书面振假名的区间切分（宿主侧，与具体 provider 无关）：
/// 原生包按 UniDic 词元给出整词书面假名（片假名），界面要的却是「底字止于汉字、不含送假名」的振假名，
/// 所以在这里把词首词尾的送假名回到底字之外，只把汉字段的读音挂到区间上。
/// 内部送假名（買い出し）留在底字里：读音本身就覆盖它，切不出更细的边界时不硬拆。
/// 数字、纯假名、记号、读音为空的词元一律不注，绝不拿音便表记（トーキョー）反推书面拼写。
/// </summary>
internal static class JapaneseRuby
{
    /// <summary>
    /// 把「词面 + 整词假名」切成「底字区间 + 底字读音」。
    /// 返回 false 表示这一段不值得注音（底字为空、无汉字、读音与词面对不上），调用方直接跳过。
    /// <paramref name="baseOffset"/> 是底字在词面里的相对偏移（原文偏移 = 词元起点 + 它）。
    /// </summary>
    public static bool TrySplit(string surface, string? reading, out int baseOffset, out int baseLength, out string baseReading)
    {
        baseOffset = 0;
        baseLength = 0;
        baseReading = string.Empty;
        if (string.IsNullOrEmpty(surface) || string.IsNullOrWhiteSpace(reading))
        {
            return false;
        }

        var hiragana = ToHiragana(reading);
        var readingStart = 0;
        var readingEnd = hiragana.Length;

        // 1. 词首送假名（お腹 → 腹）：只有读音确实以同一串假名开头才剥，对不上就当没这层，避免切错底字。
        var prefixKana = 0;
        while (prefixKana < surface.Length && RubyTextParser.IsKana(surface[prefixKana]))
        {
            prefixKana++;
        }

        if (prefixKana > 0)
        {
            var prefix = ToHiragana(surface[..prefixKana]);
            if (hiragana.StartsWith(prefix, StringComparison.Ordinal))
            {
                readingStart = prefix.Length;
            }
            else
            {
                prefixKana = 0;
            }
        }

        // 2. 词尾送假名（食べ → 食、凍り付く → 凍り付）：同样要求读音以同一串假名收尾。
        var remaining = surface[prefixKana..];
        var suffixKana = 0;
        while (suffixKana < remaining.Length && RubyTextParser.IsKana(remaining[^(suffixKana + 1)]))
        {
            suffixKana++;
        }

        if (suffixKana > 0)
        {
            var suffix = ToHiragana(remaining[^suffixKana..]);
            if (hiragana.AsSpan(readingStart, readingEnd - readingStart).EndsWith(suffix, StringComparison.Ordinal))
            {
                readingEnd -= suffix.Length;
            }
            else
            {
                suffixKana = 0;
            }
        }

        baseLength = remaining.Length - suffixKana;
        if (baseLength <= 0 || readingEnd <= readingStart)
        {
            return false;
        }

        // 3. 底字只能是汉字+假名（内部送假名），且必须至少含一个汉字：
        //    标记、数字、拉丁字母混在词面里一律整段放弃，宁可少注也不把符号当底字。
        var baseSpan = remaining.AsSpan(0, baseLength);
        var hasKanji = false;
        foreach (var ch in baseSpan)
        {
            if (RubyTextParser.IsCjkKanji(ch))
            {
                hasKanji = true;
            }
            else if (!RubyTextParser.IsKana(ch))
            {
                return false;
            }
        }

        if (!hasKanji)
        {
            return false;
        }

        baseOffset = prefixKana;
        baseReading = hiragana[readingStart..readingEnd];
        return true;
    }

    /// <summary>片假名转平假名（0x30A1-0x30F6 → 0x3041-0x3096）；长音符、中点等非假名原样保留。</summary>
    public static string ToHiragana(string? katakana)
    {
        if (string.IsNullOrEmpty(katakana))
        {
            return string.Empty;
        }

        var chars = katakana.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '\u30A1' and <= '\u30F6')
            {
                chars[i] = (char)(chars[i] - 0x60);
            }
        }

        return new string(chars);
    }
}
