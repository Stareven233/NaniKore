namespace NaniKore.Phonetics;

/// <summary>
/// 英语整词候选的展示口径（issue 011）：把原生引擎给出的候选收成「本句选中项 + 真正异读」。
/// 引擎的候选列表来自词典条目，句重音变化（They 的 ˌðeɪ/ðeɪ、through 的 θruː/ˈθruː）也在其中；
/// 这些只差 ˈ/ˌ 的条目不是异读，展示时必须折叠，否则几乎每个词都会挂出「多读」提示。
/// 反过来说，record 的动词/名词重音、read 的 /red/ 与 /riːd/ 属于真异读，必须保留。
/// </summary>
internal static class EnglishReadingCandidates
{
    /// <summary>
    /// 折叠候选：首项固定是本句选中读音，其后按引擎顺序追加，重复项与「仅重音不同」的项被剔除。
    /// 返回列表至少有一项（等于 <paramref name="reading"/>）。
    /// </summary>
    public static IReadOnlyList<string> Collapse(string reading, IReadOnlyList<string>? candidates)
    {
        var kept = new List<string>(candidates?.Count + 1 ?? 1) { reading };
        if (candidates is null || candidates.Count == 0)
        {
            return kept;
        }

        // 键只抹掉重音符号：音节点、长音符与元音差异都要保留，避免把 record 的名/动词读法折叠成一条。
        var keys = new HashSet<string>(StringComparer.Ordinal) { StressInsensitiveKey(reading) };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !keys.Add(StressInsensitiveKey(candidate)))
            {
                continue;
            }

            kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>去掉主/次重音符号后的比较键（IPA 音节点与音符本身保留）。</summary>
    internal static string StressInsensitiveKey(string ipa) => ipa.Replace("ˈ", string.Empty, StringComparison.Ordinal)
        .Replace("ˌ", string.Empty, StringComparison.Ordinal);
}
