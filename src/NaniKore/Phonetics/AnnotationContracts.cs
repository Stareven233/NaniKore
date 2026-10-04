using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 一段原文上的注音落点：从 <see cref="Start"/> 起 <see cref="Length"/> 个字符为底字，读音是 <see cref="Reading"/>。
/// 落点只描述「谁带读音」，不负责渲染；由 <see cref="AnnotatedTextFormatter.Compose"/> 拼成 底字[读音]
/// 后交既有的 RubyTextParser/RubyDocumentBuilder 消费。
/// 区间不重叠：汉字区间与拉丁词元天然互斥，日语/中文/英语标注器各自只认领自己脚本的区段。
/// </summary>
internal readonly record struct AnnotationSpan(int Start, int Length, string Reading);

/// <summary>
/// 多语言注音标注器契约：初版 ja（振假名）/ en（IPA）/ zh（简体拼音），预留 ko 等扩展位。
/// 每个标注器自带词典可用性判定，缺失时由整体链路软降级为纯文本，不抛异常。
/// </summary>
internal interface ITextAnnotator
{
    /// <summary>语言代码（"ja" / "en" / "zh"），与源语言下拉和探测结果互认。</summary>
    string LanguageCode { get; }

    /// <summary>词典是否就绪（deps/phonetics/&lt;lang&gt;/ 齐备且哈希通过）。</summary>
    bool IsAvailable { get; }

    /// <summary>对纯文本原文产出注音区间；停用过滤与去重统一经 <paramref name="context"/> 裁决。</summary>
    IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context);
}

/// <summary>
/// 单语言的注音过滤判据（issue 008 决策 C：规范字符表 + 策展词表）。
/// 与标注器解耦：日后要升级成语料词频表（SUBTLex/BCCWJ 类）时只换本对象的数据来源。
/// </summary>
internal sealed class AnnotationProfile
{
    private readonly HashSet<string> _stopWords;
    private readonly HashSet<char> _commonChars;

    private AnnotationProfile(
        string languageCode,
        bool enabled,
        bool rareOnly,
        HashSet<string> stopWords,
        HashSet<char> commonChars)
    {
        LanguageCode = languageCode;
        Enabled = enabled;
        RareOnly = rareOnly;
        _stopWords = stopWords;
        _commonChars = commonChars;
    }

    /// <summary>语言代码。</summary>
    public string LanguageCode { get; }

    /// <summary>注音总开关；关闭时一律淘汰（原文纯文本）。</summary>
    public bool Enabled { get; }

    /// <summary>仅注难词：开启时再叠加「常用字符表」过滤；关闭时只走停用词表。</summary>
    public bool RareOnly { get; }

    /// <summary>从嵌入式数据资源装载三语判据；文件缺失时退化为「不注」的最保守口径。</summary>
    public static AnnotationProfile Load(string languageCode, bool enabled = true, bool rareOnly = true)
    {
        var stopWords = EmbeddedData.LoadLines($"stopwords-{languageCode}.txt");
        var commonChars = EmbeddedData.LoadChars(
            languageCode == "ja" ? "joyo-kanji.txt" : languageCode == "zh" ? "common-chars.txt" : null);
        return new AnnotationProfile(languageCode, enabled, rareOnly, stopWords, commonChars);
    }

    /// <summary>
    /// 该词形是否淘汰：开关关闭、命中停用词表、或（仅难词模式下）整词均为常用字符表的汉字。
    /// 无汉字的词元（假名/拉丁）不经字符表裁决——日语假名本就不注，拉丁词交给英语停用词表。
    /// </summary>
    public bool IsSkipped(string surface)
    {
        if (!Enabled || string.IsNullOrEmpty(surface))
        {
            return true;
        }

        if (_stopWords.Contains(Key(surface)))
        {
            return true;
        }

        return RareOnly && ContainsOnlyCommonChars(surface);
    }

    private static string Key(string surface) => surface.ToLowerInvariant();

    private bool ContainsOnlyCommonChars(string surface)
    {
        if (_commonChars.Count == 0)
        {
            return false;
        }

        var hasCjk = false;
        foreach (var ch in surface)
        {
            if (!RubyTextParser.IsCjkKanji(ch))
            {
                continue;
            }

            hasCjk = true;
            if (!_commonChars.Contains(ch))
            {
                return false;
            }
        }

        return hasCjk;
    }
}

/// <summary>
/// 单篇原文的注音上下文：过滤判据 + 去重状态。各标注器一份，口径全语言统一。
/// </summary>
internal sealed class AnnotationContext
{
    private readonly AnnotationProfile _profile;
    private readonly Dictionary<string, string> _emitted = new(StringComparer.OrdinalIgnoreCase);

    public AnnotationContext(AnnotationProfile profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// 裁决是否给该词形注音（判定与登记一体，调用方不得二次写入）：
    /// 停用/高频词直接淘汰；同篇同词形同读音只注首现；
    /// 同词形不同读音（多音词的上下文歧义，如 行く/行う、行 háng/xíng）补标并刷新记录。
    /// </summary>
    public bool ShouldEmit(string surface, string reading)
    {
        if (_profile.IsSkipped(surface))
        {
            return false;
        }

        var key = surface.ToLowerInvariant();
        if (_emitted.TryGetValue(key, out var previous))
        {
            if (string.Equals(previous, reading, StringComparison.Ordinal))
            {
                return false;
            }

            _emitted[key] = reading;
            return true;
        }

        _emitted[key] = reading;
        return true;
    }
}

/// <summary>嵌入式判据数据（随程序集分发的小体量策展表）的读取口径。</summary>
internal static class EmbeddedData
{
    public static HashSet<string> LoadLines(string fileName) =>
        new(ReadAllText(fileName)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && !line.StartsWith('#')), StringComparer.OrdinalIgnoreCase);

    public static HashSet<char> LoadChars(string? fileName) =>
        new(ReadAllText(fileName)
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '#'));

    private static string ReadAllText(string? fileName)
    {
        if (fileName is null)
        {
            return string.Empty;
        }

        var assembly = typeof(AnnotationProfile).Assembly;
        var resourceName = $"NaniKore.Phonetics.Data.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? string.Empty : new StreamReader(stream).ReadToEnd();
    }
}
