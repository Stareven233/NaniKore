using System.IO.Compression;
using System.Text;
using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 中文读音引擎产出的「词级单元」：一段原文区间与逐字音节（含候选）。
/// <see cref="Syllables"/> 为 null 表示该区间不该注音（非汉字，或词典无读音的未知汉字），调用方直接跳过。
/// </summary>
internal readonly record struct ChineseToken(int Start, int Length, IReadOnlyList<AnnotationSyllable>? Syllables);

/// <summary>
/// 进程内中文拼音引擎：pypinyin 词组/单字数据 + CC-CEDICT 覆盖 + 繁体 t2s 折叠，
/// 以最长匹配产出词级单元。不依赖 NMeCab 或二进制词典，纯数据 + 逐字对齐。
/// 数据由 <see cref="ChinesePinyinDataBuilder"/> 生成（TSV，gzip），随程序集内嵌（issue 012）。
/// </summary>
internal sealed class ChinesePinyinEngine
{
    /// <summary>内嵌资源名（csproj 里以 LogicalName 固定，与文件名解耦）。</summary>
    internal const string EmbeddedResourceName = "NaniKore.Phonetics.Data.zh_pinyin.txt.gz";

    /// <summary>
    /// 内嵌数据字节的 SHA-256（哈希即版本）：重生成数据后必须同步更新，自测会核对。
    /// 与内嵌前的 `deps/phonetics/zh/zh_pinyin.txt.gz` 原件逐字节一致。
    /// </summary>
    internal const string EmbeddedDataSha256 = "2B5C68679BD393624D17B77BCA5734F9A608EB758FFA2F98D69D1DE0E520A981";

    private readonly Dictionary<int, string[]> _single;      // 码位 -> 候选读音（首项为默认）
    private readonly Dictionary<int, string[]> _fallbackSingle; // CC-CEDICT 单字兜底（仅单字无 pypinyin 时用）
    private readonly Dictionary<string, string[]> _phrase;   // 简体词面 -> 每字默认读音
    private readonly Dictionary<char, char> _t2s;            // 繁 -> 简 单字
    private readonly int _maxPhraseLength;

    private ChinesePinyinEngine(
        Dictionary<int, string[]> single,
        Dictionary<int, string[]> fallbackSingle,
        Dictionary<string, string[]> phrase,
        Dictionary<char, char> t2s,
        int maxPhraseLength)
    {
        _single = single;
        _fallbackSingle = fallbackSingle;
        _phrase = phrase;
        _t2s = t2s;
        _maxPhraseLength = maxPhraseLength;
    }

    /// <summary>
    /// 从程序集内嵌资源装载：每次调用新开一条资源流（闲置释放后可原样重载），
    /// 不读 deps/phonetics/zh，也没有任何外部旧数据回退。
    /// 资源缺失或数据损坏返回 null（标注链路软降级为纯文本）。
    /// </summary>
    public static ChinesePinyinEngine? TryLoad()
    {
        using var resource = typeof(ChinesePinyinEngine).Assembly.GetManifestResourceStream(EmbeddedResourceName);
        return resource is null ? null : TryLoad(resource);
    }

    private static ChinesePinyinEngine? TryLoad(Stream compressed)
    {
        try
        {
            var single = new Dictionary<int, string[]>();
            var fallback = new Dictionary<int, string[]>();
            var phrase = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var t2s = new Dictionary<char, char>();
            var maxLen = 1;

            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            // 首行版本标记，格式不认时直接视为不可用
            if (reader.ReadLine() != ChinesePinyinDataBuilder.FormatVersion)
            {
                return null;
            }

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split('\t');
                switch (parts[0])
                {
                    case "S" when parts.Length == 3 && int.TryParse(parts[1], out var cp):
                        single[cp] = SplitCandidates(parts[2]);
                        break;
                    case "C" when parts.Length == 3 && int.TryParse(parts[1], out var ccp):
                        fallback[ccp] = SplitCandidates(parts[2]);
                        break;
                    case "P" when parts.Length >= 3:
                        var readings = parts[2..];
                        phrase[parts[1]] = readings;
                        if (readings.Length > maxLen)
                        {
                            maxLen = readings.Length;
                        }

                        break;
                    case "T" when parts.Length == 3 && parts[1].Length == 1 && parts[2].Length == 1:
                        t2s[parts[1][0]] = parts[2][0];
                        break;
                }
            }

            return new ChinesePinyinEngine(single, fallback, phrase, t2s, maxLen);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static string[] SplitCandidates(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>把原文分词为词级单元；非汉字与代理项不作为注音单元。</summary>
    public IReadOnlyList<ChineseToken> Segment(string text)
    {
        var tokens = new List<ChineseToken>();
        foreach (var (segmentStart, segment) in BmpSegments(text))
        {
            var cursor = 0;
            while (cursor < segment.Length)
            {
                if (TryMatchPhrase(segment, cursor, out var phraseLength, out var readings))
                {
                    var syllables = new AnnotationSyllable[phraseLength];
                    for (var k = 0; k < phraseLength; k++)
                    {
                        var simp = Simplify(segment[cursor + k]);
                        var reading = readings[k];
                        syllables[k] = new AnnotationSyllable(segmentStart + cursor + k, 1, reading, BuildCandidates(reading, simp));
                    }

                    tokens.Add(new ChineseToken(segmentStart + cursor, phraseLength, syllables));
                    cursor += phraseLength;
                    continue;
                }

                var ch = segment[cursor];
                if (RubyTextParser.IsCjkKanji(ch))
                {
                    var simp = Simplify(ch);
                    if (TryLookupChar(simp, out var candidates))
                    {
                        var reading = candidates[0];
                        tokens.Add(new ChineseToken(segmentStart + cursor, 1,
                            [new AnnotationSyllable(segmentStart + cursor, 1, reading, candidates)]));
                    }
                    // 词典无读音的未知汉字：不产出，避免臆造读音（保留原文纯文）
                }

                cursor++;
            }
        }

        return tokens;
    }

    /// <summary>可注音候选词数：与 <see cref="Segment"/> 同一分词口径，只数有读音的汉字词元。</summary>
    public int CountWords(string text)
    {
        var count = 0;
        foreach (var token in Segment(text))
        {
            if (token.Syllables is not null)
            {
                count++;
            }
        }

        return count;
    }

    private bool TryMatchPhrase(string segment, int cursor, out int length, out string[] readings)
    {
        var upper = Math.Min(_maxPhraseLength, segment.Length - cursor);
        for (var candidate = upper; candidate >= 2; candidate--)
        {
            var allHanzi = true;
            for (var k = 0; k < candidate; k++)
            {
                if (!RubyTextParser.IsCjkKanji(segment[cursor + k]))
                {
                    allHanzi = false;
                    break;
                }
            }

            if (!allHanzi)
            {
                continue;
            }

            var key = BuildSimplifiedKey(segment, cursor, candidate);
            if (_phrase.TryGetValue(key, out var matched) && matched.Length == candidate)
            {
                length = candidate;
                readings = matched;
                return true;
            }
        }

        length = 0;
        readings = [];
        return false;
    }

    private string BuildSimplifiedKey(string segment, int cursor, int length)
    {
        Span<char> buffer = length <= 32 ? stackalloc char[length] : new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = Simplify(segment[cursor + i]);
        }

        return new string(buffer);
    }

    private char Simplify(char ch) => _t2s.TryGetValue(ch, out var simplified) ? simplified : ch;

    private bool TryLookupChar(char simplified, out string[] candidates)
    {
        if (_single.TryGetValue(simplified, out candidates!))
        {
            return true;
        }

        return _fallbackSingle.TryGetValue(simplified, out candidates!);
    }

    /// <summary>候选集合把本句默认读音放首位，其余来自单字多音候选，去重。</summary>
    private IReadOnlyList<string> BuildCandidates(string reading, char simplified)
    {
        if (!TryLookupChar(simplified, out var extra) || extra.Length <= 1)
        {
            return extra is { Length: 1 } && extra[0] == reading ? extra : [reading];
        }

        var list = new List<string>(extra.Length + 1) { reading };
        foreach (var item in extra)
        {
            if (item.Length > 0 && item != reading && !list.Contains(item))
            {
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// 代理项边界切段：MeCab 时代遗留的稳健切分，这里用于保证非 BMP 字不破坏后续已知词的偏移。
    /// 合法非 BMP scalar 一次跳过两单元，非法代理项跳过一单元；产出的 Start 是原文 UTF-16 偏移。
    /// </summary>
    internal static IEnumerable<(int Start, string Text)> BmpSegments(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index]))
            {
                continue;
            }

            if (index > start)
            {
                yield return (start, text[start..index]);
            }

            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            yield return (start, text[start..]);
        }
    }
}
