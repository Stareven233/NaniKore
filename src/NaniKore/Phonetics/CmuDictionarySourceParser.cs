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

/// <summary>已转换为 IPA 的英语候选；Variant 保留 CMUdict 来源顺序，默认项仍是编号 1。</summary>
internal readonly record struct EnglishPronunciationCandidate(
    string Word,
    int Variant,
    string Ipa)
{
    public bool IsDefault => Variant == 1;
}

/// <summary>
/// P0 候选查询的内存契约：候选完整返回，默认查询只取编号 1；暂不绑定正式 SQLite schema。
/// </summary>
internal sealed class EnglishPronunciationIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EnglishPronunciationCandidate>> _entries;

    private EnglishPronunciationIndex(IReadOnlyDictionary<string, IReadOnlyList<EnglishPronunciationCandidate>> entries) =>
        _entries = entries;

    public static EnglishPronunciationIndex FromCmuEntries(
        IEnumerable<CmuDictionaryEntry> entries,
        Func<string, string> convert)
    {
        var grouped = new Dictionary<string, List<EnglishPronunciationCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var ipa = convert(entry.Arpabet);
            if (ipa.Length == 0)
            {
                continue;
            }

            if (!grouped.TryGetValue(entry.Word, out var candidates))
            {
                candidates = [];
                grouped.Add(entry.Word, candidates);
            }

            candidates.Add(new EnglishPronunciationCandidate(entry.Word, entry.Variant, ipa));
        }

        return new EnglishPronunciationIndex(grouped.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<EnglishPronunciationCandidate>)pair.Value.OrderBy(candidate => candidate.Variant).ToArray(),
            StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<EnglishPronunciationCandidate> LookupAll(string word) =>
        _entries.TryGetValue(word, out var candidates) ? candidates : [];

    public EnglishPronunciationCandidate? Lookup(string word) =>
        LookupAll(word).FirstOrDefault(candidate => candidate.IsDefault) is { Ipa.Length: > 0 } candidate
            ? candidate
            : null;
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
            // CMUdict 同时存在 ;;; 旧式注释、# 注释和行尾来源标签，均不属于发音。
            var content = line.AsSpan().Trim();
            var comment = content.IndexOf('#');
            if (comment >= 0) content = content[..comment].TrimEnd();
            if (content.IsEmpty || content.StartsWith(";;;", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = content.IndexOfAny(' ', '\t');
            if (separator <= 0 || separator == content.Length - 1)
            {
                continue;
            }

            var rawWord = content[..separator].ToString();
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

            var arpabet = content[(separator + 1)..].Trim().ToString();
            if (arpabet.Length == 0)
            {
                continue;
            }

            yield return new CmuDictionaryEntry(rawWord.ToLowerInvariant(), variant, arpabet);
        }
    }
}
