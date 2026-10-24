using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 多语言注音调度器：按探测结果（或用户手选的源语言）把原文分派给各标注器，合并注音区间。
/// 分派规则（issue 008 决策）：
///   - 拉丁词元固定归英语标注器；CJK 歧义区段归探测/手选语言（手选优先）；
///   - 含假名或纯汉字（无简体特征）→ 日语候选；命中简体独占字 → 中文（简体）；
///   - 注音是源文阅读辅助，与目标语言解耦，不再做旧提示词时代的目标语言互斥剪枝。
/// </summary>
internal sealed class TextAnnotatorRegistry
{
    private readonly Dictionary<string, ITextAnnotator> _annotators;

    public TextAnnotatorRegistry(IEnumerable<ITextAnnotator> annotators)
    {
        _annotators = new Dictionary<string, ITextAnnotator>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotator in annotators)
        {
            _annotators[annotator.LanguageCode] = annotator;
        }
    }

    /// <summary>默认装配：初版三语；词典缺哪语哪语自动软降级（IsAvailable=false）。</summary>
    public static TextAnnotatorRegistry CreateDefault() =>
        new(
        [
            new JapaneseFuriganaAnnotator(),
            new EnglishIpaAnnotator(),
            new ChinesePinyinAnnotator()
        ]);

    /// <summary>指定语言标注器；未装配或缺词典时为 null。</summary>
    public ITextAnnotator? GetAnnotator(string? languageCode)
    {
        var code = NormalizeLanguageCode(languageCode);
        return code is not null && _annotators.TryGetValue(code, out var annotator) ? annotator : null;
    }

    /// <summary>
    /// 设置页下载/更新词典成功后调用：清掉标注器的不可用闩锁，使其下次访问时重新探测。
    /// 传 null 或「auto/自动检测」时重置全部已装配标注器（一次安装动作可能同时补全多个语言）。
    /// </summary>
    public void ResetAvailability(string? languageCode = null)
    {
        var code = NormalizeLanguageCode(languageCode);
        foreach (var annotator in _annotators.Values)
        {
            if (code is null || string.Equals(annotator.LanguageCode, code, StringComparison.OrdinalIgnoreCase))
            {
                annotator.ResetAvailability();
            }
        }
    }

    /// <summary>
    /// 统一入口：<paramref name="pinnedLanguageCode"/> 非空（且不是 auto/自动检测）时按手选语言执行，
    /// 否则按探测结果执行。返回合并后的注音区间（按起点排序），缺失词典的语言不产生区间。
    /// </summary>
    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, string? pinnedLanguageCode = null)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return [];
        }

        var spans = new List<AnnotationSpan>();
        foreach (var annotator in SelectAnnotators(plainText, pinnedLanguageCode))
        {
            if (!annotator.IsAvailable)
            {
                continue;
            }

            // 与结果面板同一口径：候选词不足阈值时短文本全注，否则按难词过滤
            var profile = AnnotationProfile.Load(annotator.LanguageCode, wordCount: annotator.CountWords(plainText));
            var context = new AnnotationContext(profile);
            spans.AddRange(annotator.Annotate(plainText, context));
        }

        spans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return spans;
    }

    private IEnumerable<ITextAnnotator> SelectAnnotators(string plainText, string? pinnedLanguageCode)
    {
        var pinned = NormalizeLanguageCode(pinnedLanguageCode);
        if (pinned is "ja" or "zh" or "en")
        {
            // 手选语言只执行该语言标注器：用户明确知道原文属于哪种语言（含纠错误探测的场景）
            return _annotators.TryGetValue(pinned, out var pinnedAnnotator) ? [pinnedAnnotator] : [];
        }

        var detection = LanguageDetector.Detect(plainText, targetLanguage: null);
        var selected = new List<ITextAnnotator>();
        var cjkCode = ResolveCjkLanguage(detection);
        if (cjkCode is not null && _annotators.TryGetValue(cjkCode, out var cjkAnnotator))
        {
            selected.Add(cjkAnnotator);
        }

        // 拉丁词元固定归英语标注器（CJK 区间与拉丁词元天然不重叠，无需先到先得仲裁）
        if (detection.HasEnglish && _annotators.TryGetValue("en", out var englishAnnotator))
        {
            selected.Add(englishAnnotator);
        }

        return selected;
    }

    /// <summary>CJK 歧义语言裁决：简体特征优先（简体独占字是确证），否则含假名/纯汉字按日语候选。</summary>
    private static string? ResolveCjkLanguage(LanguageDetectionResult detection)
    {
        if (detection.HasSimplifiedChinese)
        {
            return "zh";
        }

        if (detection.HasJapanese)
        {
            return "ja";
        }

        return null;
    }

    /// <summary>
    /// 把手选值归一化为语言代码：接受代码与共享语言列表的展示名/别名（日本語、English、中文（简体）等）；
    /// 空值或自动检测返回 null（走探测）。
    /// </summary>
    internal static string? NormalizeLanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("自动检测", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed is "ja" or "JA" or "日本語" or "日语" or "日语（日本語）" || trimmed.Contains("日本", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Japanese", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        if (trimmed is "zh" or "ZH" or "中文（简体）" or "简体中文" or "中文" || trimmed.Contains("简体", StringComparison.OrdinalIgnoreCase))
        {
            return "zh";
        }

        if (trimmed is "en" or "EN" or "English" || trimmed.Equals("英语", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        return null;
    }
}
