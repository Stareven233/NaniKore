using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 单个原生语言包实例的标注器（由 <see cref="NativePhoneticPackHost"/> 挂在惰性资源上）。
/// 生命周期：首次访问时按受控绝对路径加载并校验 DLL（冷加载约 1.4s 日语 / 0.6s 英语，必须发生在后台），
/// 闲置回收或重置时只调用 DLL 的 release 解除词表引用，不卸载 DLL；下次访问自动重建。
/// 任何加载失败都退化成「不注音」并留下原因，绝不抛异常打断翻译主流程。
/// </summary>
internal sealed class NativeTextAnnotator : ITextAnnotator, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<string, string> _packDirectory;
    private NativePhoneticLibrary? _library;
    private bool _disposed;

    /// <param name="packDirectory">包目录解析（生产用正式目录；自测可注临时目录，使状态与真实加载永远指向同一处）。</param>
    public NativeTextAnnotator(string languageCode, Func<string, string>? packDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        LanguageCode = languageCode;
        _packDirectory = packDirectory ?? NativePhoneticManifest.GetPackDirectory;
    }

    public string LanguageCode { get; }

    /// <summary>最近一次加载失败的原因分类（成功为 <see cref="NativePhoneticPackageFailure.None"/>）。</summary>
    public NativePhoneticPackageFailure Failure { get; private set; }

    /// <summary>失败明细（文件路径、导出名、ABI 版本等），供设置页与自测定位。</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// 词表是否可用：首次访问会真正加载并校验 DLL——设置页展示的状态与这里的判定因此不会互相矛盾。
    /// </summary>
    public bool IsAvailable => EnsureLibrary() is not null;

    /// <summary>丢弃当前实例（设置页下载完成或语言包被替换后调用）：下一次访问重新校验与重建。</summary>
    public void ResetAvailability()
    {
        lock (_gate)
        {
            _library?.Dispose();
            _library = null;
            Failure = NativePhoneticPackageFailure.None;
            FailureDetail = null;
        }
    }

    /// <summary>候选词计数：与注音同一套分词口径，但不依赖读音是否成功（只数能被认领的词元）。</summary>
    public int CountWords(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return 0;
        }

        var count = 0;
        foreach (var token in RunEngine(plainText))
        {
            if (IsAnnotatable(token))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>产出原文区间；底字必须落在原文内并与词面切片一致，否则整条丢弃而不是猜偏移。</summary>
    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return [];
        }

        var japanese = string.Equals(LanguageCode, NativePhoneticManifest.JapaneseCode, StringComparison.OrdinalIgnoreCase);
        var spans = new List<AnnotationSpan>();
        foreach (var token in RunEngine(plainText))
        {
            if (token.Reading is not { Length: > 0 } reading || !IsAnnotatable(token))
            {
                continue;
            }

            if (token.Start < 0 || token.Length <= 0 || token.Start + token.Length > plainText.Length)
            {
                continue;
            }

            if (!plainText.AsSpan(token.Start, token.Length).SequenceEqual(token.Surface))
            {
                continue;
            }

            // 日语：UniDic 给的是整词书面假名，界面上要的是只覆盖汉字的振假名，送假名切回底字之外。
            var start = token.Start;
            var length = token.Length;
            if (japanese)
            {
                if (!JapaneseRuby.TrySplit(token.Surface, reading, out var baseOffset, out var baseLength, out var baseReading))
                {
                    continue;
                }

                start += baseOffset;
                length = baseLength;
                reading = baseReading;
            }

            // 过滤与去重仍按整词词面裁决：停用词表、常用汉字表与去重键都以词面为准，不因切送假名而变。
            if (!context.ShouldEmit(token.Surface, reading))
            {
                continue;
            }

            spans.Add(new AnnotationSpan(start, length, reading));
        }

        return spans;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // 只解除词表/模型引用：NativeAOT DLL 不支持卸载，生产明确不强制 GC、不裁工作集。
            _library?.Dispose();
            _library = null;
        }
    }

    private IReadOnlyList<NativePhoneticToken> RunEngine(string plainText)
    {
        var library = EnsureLibrary();
        if (library is null)
        {
            return [];
        }

        var result = library.Annotate(plainText);
        // 引擎级错误（非法输入、解析失败）只吞掉本次结果：那是文本问题，不是包不可用。
        return result.Succeeded ? result.Tokens : [];
    }

    private NativePhoneticLibrary? EnsureLibrary()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_library is not null)
            {
                return _library;
            }

            if (NativePhoneticLibrary.TryOpenAt(LanguageCode, _packDirectory(LanguageCode), out var library, out var failure, out var detail))
            {
                _library = library;
                Failure = NativePhoneticPackageFailure.None;
                FailureDetail = null;
                return _library;
            }

            Failure = failure;
            FailureDetail = detail;
            return null;
        }
    }

    /// <summary>
    /// 能被认领的词元：日语只认含汉字的词面（假名/拉丁不注），英语只认至少两个拉丁字母的词面
    /// （数字、货币、符号本轮不注音）。与旧标注器的认领口径一致，切换 provider 时过滤行为不变。
    /// </summary>
    private bool IsAnnotatable(NativePhoneticToken token)
    {
        if (token.Surface.Length == 0 || token.Length <= 0)
        {
            return false;
        }

        if (string.Equals(LanguageCode, NativePhoneticManifest.JapaneseCode, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ch in token.Surface)
            {
                if (RubyTextParser.IsCjkKanji(ch))
                {
                    return true;
                }
            }

            return false;
        }

        var letters = 0;
        foreach (var ch in token.Surface)
        {
            if (char.IsAsciiLetter(ch))
            {
                letters++;
            }
            else if (ch is not ('\'' or '’' or '.'))
            {
                return false;
            }
        }

        return letters >= 2;
    }
}
