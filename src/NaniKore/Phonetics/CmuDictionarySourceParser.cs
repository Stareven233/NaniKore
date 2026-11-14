namespace NaniKore.Phonetics;

/// <summary>
/// CMUdict 源行的结构化表示。编号变体先保留在验证层，避免在进入数据库前静默丢失。
/// </summary>
internal readonly record struct CmuDictionaryEntry(
    string Word,
    int Variant,
    string Arpabet)
{
    /// <summary>CMUdict 未带编号的第一条读音作为现有默认读音。</summary>
    public bool IsDefault => Variant == 1;
}

/// <summary>
/// 解析 CMUdict 文本行的最小纯函数，供 P0 fixture 和后续正式生成器共用。
/// </summary>
internal static class CmuDictionarySourceParser
{
    /// <summary>
    /// 读取 WORD 或 WORD(n) 形式；注释、空行和无效行忽略。
    /// 变体编号不是语义标签，只作为候选的稳定来源顺序保留。
    /// </summary>
    public static IEnumerable<CmuDictionaryEntry> Parse(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";;;", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var rawWord = line[..separator];
            var variant = 1;
            var open = rawWord.LastIndexOf('(');
            if (open > 0 && rawWord.EndsWith(')'))
            {
                if (!int.TryParse(rawWord.AsSpan(open + 1, rawWord.Length - open - 2), out variant) || variant < 2)
                {
                    continue;
                }

                rawWord = rawWord[..open];
            }

            if (rawWord.Length < 2 || rawWord == "#")
            {
                continue;
            }

            var arpabet = line[(separator + 1)..].Trim();
            if (arpabet.Length == 0)
            {
                continue;
            }

            yield return new CmuDictionaryEntry(rawWord.ToLowerInvariant(), variant, arpabet);
        }
    }
}
