using System.Security.Cryptography;
using NaniKore.Translation;
using NMeCab.Specialized;

namespace NaniKore.Phonetics;

/// <summary>
/// 日语振假名标注器：NMeCab（纯托管 LibNMeCab）+ IPAdic 编译态词典。
/// 只给含汉字的词元注音（纯假名自表音），读音取「発音」素性（平假名），
/// 送假名按「底字汉字部分带读音」切分（食べ → 食[た]べ），与 RubyTextParser 的底字回溯兼容。
/// </summary>
internal sealed class JapaneseFuriganaAnnotator : ITextAnnotator, IDisposable
{
    private readonly object _gate = new();
    private MeCabIpaDicTagger? _tagger;
    private bool _availabilityProbed;

    public string LanguageCode => "ja";

    public bool IsAvailable
    {
        get
        {
            EnsureAvailability();
            return _tagger is not null;
        }
    }

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
    {
        EnsureAvailability();
        if (_tagger is null || string.IsNullOrEmpty(plainText))
        {
            return [];
        }

        var spans = new List<AnnotationSpan>();
        var cursor = 0;
        foreach (var node in _tagger.Parse(plainText))
        {
            var surface = node.Surface;
            if (string.IsNullOrEmpty(surface))
            {
                continue;
            }

            var start = plainText.IndexOf(surface, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                start = cursor;
            }

            cursor = start + surface.Length;

            if (!ContainsKanji(surface))
            {
                continue;
            }

            // IPAdic 的 読み（片假名）才是振假名正源（発音是音便表记，如 ちゅうちょ→ちゅーちょ）；
            // 読み缺失时回落 発音，两者最终都转平假名
            var reading = KatakanaToHiragana(node.Reading);
            if (string.IsNullOrWhiteSpace(reading))
            {
                reading = KatakanaToHiragana(node.Pronounciation);
            }

            if (string.IsNullOrWhiteSpace(reading))
            {
                continue;
            }

            var kanaSuffix = CountKanaSuffix(surface);
            var baseLength = kanaSuffix == 0 || reading.Length <= kanaSuffix
                ? surface.Length
                : surface.Length - kanaSuffix;
            if (baseLength <= 0)
            {
                continue;
            }

            var baseReading = kanaSuffix == 0 ? reading : reading[..^kanaSuffix];
            if (string.IsNullOrWhiteSpace(baseReading))
            {
                continue;
            }

            if (!context.ShouldEmit(surface, baseReading))
            {
                continue;
            }

            spans.Add(new AnnotationSpan(start, baseLength, baseReading));
        }

        return spans;
    }

    /// <summary>候选词数 = 含汉字的词面数：与 <see cref="Annotate"/> 同一套分词口径，只数词形不查读音。</summary>
    public int CountWords(string plainText)
    {
        EnsureAvailability();
        if (_tagger is null || string.IsNullOrEmpty(plainText))
        {
            return 0;
        }

        var count = 0;
        foreach (var node in _tagger.Parse(plainText))
        {
            if (!string.IsNullOrEmpty(node.Surface) && ContainsKanji(node.Surface))
            {
                count++;
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
            _availabilityProbed = false;
        }
    }

    /// <summary>词典目录存在且 Tagger 可创建才算可用；失败只静默一次（随后维持不可用），绝不影响翻译主链路。</summary>
    private void EnsureAvailability()
    {
        if (_availabilityProbed)
        {
            return;
        }

        _availabilityProbed = true;
        try
        {
            var directory = PhoneticDependencyPaths.JapaneseDictionaryDirectory;
            if (!PhoneticDependencyPaths.JapaneseDictionaryFiles.All(File.Exists))
            {
                return;
            }

            _tagger = MeCabIpaDicTagger.Create(directory, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            _tagger = null;
        }
    }

    private static int CountKanaSuffix(string surface)
    {
        var count = 0;
        while (count < surface.Length && RubyTextParser.IsKana(surface[^(count + 1)]))
        {
            count++;
        }

        return count;
    }

    private static bool ContainsKanji(string surface)
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

    /// <summary>片假名读音转平假名（0x30A1-0x30F6 → 0x3041-0x3096），长音符等原样保留。</summary>
    internal static string KatakanaToHiragana(string? katakana)
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
