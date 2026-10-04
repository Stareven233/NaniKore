using NaniKore.Phonetics;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunPhoneticsTests()
    {
        TestAnnotationProfileAndDedupe();
        TestAnnotationFormatter();
        TestEnglishPipeline();
        TestPinyinToneConversion();
        TestRegistryLanguageRouting();
        TestPhoneticLanguageCatalog();
        TestPhoneticDictionaryStates();
        TestRegistryResetAvailability();
        TestJapaneseFuriganaDictionary();
        TestChinesePinyinDictionary();
        TestEnglishIpaDictionary();
    }

    /// <summary>语言清单是设置页卡片与源语言下拉的共同口径：代码、展示名、顺序必须稳定。</summary>
    private static void TestPhoneticLanguageCatalog()
    {
        Check(
            "三语清单代码与顺序",
            PhoneticLanguages.All.Select(language => language.Code).SequenceEqual(["ja", "en", "zh"]));
        Check(
            "展示名与源语言下拉口径一致",
            PhoneticLanguages.DisplayName("ja") == "日本語" &&
            PhoneticLanguages.DisplayName("en") == "English" &&
            PhoneticLanguages.DisplayName("zh") == "中文（简体）" &&
            PhoneticLanguages.DisplayName("ko") is null &&
            PhoneticLanguages.DisplayName(null) is null);
    }

    /// <summary>
    /// 按语言词典状态：Ready 必须与清单完整性判定一致（哈希即版本）；
    /// 未装配语言（如 ko）返回 Unsupported 而不是崩溃。
    /// </summary>
    private static void TestPhoneticDictionaryStates()
    {
        foreach (var language in PhoneticLanguages.All)
        {
            var state = PhoneticDependencyManifest.GetState(language.Code);
            Check(
                $"{language.Code} 词典状态与清单完整性一致",
                state == PhoneticDictionaryState.Ready == PhoneticDependencyManifest.IsComplete(language.Code) &&
                state is PhoneticDictionaryState.Ready or PhoneticDictionaryState.Missing or PhoneticDictionaryState.NeedsUpdate);
        }

        Check(
            "未装配语言返回 Unsupported",
            PhoneticDependencyManifest.GetState("ko") == PhoneticDictionaryState.Unsupported &&
            PhoneticDependencyManifest.GetState("") == PhoneticDictionaryState.Unsupported &&
            PhoneticDependencyManifest.GetFiles("ko").Count == 0);
    }

    /// <summary>下载落地后的热生效口径：ResetAvailability 清闩锁后重新探测，注音结果不变。</summary>
    private static void TestRegistryResetAvailability()
    {
        var registry = TextAnnotatorRegistry.CreateDefault();
        registry.ResetAvailability();
        registry.ResetAvailability("zh");
        Check(
            "重置可用性后英语注音不受影响",
            registry.Annotate("The ubiquitous government", "en").Count == 1);

        // 清闩锁只影响「是否重新探测词典」，不得改动注音行为本身
        var annotator = registry.GetAnnotator("en")!;
        var before = Annotated(annotator, "The ubiquitous government", new AnnotationContext(AnnotationProfile.Load("en")));
        annotator.ResetAvailability();
        annotator.ResetAvailability();
        var after = Annotated(annotator, "The ubiquitous government", new AnnotationContext(AnnotationProfile.Load("en")));
        Check(
            "重复重置可用性后注音结果不变",
            string.Equals(before, after, StringComparison.Ordinal));
    }

    private static void TestAnnotationProfileAndDedupe()
    {
        var ja = AnnotationProfile.Load("ja");
        Check("常用汉字表过滤：日语超高频汉字词不注", ja.IsSkipped("日本語"));
        Check("策展停用词过滤：出来る不注", ja.IsSkipped("出来る"));
        Check("表外汉字注：躊躇不淘汰", !ja.IsSkipped("躊躇"));
        Check("策展助词表：纯假名助词命中策展条目不注", ja.IsSkipped("の"));
        Check("纯假名普通词不经字符表淘汰", !ja.IsSkipped("ふりがな"));

        var zh = AnnotationProfile.Load("zh");
        Check("一级字表过滤：中文虚词不注", zh.IsSkipped("的"));
        Check("一级字表过滤：常用字整词不注", zh.IsSkipped("问题"));
        Check("表外汉字注：俳佪不淘汰（一级字表之外）", !zh.IsSkipped("俳佪"));
        Check("二级常用难词注：龃龉不淘汰（龃龉均表外）", !zh.IsSkipped("龃龉"));
        Check("二级常用难词注：饕餮不淘汰（餮为二级字）", !zh.IsSkipped("饕餮"));
        Check("一级字表边界：魑魅不淘汰（魑为二级字）", !zh.IsSkipped("魑魅"));
        Check("一级字表边界：尴尬仍不注（尴尬均为一级字，标准口径）", zh.IsSkipped("尴尬"));
        Check("一级字表边界：徘徊仍不注（徘徊均为一级字，标准口径）", zh.IsSkipped("徘徊"));
        Check("一级常用整词仍淘汰：日本", zh.IsSkipped("日本"));

        var en = AnnotationProfile.Load("en");
        Check("英语停用词过滤", en.IsSkipped("the") && en.IsSkipped("and"));
        Check("英语高频词过滤", en.IsSkipped("government"));
        Check("英语难词不淘汰", !en.IsSkipped("ubiquitous"));

        var disabled = AnnotationProfile.Load("ja", enabled: false);
        Check("总开关关闭时一律不注", disabled.IsSkipped("躊躇"));

        var context = new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false));
        Check("同篇同词形同读音只注首现", context.ShouldEmit("日本語", "にほんご") && !context.ShouldEmit("日本語", "にほんご"));
        Check("同篇同词形异读音补标", context.ShouldEmit("行", "ぎょう") && context.ShouldEmit("行", "こう"));
    }

    private static void TestAnnotationFormatter()
    {
        var text = "你好世界";
        var spans = new[] { new AnnotationSpan(0, 2, "nǐ hǎo"), new AnnotationSpan(2, 1, "shì") };
        Check(
            "区间拼装为底字[读音]",
            AnnotatedTextFormatter.Compose(text, spans) == "你好[nǐ hǎo]世[shì]界",
            $"expected=你好[nǐ hǎo]世[shì]界, actual={AnnotatedTextFormatter.Compose(text, spans)}");

        Check(
            "空区间原样返回",
            AnnotatedTextFormatter.Compose(text, []) == text,
            $"actual={AnnotatedTextFormatter.Compose(text, [])}");

        Check(
            "越界区间被安全忽略",
            AnnotatedTextFormatter.Compose(text, [new AnnotationSpan(0, 99, "x")]) == text,
            $"actual={AnnotatedTextFormatter.Compose(text, [new AnnotationSpan(0, 99, "x")])}");
    }

    private static void TestEnglishPipeline()
    {
        Check(
            "CMUdict ARPAbet 转 IPA（应激位）",
            ArpabetIpaConverter.Convert("Y UW0 B IH1 K W IH0 T AH0 S") == "juːbˈɪkwɪtəs",
            $"actual={ArpabetIpaConverter.Convert("Y UW0 B IH1 K W IH0 T AH0 S")}");

        Check(
            "词形还原候选覆盖屈折",
            EnglishLemmatizer.Candidates("studies").Contains("study") &&
            EnglishLemmatizer.Candidates("abandoned").Contains("abandon") &&
            EnglishLemmatizer.Candidates("running").Contains("run"),
            $"studies -> {string.Join(",", EnglishLemmatizer.Candidates("studies"))}");
    }

    private static void TestPinyinToneConversion()
    {
        Check("声调数字转符号", PinyinToneConverter.ConvertSyllable("ri4") == "rì", $"actual={PinyinToneConverter.ConvertSyllable("ri4")}");
        Check("多音节短语", PinyinToneConverter.ConvertPhrase("Ri4 Ben3") == "rì běn", $"actual={PinyinToneConverter.ConvertPhrase("Ri4 Ben3")}");
        Check("ou 标 o 位", PinyinToneConverter.ConvertSyllable("hao3") == "hǎo", $"actual={PinyinToneConverter.ConvertSyllable("hao3")}");
        Check("轻声去数字", PinyinToneConverter.ConvertSyllable("ma5") == "ma", $"actual={PinyinToneConverter.ConvertSyllable("ma5")}");
        Check("v 归一化为 ü", PinyinToneConverter.ConvertSyllable("lü3") == "lǚ", $"actual={PinyinToneConverter.ConvertSyllable("lü3")}");
    }

    private static void TestRegistryLanguageRouting()
    {
        Check("源语言归一化：日语", TextAnnotatorRegistry.NormalizeLanguageCode("日本語") == "ja");
        Check("源语言归一化：英语", TextAnnotatorRegistry.NormalizeLanguageCode("English") == "en");
        Check("源语言归一化：简体中文", TextAnnotatorRegistry.NormalizeLanguageCode("中文（简体）") == "zh");
        Check("自动检测归一化为空", TextAnnotatorRegistry.NormalizeLanguageCode("自动检测") is null);

        var registry = TextAnnotatorRegistry.CreateDefault();
        var unsupported = "적응 불가능한 텍스트";
        Check(
            "登记表不支持的语言原样返回（不误注音）",
            registry.Annotate(unsupported).Count == 0,
            $"actual={string.Join("|", registry.Annotate(unsupported))}");

        var pinned = registry.Annotate("The ubiquitous government", "en");
        Check(
            "手选英语：拉丁词元按英语注音",
            pinned.Count == 1,
            $"actual={AnnotatedTextFormatter.Compose("The ubiquitous government", pinned)}");
    }

    private static void TestJapaneseFuriganaDictionary()
    {
        var annotator = new JapaneseFuriganaAnnotator();
        if (!annotator.IsAvailable)
        {
            Skip("ja 词典未就绪（deps/phonetics/ja），跳过日语金样本");
            return;
        }

        var defaultContext = new AnnotationContext(AnnotationProfile.Load("ja"));
        var annotated = Annotated(annotator, "躊躇した", defaultContext);
        Check("日语金样本：纯汉字日语词注振假名", annotated == "躊躇[ちゅうちょ]した", $"expected=躊躇[ちゅうちょ]した, actual={annotated}");

        var allContext = new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false));
        Check(
            "送假名切分：食べた",
            Annotated(annotator, "食べた", allContext) == "食[た]べた",
            $"actual={Annotated(annotator, "食べた", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)))}");
        Check(
            "送假名切分：買い出しに行く",
            Annotated(annotator, "買い出しに行く", allContext) == "買い出[かいだ]しに行く",
            $"actual={Annotated(annotator, "買い出しに行く", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)))}");
        Check(
            "送假名切分：行きます",
            Annotated(annotator, "行きます", allContext) == "行[い]きます",
            $"actual={Annotated(annotator, "行きます", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)))}");
        Check(
            "同篇重复词形只注首现",
            Annotated(annotator, "日本語を勉強する日本語", allContext) ==
                "日本語[にほんご]を勉強[べんきょう]する日本語",
            $"actual={Annotated(annotator, "日本語を勉強する日本語", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)))}");
        Check(
            "中英混排：拉丁词元不被日语标注器认领",
            annotator.Annotate("これはiPhoneです", allContext).Count == 0,
            $"actual={Annotated(annotator, "これはiPhoneです", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)))}");
    }

    private static void TestChinesePinyinDictionary()
    {
        var annotator = new ChinesePinyinAnnotator();
        if (!annotator.IsAvailable)
        {
            Skip("zh 词典未就绪（deps/phonetics/zh），跳过中文金样本");
            return;
        }

        var defaultContext = new AnnotationContext(AnnotationProfile.Load("zh"));
        var annotated = Annotated(annotator, "俳佪", defaultContext);
        Check(
            "中文金样本：表外字注拼音，常用字按仅难词过滤",
            annotated == "俳佪[pái huái]",
            $"expected=俳佪[pái huái], actual={annotated}");

        // 二级常用难词：一级字表覆盖不到，仅难词模式下也应注音（含词典查询与过滤联动）
        Check(
            "中文金样本：龃龉注拼音",
            Annotated(annotator, "龃龉", defaultContext) == "龃龉[jǔ yǔ]",
            $"actual={Annotated(annotator, "龃龉", defaultContext)}");
        Check(
            "中文金样本：魑魅魍魉注拼音，一级字后缀不注",
            Annotated(annotator, "魑魅魍魉横行", defaultContext) == "魑魅魍魉[chī mèi wǎng liǎng]横行",
            $"actual={Annotated(annotator, "魑魅魍魉横行", defaultContext)}");

        var allContext = new AnnotationContext(AnnotationProfile.Load("zh", rareOnly: false));
        var polyphone = Annotated(annotator, "不行就去银行", allContext);
        Check(
            "中文多音字按词面区分",
            polyphone == "不行[bù xíng]就去银行[yín háng]",
            $"actual={polyphone}");
    }

    private static void TestEnglishIpaDictionary()
    {
        var annotator = new EnglishIpaAnnotator();
        if (!annotator.IsAvailable)
        {
            Skip("en 词典未就绪（deps/phonetics/en），跳过英语金样本");
            return;
        }

        var defaultProfile = AnnotationProfile.Load("en");
        var ubiquitous = Annotated(annotator, "The ubiquitous government", new AnnotationContext(defaultProfile));
        Check(
            "英语金样本：难词注 IPA，停用/高频词不注",
            ubiquitous == "The ubiquitous[juːbˈɪkwɪtəs] government",
            $"actual={ubiquitous}");
        var inflection = Annotated(annotator, "abandoned the field", new AnnotationContext(defaultProfile));
        Check(
            "英语金样本：屈折词被停用词包围仍只注自身",
            inflection == "abandoned[əbˈændənd] the field",
            $"actual={inflection}");
        var possessive = annotator.Annotate("Bob's device", new AnnotationContext(defaultProfile));
        Check(
            "英语金样本：所有格只给词干注音",
            possessive is [{ Start: 0, Length: 3 }, ..],
            $"actual={AnnotatedTextFormatter.Compose("Bob's device", possessive)}");
    }

    /// <summary>黄金样例辅助：一次标注一次拼装（AnnotationContext 的去重是单篇状态，重复调用会清空结果）。</summary>
    private static string Annotated(ITextAnnotator annotator, string text, AnnotationContext context)
    {
        var spans = annotator.Annotate(text, context);
        return AnnotatedTextFormatter.Compose(text, spans);
    }

    private static void Skip(string reason) => Console.WriteLine($"SKIP: {reason}");
}
