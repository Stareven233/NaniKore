using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 简体中文拼音标注器：进程内 <see cref="ChinesePinyinEngine"/> 做词组最长匹配与单字兜底，
/// 数据来自 pypinyin + CC-CEDICT + Unihan 合并表，随程序集内嵌（见 <see cref="ChinesePinyinDataBuilder"/>）。
/// 逐字音节带候选集合；展示/复制/收藏/选区一律用原文 code point 与 UTF-16 区间。
/// </summary>
internal sealed class ChinesePinyinAnnotator : ITextAnnotator, IDisposable
{
    private readonly object _gate = new();
    private ChinesePinyinEngine? _engine;
    private bool _probed;

    public string LanguageCode => "zh";

    public bool IsAvailable
    {
        get
        {
            EnsureEngine();
            return _engine is not null;
        }
    }

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
    {
        EnsureEngine();
        var engine = _engine;
        if (engine is null || string.IsNullOrEmpty(plainText))
        {
            return [];
        }

        var spans = new List<AnnotationSpan>();
        foreach (var token in engine.Segment(plainText))
        {
            if (token.Syllables is null)
            {
                continue;
            }

            var surface = plainText.Substring(token.Start, token.Length);
            var reading = string.Join(' ', token.Syllables.Select(s => s.Reading));
            if (!context.ShouldEmit(surface, reading))
            {
                continue;
            }

            spans.Add(new AnnotationSpan(token.Start, token.Length, reading, token.Syllables));
        }

        return spans;
    }

    /// <summary>候选词数 = 有读音的汉字词元数：与 <see cref="Annotate"/> 同一分词口径，不查过滤。</summary>
    public int CountWords(string plainText)
    {
        EnsureEngine();
        return _engine?.CountWords(plainText) ?? 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _engine = null;
        }
    }

    /// <summary>清闩锁：下一次探测重新装载内嵌数据（闲置释放、热更口径共用同一入口）。</summary>
    public void ResetAvailability()
    {
        lock (_gate)
        {
            _probed = false;
            _engine = null;
        }
    }

    private void EnsureEngine()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;
        // 内嵌资源，无外部文件可缺；每次重载都新开一条资源流（闲置释放后同样走这里）
        _engine = ChinesePinyinEngine.TryLoad();
    }
}
