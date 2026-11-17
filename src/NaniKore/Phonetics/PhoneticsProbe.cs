using System.Globalization;
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
        Console.WriteLine($"[定位] 数据根目录：{PhoneticDataPaths.PhoneticsRootDirectory}");
        foreach (var pack in NativePhoneticManifest.Packs)
        {
            var failure = NativePhoneticPackVerifier.Verify(pack, NativePhoneticManifest.GetPackDirectory(pack.Language), out _);
            Console.WriteLine($"[定位] native {pack.Language} 完整：{failure == NativePhoneticPackageFailure.None}（{failure}）");
        }

        Console.WriteLine($"[定位] zh 数据完整：{ChinesePinyinData.IsComplete()}（{ChinesePinyinData.GetState()}）");

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
        Console.WriteLine($"[判据] 短文本全注阈值={AnnotationProfile.ShortTextWordThreshold}：ja 3词={AnnotationProfile.Load("ja", wordCount: 3).AnnotateAll}，ja 12词={AnnotationProfile.Load("ja", wordCount: 12).AnnotateAll}");

        Console.WriteLine($"[拼音] ri4={Dump(PinyinToneConverter.ConvertSyllable("ri4"))}, Ri4 Ben3={Dump(PinyinToneConverter.ConvertPhrase("Ri4 Ben3"))}");

        var registry = TextAnnotatorRegistry.CreateDefault();
        var samples = new (string Language, string Text)[]
        {
            ("ja", "日本語を勉強する日本語"),
            ("ja", "日本語を勉強する"),
            ("ja", "東京へ行く"),
            ("ja", "今日はいい天気です"),
            ("ja", "凍り付く"),
            ("ja", "食べた"),
            ("ja", "お祝い"),
            ("ja", "買い出しに行く"),
            ("ja", "行きます"),
            ("ja", "お茶を飲む"),
            ("ja", "睡眠薬"),
            ("ja", "持ち帰り"),
            ("ja", "A株100株と3.14円"),
            ("ja", "😀東京\n\u3000大阪😀"),
            ("ja", "躊躇した"),
            ("ja", "薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬"),
            ("ja", "先生は日本語で問題を説明して、生徒たちは静かに聞いていた。その後みんなで復習を始めた"),
            ("zh", "龃龉的滋味"),
            ("zh", "日本"),
            ("zh", "尴尬而不失礼貌"),
            ("zh", "魑魅魍魉横行"),
            ("zh", "饕餮大餐"),
            ("zh", "不行就去银行"),
            ("en", "The ubiquitous government"),
            ("en", "The ubiquitous government and treason"),
            ("en", "abandoned the field"),
            ("en", "treason"),
            ("en", "Bob's device"),
            // 011 整词候选与实际阅读样本：短文本全注，可直接看 IPA 与跨词对齐
            ("en", "The water is better in the city."),
            ("en", "They will record a new record tomorrow."),
            ("en", "I read a book yesterday, but please read it again."),
            ("en", "The 2026 price is $50 for Dr. Smith.")
        };

        foreach (var (language, text) in samples)
        {
            var annotator = registry.GetAnnotator(language);
            var wordCount = annotator?.CountWords(text) ?? 0;
            // 生产口径：候选词不足阈值时短文本全注，否则按设置里的仅难词过滤
            var profile = AnnotationProfile.Load(language, wordCount: wordCount);
            var spans = annotator is null ? [] : annotator.Annotate(text, new AnnotationContext(profile));
            Console.WriteLine($"[{language}] available={annotator?.IsAvailable} 词数={wordCount} 全注={profile.AnnotateAll} {text} => {Dump(AnnotatedTextFormatter.Compose(text, spans))} (spans={spans.Count})");

            // 黄金样例调试视角：关闭难词过滤后的原始候选（停用词仍生效），与生产口径对照看过滤吃掉了什么
            var rawSpans = annotator is null ? [] : annotator.Annotate(text, new AnnotationContext(AnnotationProfile.Load(language, rareOnly: false)));
            Console.WriteLine($"[{language}]   仅难词=关 {text} => {Dump(AnnotatedTextFormatter.Compose(text, rawSpans))} (spans={rawSpans.Count})");

            // 英语整词候选（issue 011）：直观看本句读音与其他已收录读音的排列
            foreach (var span in spans)
            {
                if (span.WholeWord is { } wholeWord)
                {
                    Console.WriteLine($"[{language}]   候选 {text.Substring(span.Start, span.Length)} => {string.Join(" | ", wholeWord.Candidates)}");
                }
            }
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
