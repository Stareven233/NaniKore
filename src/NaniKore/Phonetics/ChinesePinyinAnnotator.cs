using NaniKore.Translation;
using NMeCab;

namespace NaniKore.Phonetics;

/// <summary>
/// 简体中文拼音标注器：与日语同一个引擎（NMeCab），换 CC-CEDICT 转译的 MeCab 词典。
/// 读音取词条 feature[0]（带声调符号的拼音，如 nǐ hǎo）；多音字由词面自动区分
/// （行[xíng] 独立成条 vs 银行[yín háng]）；未知 Feature 不作为拼音展示。
/// </summary>
internal sealed class ChinesePinyinAnnotator : ITextAnnotator, IDisposable
{
    private readonly object _gate = new();
    private MeCabTagger? _tagger;
    private bool _probed;

    public string LanguageCode => "zh";

    public bool IsAvailable
    {
        get
        {
            EnsureTagger();
            return _tagger is not null;
        }
    }

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
    {
        EnsureTagger();
        if (_tagger is null || string.IsNullOrEmpty(plainText))
        {
            return [];
        }

        var spans = new List<AnnotationSpan>();
        foreach (var (segmentStart, segment) in BmpSegments(plainText))
        {
            var cursor = 0;
            foreach (var node in _tagger.Parse(segment))
            {
                var surface = node.Surface;
                if (string.IsNullOrEmpty(surface))
                {
                    continue;
                }

                var relativeStart = segment.IndexOf(surface, cursor, StringComparison.Ordinal);
                if (relativeStart < 0)
                {
                    // 未找到词面时跳过，严禁 start = cursor 导致游标与字符错位。
                    continue;
                }

                var start = segmentStart + relativeStart;
                cursor = relativeStart + surface.Length;

                // 底字区间只落于合法 CJK 汉字，严禁英文、数字、标点符号混入。
                if (!surface.All(RubyTextParser.IsCjkKanji))
                {
                    continue;
                }

                var syllables = ParseSyllables(node.Feature, start, surface.Length);
                if (syllables is null)
                {
                    continue;
                }
                var pinyin = string.Join(' ', syllables.Select(s => s.Reading));

                if (!context.ShouldEmit(surface, pinyin))
                {
                    continue;
                }

                spans.Add(new AnnotationSpan(start, surface.Length, pinyin, syllables));
            }
        }

        return spans;
    }

    /// <summary>候选词数 = 含汉字词面数：与 <see cref="Annotate"/> 同一套分词口径，只数词形不查读音。</summary>
    public int CountWords(string plainText)
    {
        EnsureTagger();
        if (_tagger is null || string.IsNullOrEmpty(plainText))
        {
            return 0;
        }

        var count = 0;
        foreach (var (_, segment) in BmpSegments(plainText))
        {
            foreach (var node in _tagger.Parse(segment))
            {
                if (!string.IsNullOrEmpty(node.Surface) && ContainsHanzi(node.Surface))
                {
                    count++;
                }
            }
        }

        return count;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tagger?.Dispose();
            _tagger = null;
        }
    }

    /// <summary>词典下载落地后清闩锁：下一次探测重新创建 Tagger，实现无需重启的热生效。</summary>
    public void ResetAvailability()
    {
        lock (_gate)
        {
            _probed = false;
        }
    }

    /// <summary>
    /// LibNMeCab 的 <see cref="MeCabNode.Feature"/> 是「词面\t素性」整体（实测），拼音取最后一个制表符之后的内容。
    /// </summary>
    private static string ExtractFeature(string feature)
    {
        if (string.IsNullOrEmpty(feature))
        {
            return string.Empty;
        }

        var tab = feature.LastIndexOf('\t');
        return tab < 0 ? feature : feature[(tab + 1)..];
    }

    /// <summary>未知词 Feature 不具备可信读音；只接纳一字一音且字符属于拼音字母的词条。</summary>
    internal static IReadOnlyList<AnnotationSyllable>? ParseSyllables(string feature, int start, int length)
    {
        var extracted = ExtractFeature(feature);
        if (extracted.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var readings = extracted.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (readings.Length != length || readings.Any(s => s.Length == 0 || !s.All(ch =>
            ch is >= 'a' and <= 'z' or 'ā' or 'á' or 'ǎ' or 'à' or 'ē' or 'é' or 'ě' or 'è'
                or 'ī' or 'í' or 'ǐ' or 'ì' or 'ō' or 'ó' or 'ǒ' or 'ò' or 'ū' or 'ú' or 'ǔ' or 'ù'
                or 'ü' or 'ǖ' or 'ǘ' or 'ǚ' or 'ǜ' or 'ń' or 'ň' or 'ǹ' or 'ḿ')))
        {
            return null;
        }
        return readings.Select((reading, index) => new AnnotationSyllable(start + index, 1, reading)).ToArray();
    }

    /// <summary>
    /// MeCab 的 BMP 字符表不能安全处理代理项；只在此边界切段，保留其他标点、空白及混排上下文。
    /// 合法非 BMP scalar 一次跳过两单元，非法代理项跳过一单元，后续偏移仍取原文位置。
    /// </summary>
    private static IEnumerable<(int Start, string Text)> BmpSegments(string text)
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

    private static bool ContainsHanzi(string surface)
    {
        foreach (var ch in surface)
        {
            if (RubyTextParser.IsCjkKanji(ch))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureTagger()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;
        try
        {
            if (!PhoneticDependencyPaths.ChineseDictionaryFiles.All(File.Exists))
            {
                return;
            }

            _tagger = MeCabTagger.Create(PhoneticDependencyPaths.ChineseDictionaryDirectory, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            _tagger = null;
        }
    }
}
