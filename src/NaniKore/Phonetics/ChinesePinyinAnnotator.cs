using System.Security.Cryptography;
using NaniKore.Translation;
using NMeCab;

namespace NaniKore.Phonetics;

/// <summary>
/// 简体中文拼音标注器：与日语同一个引擎（NMeCab），换 CC-CEDICT 转译的 MeCab 词典。
/// 读音取词条 feature[0]（带声调符号的拼音，如 nǐ hǎo）；多音字由词面自动区分
/// （行[xíng] 独立成条 vs 银行[yín háng]）；未登录字走 unk.dic 的单字默认读音（取最常见读音）。
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

            if (!ContainsHanzi(surface))
            {
                continue;
            }

            var pinyin = NormalizePinyin(ExtractFeature(node.Feature));
            if (string.IsNullOrEmpty(pinyin))
            {
                continue;
            }

            if (!context.ShouldEmit(surface, pinyin))
            {
                continue;
            }

            spans.Add(new AnnotationSpan(start, surface.Length, pinyin));
        }

        return spans;
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

    /// <summary>归一化：去首尾空白、压缩连续空格；空判据（*/BOS/EOS）转为空串。</summary>
    private static string NormalizePinyin(string feature)
    {
        if (string.IsNullOrWhiteSpace(feature) || feature == "*" || feature == "BOS/EOS")
        {
            return string.Empty;
        }

        return string.Join(' ', feature.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
