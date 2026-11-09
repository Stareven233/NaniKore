using NaniKore.Runtime;

namespace NaniKore.Phonetics;

internal interface IIdleTextAnnotator
{
    void ConfigureIdleTimeout(TimeSpan timeout);
}

/// <summary>每种语言独立计时；整个分词或查询过程受保护，超时和更新均只释放不用的词典实例。</summary>
internal sealed class CachedTextAnnotator<T>(string languageCode, Func<T> create)
    : ITextAnnotator, IIdleTextAnnotator, IDisposable where T : class, ITextAnnotator, IDisposable
{
    private readonly IdleResource<T> _resource = new(create);

    public string LanguageCode => languageCode;

    public bool IsAvailable => _resource.Use(annotator => annotator.IsAvailable);

    public int CountWords(string plainText) => _resource.Use(annotator => annotator.CountWords(plainText));

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context) =>
        _resource.Use(annotator => annotator.Annotate(plainText, context));

    public void ResetAvailability() => _resource.Reset();

    public void ConfigureIdleTimeout(TimeSpan timeout) => _resource.Configure(timeout);

    public void Dispose() => _resource.Dispose();
}
