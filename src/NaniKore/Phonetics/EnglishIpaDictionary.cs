using Microsoft.Data.Sqlite;

namespace NaniKore.Phonetics;

/// <summary>
/// 英语 IPA 词典（deps/phonetics/en/ipa_dict.db）：(word, variant) 多读音表，variant=1 为默认读音。
/// 默认查询 O(log n)；候选完整保留供后续查看。词条由管线以 CMUdict 为基座生成。
/// 缺失时整个英语标注器软降级（不注音，不报错）。
/// </summary>
internal sealed class EnglishIpaDictionary : IDisposable
{
    private readonly SqliteConnection _connection;

    private EnglishIpaDictionary(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>打开只读连接；词典文件缺失返回 null（软降级）。</summary>
    public static EnglishIpaDictionary? TryOpen(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            return new EnglishIpaDictionary(connection);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>按小写词形查默认读音（variant 最小者，即编号 1）；未命中返回 null。</summary>
    /// <remarks>
    /// 读出的历史条目是生成期 wire format（重音贴在元音前、无音节点、IY 不带 ː，如 treason → trˈizən），
    /// 必须过 <see cref="IpaTranscription.Normalize"/> 归一成词典式 IPA（ˈtriː.zən）再给界面；
    /// 该归一对按新口径生成的条目幂等。
    /// </remarks>
    public string? Lookup(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        // 多读音表：默认取 variant 最小者（编号 1 必存在，ORDER BY 仅兜底）。
        command.CommandText = "SELECT ipa FROM words WHERE word = $word ORDER BY variant LIMIT 1";
        command.Parameters.AddWithValue("$word", word.ToLowerInvariant());
        var stored = command.ExecuteScalar() as string;
        return stored is null ? null : IpaTranscription.Normalize(stored);
    }

    /// <summary>按小写词形查全部候选读音（variant 升序，首项即默认）；每条过 Normalize，未命中返回空。</summary>
    public IReadOnlyList<EnglishPronunciationCandidate> LookupAll(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return [];
        }

        var normalized = word.ToLowerInvariant();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT variant, ipa FROM words WHERE word = $word ORDER BY variant";
        command.Parameters.AddWithValue("$word", normalized);
        using var reader = command.ExecuteReader();
        var candidates = new List<EnglishPronunciationCandidate>();
        while (reader.Read())
        {
            candidates.Add(new EnglishPronunciationCandidate(normalized, reader.GetInt32(0), IpaTranscription.Normalize(reader.GetString(1))));
        }

        return candidates;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// 英语规则词形还原（lemmatizer）兜底：按屈折后缀尝试候选词形回查。
/// 设计约束：常见屈折（-s/-es/-ed/-ing/-ly）优先直接烧进词库，还原只是长尾兜底，
/// 绝不用还原词形的重音替代原词读音（如 abandoned ≠ abandon 的重音位置）。
/// </summary>
internal static class EnglishLemmatizer
{
    /// <summary>按「最像原词 → 最激进还原」的顺序产出候选（含原词自身）。</summary>
    public static IEnumerable<string> Candidates(string word)
    {
        yield return word;

        if (word.Length < 4)
        {
            yield break;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal))
        {
            yield return string.Concat(word.AsSpan(0, word.Length - 3), "y"); // studies → study
        }

        if (word.EndsWith("es", StringComparison.Ordinal))
        {
            yield return word[..^2]; // boxes → box
        }

        if (word.EndsWith("s", StringComparison.Ordinal) && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            yield return word[..^1]; // devices → device
        }

        if (word.EndsWith("ied", StringComparison.Ordinal))
        {
            yield return string.Concat(word.AsSpan(0, word.Length - 3), "y"); // carried → carry
        }

        if (word.EndsWith("ed", StringComparison.Ordinal))
        {
            yield return word[..^2]; // walked → walk
            if (word.Length > 4 && word[^3] == word[^4])
            {
                yield return word[..^3]; // stopped → stop
            }
        }

        if (word.EndsWith("ing", StringComparison.Ordinal))
        {
            yield return word[..^3]; // walking → walk
            if (word.Length > 5 && word[^4] == word[^5])
            {
                yield return word[..^4]; // running → run
            }

            yield return word[..^3] + "e"; // dancing-like 兜底（dance 已在库则不发生）
        }

        if (word.EndsWith("ly", StringComparison.Ordinal))
        {
            yield return word[..^2]; // quickly → quick
        }
    }
}
