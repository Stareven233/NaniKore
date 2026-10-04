namespace NaniKore.Phonetics;

/// <summary>
/// 英语 IPA 标注器：对 ASCII 词元标注国际音标。
/// 口径：仅拉丁词元；跳过原文中已有的方括号区域（避免给上轮残留注音/代码数组下标注音）、
/// 单字母、未登录词；所有格（Bob's）只给 Bob 部分注音。
/// </summary>
internal sealed class EnglishIpaAnnotator : ITextAnnotator, IDisposable
{
    private EnglishIpaDictionary? _dictionary;
    private bool _probed;

    public string LanguageCode => "en";

    public bool IsAvailable
    {
        get
        {
            EnsureDictionary();
            return _dictionary is not null;
        }
    }

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
    {
        EnsureDictionary();
        if (_dictionary is null || string.IsNullOrEmpty(plainText))
        {
            return [];
        }

        var spans = new List<AnnotationSpan>();
        var inBracket = false;
        var i = 0;
        while (i < plainText.Length)
        {
            var ch = plainText[i];
            if (ch == '[')
            {
                inBracket = true;
                i++;
                continue;
            }

            if (ch == ']')
            {
                inBracket = false;
                i++;
                continue;
            }

            if (inBracket || !IsWordChar(ch))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < plainText.Length && IsWordChar(plainText[i]))
            {
                i++;
            }

            var word = plainText[start..i];
            if (word.Length < 2)
            {
                continue;
            }

            // 所有格 Bob's → 只给 Bob 注音（'s 不纳入区间）
            var spanLength = word.Length;
            var lookupWord = word;
            if (spanLength > 3 && (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal)))
            {
                spanLength -= 2;
                lookupWord = word[..spanLength];
            }

            var ipa = Lookup(lookupWord);
            if (ipa is null)
            {
                continue;
            }

            if (!context.ShouldEmit(lookupWord, ipa))
            {
                continue;
            }

            spans.Add(new AnnotationSpan(start, spanLength, ipa));
        }

        return spans;
    }

    public void Dispose() => _dictionary?.Dispose();

    /// <summary>词典下载落地后清闩锁：下一次探测重新打开 SQLite，实现无需重启的热生效。</summary>
    public void ResetAvailability() => _probed = false;

    /// <summary>直查优先，未命中再按规则还原候选逐个回查。</summary>
    private string? Lookup(string word) =>
        _dictionary!.Lookup(word) ?? EnglishLemmatizer.Candidates(word).Select(candidate => _dictionary.Lookup(candidate)).FirstOrDefault(ipa => ipa is not null);

    private void EnsureDictionary()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;
        _dictionary = EnglishIpaDictionary.TryOpen(PhoneticDependencyPaths.EnglishDictionaryFile);
    }

    private static bool IsWordChar(char ch) =>
        ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '\'' or '’';
}
