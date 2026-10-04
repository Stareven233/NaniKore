using System.Globalization;
using NMeCab;
using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 开发期排查 CLI：--phonetics-probe 打印三语标注器在固定输入上的实际输出、判据数据规模与词典定位，
/// 用于黄金样例调试（不参与发布路径）。
/// </summary>
internal static class PhoneticsProbe
{
    public static void Run()
    {
        Console.WriteLine($"[定位] 词典根目录：{PhoneticDependencyPaths.PhoneticsRootDirectory}");
        foreach (var language in PhoneticDependencyManifest.SupportedLanguages)
        {
            Console.WriteLine($"[定位] {language} 完整：{PhoneticDependencyManifest.IsComplete(language)}，缺失文件 {PhoneticDependencyManifest.GetIncompleteFiles(language).Count}");
        }

        foreach (var language in new[] { "ja", "zh", "en" })
        {
            var profile = AnnotationProfile.Load(language);
            Console.WriteLine($"[判据] {language}: 打开={profile.Enabled}, 仅难词={profile.RareOnly}");
        }

        Console.WriteLine($"[判据] ja 日本語淘汰={AnnotationProfile.Load("ja").IsSkipped("日本語")}，出来る={AnnotationProfile.Load("ja").IsSkipped("出来る")}，躊躇={AnnotationProfile.Load("ja").IsSkipped("躊躇")}");
        Console.WriteLine($"[判据] zh 的={AnnotationProfile.Load("zh").IsSkipped("的")}，问题={AnnotationProfile.Load("zh").IsSkipped("问题")}，龃龉={AnnotationProfile.Load("zh").IsSkipped("龃龉")}，尴尬={AnnotationProfile.Load("zh").IsSkipped("尴尬")}");
        Console.WriteLine($"[判据] en the={AnnotationProfile.Load("en").IsSkipped("the")}，government={AnnotationProfile.Load("en").IsSkipped("government")}，ubiquitous={AnnotationProfile.Load("en").IsSkipped("ubiquitous")}");
        Console.WriteLine("[判据] 停用词条数（计数）："
            + $" ja={CountLines("stopwords-ja")}, zh={CountLines("stopwords-zh")}, en={CountLines("stopwords-en")},"
            + $" common-chars-ja={CountLines("common-chars-ja")}, common-chars-zh={CountLines("common-chars-zh")}");

        Console.WriteLine($"[arpabet] ubiquitous 码点：{(Dump(ArpabetIpaConverter.Convert("Y UW0 B IH1 K W IH0 T AH0 S")))}");
        Console.WriteLine($"[拼音] ri4={Dump(PinyinToneConverter.ConvertSyllable("ri4"))}, Ri4 Ben3={Dump(PinyinToneConverter.ConvertPhrase("Ri4 Ben3"))}");

        var registry = TextAnnotatorRegistry.CreateDefault();
        var samples = new (string Language, string Text)[]
        {
            ("ja", "日本語を勉強する日本語"),
            ("ja", "食べた"),
            ("ja", "買い出しに行く"),
            ("ja", "行きます"),
            ("ja", "躊躇した"),
            ("zh", "龃龉的滋味"),
            ("zh", "尴尬而不失礼貌"),
            ("zh", "魑魅魍魉横行"),
            ("zh", "饕餮大餐"),
            ("zh", "不行就去银行"),
            ("en", "The ubiquitous government"),
            ("en", "abandoned the field"),
            ("en", "Bob's device")
        };

        foreach (var (language, text) in samples)
        {
            var annotator = registry.GetAnnotator(language);
            var context = new AnnotationContext(AnnotationProfile.Load(language, rareOnly: false));
            var spans = annotator is null ? [] : annotator.Annotate(text, context);
            Console.WriteLine($"[{language}] available={annotator?.IsAvailable} {text} => {Dump(AnnotatedTextFormatter.Compose(text, spans))} (spans={spans.Count})");
        }

        if (registry.GetAnnotator("zh") is ITextAnnotator zhAnnotator)
        {
            DumpRawTagger(MeCabTagger.Create(PhoneticDependencyPaths.ChineseDictionaryDirectory, null), "龃龉不行就去银行", "zh");
            DumpRawTagger(MeCabTagger.Create(PhoneticDependencyPaths.ChineseDictionaryDirectory, null), "你好世界", "zh2");
        }
    }

    private static void DumpRawTagger(MeCabTagger tagger, string text, string label)
    {
        Console.WriteLine($"[raw-{label}] {Dump(text)}");
        foreach (var node in tagger.Parse(text))
        {
            var surface = node.Surface;
            if (string.IsNullOrEmpty(surface))
            {
                continue;
            }

            Console.WriteLine($"[raw-{label}] 词面={Dump(surface)} feature={Dump(node.Feature)} f0={Dump(node.GetFeatureAt(0))}");
            var parts = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var value = node.GetFeatureAt(i);
                parts.Add($"{i}:{(string.IsNullOrEmpty(value) ? "<空>" : value)}");
            }

            Console.WriteLine($"[raw-{label}]   各列：{string.Join(" | ", parts)}");
        }
    }

    private static int CountLines(string resourceName)
    {
        using var stream = typeof(AnnotationProfile).Assembly.GetManifestResourceStream($"NaniKore.Phonetics.Data.{resourceName}.txt");
        if (stream is null)
        {
            return -1;
        }

        var count = 0;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is not null)
        {
            count++;
        }

        return count;
    }

    private static string Dump(string text) =>
        string.Join(" ", text.Select(ch => ch > 0x7E ? $"U+{(int)ch:X4}" : ch.ToString(CultureInfo.InvariantCulture)));
}
