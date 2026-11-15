using NaniKore.Phonetics;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

/// <summary>
/// 英语原生注音与整词候选（issue 011）自测：全部走生产同一链——原生 Misaki 语言包 + 宿主信号切分 + 过滤判据。
/// 口径按 epic 2026-11-15 决定：直接采用 Misaki 本句选音与已收录候选，允许 ɾ/ʔ 等美音变体；
/// read/lead 的上游误选与缺项不是回归，但要逐条如实写成已知限制，不能被写成语境消歧已完成。
/// 短文本（不足 10 词）走全注豁免以避开停用/难词过滤，只验证 IPA、区间与候选本身。
/// </summary>
internal static partial class SelfTest
{
    private static void TestEnglishNativeAnnotation()
    {
        using var host = new NativePhoneticPackHost();
        using var registry = TextAnnotatorRegistry.Create(host);
        var annotator = registry.GetAnnotator("en");
        if (annotator is null || !annotator.IsAvailable)
        {
            Skip("en 原生语言包未就绪（deps/phonetics/misaki/en），跳过英语原生注音金样本");
            return;
        }

        Check(
            "英语 provider 已切到原生语言包（与日语同一套宿主槽位）",
            ReferenceEquals(annotator, host.GetAnnotator("en")) &&
            annotator.LanguageCode == "en");

        // G2：新口径的 IPA 适配——闪音/喉塞音照原样展示，不再因严格转换器漏注常用词。
        Check(
            "G2 美音 IPA：water/better/city 保留闪音 ɾ 且有有效注音",
            Compose(registry, "The water is better in the city.") ==
            "The[ðə] water[ˈwɔːɾ.ər] is[ɪz] better[ˈbeɾ.ər] in[ɪn] the city[ˈsɪɾ.iː].",
            $"actual={Compose(registry, "The water is better in the city.")}");

        Check(
            "英语金样本：难词注词典式 IPA、屈折词与所有格按原文区间标注",
            Compose(registry, "abandoned the field") == "abandoned[ə.ˈbæn.dənd] the[ðə] field[ˈfiːld]" &&
            Compose(registry, "Bob's device") == "Bob[ˈbɑːb]'s device[də.ˈvaɪs]" &&
            Compose(registry, "treason") == "treason[ˈtriː.zən]",
            $"actual={Compose(registry, "abandoned the field")} | {Compose(registry, "Bob's device")} | {Compose(registry, "treason")}");

        // G5：本句选音——同句两处 read 读音不同，都要注；同词同音仍然只注首现。
        Check(
            "G5 本句选音：read 当场/过去两处读音分别标注",
            Compose(registry, "I read a book yesterday, but please read it again.") ==
            "I read[ˈred] a book[ˈbʊk] yesterday[ˈjes.tər.ˌdeɪ], but[ˌbʌt] please[ˈpliːz] read[ˈriːd] it[ɪt] again[ə.ˈɡen].",
            $"actual={Compose(registry, "I read a book yesterday, but please read it again.")}");

        Check(
            "G5 本句选音：record 动词/名词按本句读音分别标注",
            Compose(registry, "They will record a new record tomorrow.") ==
            "They[ˌðeɪ] will[wɪl] record[rə.ˈkɔːrd] a new[ˈnuː] record[ˈrek.ərd] tomorrow[tə.ˈmɑː.roʊ].",
            $"actual={Compose(registry, "They will record a new record tomorrow.")}");

        Check(
            "同词同音去重保留：同一读音的 lead 只注首现",
            Compose(registry, "He can lead the way with a lead pipe.") ==
            "He[ˌhiː] can[kæn] lead[ˈliːd] the[ðə] way[ˈweɪ] with[wɪð] a lead pipe[ˈpaɪp].",
            $"actual={Compose(registry, "He can lead the way with a lead pipe.")}");

        // 数字、货币与标点不注音；明确收录的缩写照常注音。
        const string priceText = "The 2026 price is $50 for Dr. Smith.";
        var priceSpans = registry.Annotate(priceText, "en");
        Check(
            "数字与货币不进底字区间，Dr. 按词典收录读法注音",
            Compose(registry, priceText) == "The[ðə] 2026 price[ˈpraɪs] is[ɪz] $50 for[fɔːr] Dr.[ˈdɑːk.tər] Smith[ˈsmɪθ]." &&
            priceSpans.All(span => !priceText.Substring(span.Start, span.Length).Any(ch => char.IsAsciiDigit(ch) || ch == '$')),
            $"actual={AnnotatedTextFormatter.Compose(priceText, priceSpans)}");

        const string acronymText = "NASA and U.S. and ZZQX.";
        var acronymSpans = registry.Annotate(acronymText, "en");
        var nasa = acronymSpans.FirstOrDefault(span => Span(acronymText, span) == "NASA");
        var unknownAcronym = acronymSpans.FirstOrDefault(span => Span(acronymText, span) == "ZZQX");
        Check(
            "缩写：明确收录的 NASA 有读音，区间只覆盖缩写本身",
            nasa.Reading == "ˈnæs.ə" && nasa.Length == 4,
            $"actual={AnnotatedTextFormatter.Compose(acronymText, acronymSpans)}");
        Check(
            "缩写：未收录全大写串只拿到引擎逐字母拼名（如实记录，不作为语境能力）",
            unknownAcronym.Reading == "ˌziː.ˌziː.ˌkjuː.ˈeks",
            $"actual={AnnotatedTextFormatter.Compose(acronymText, acronymSpans)}");

        // 原文保真：emoji、制表符、换行与重复词按 UTF-16 定位，区间不重叠，拼装后可无损还原。
        const string mixed = "😀 read read.\tNASA\r\nread";
        var mixedSpans = registry.Annotate(mixed, "en");
        var mixedComposed = AnnotatedTextFormatter.Compose(mixed, mixedSpans);
        Check(
            "emoji/制表符/换行：区间按 UTF-16 定位且原文无损还原",
            mixedComposed == "😀 read[ˈriːd] read[ˈred].\tNASA[ˈnæs.ə]\r\nread[ˈriːd]" &&
            RubyTextParser.ToPlainText(mixedComposed) == mixed &&
            mixedSpans.Zip(mixedSpans.Skip(1)).All(pair => pair.First.Start + pair.First.Length <= pair.Second.Start),
            $"actual={mixedComposed}");

        // 不识别 TTS markdown 读音指令，也不按指令改写原文。
        const string markdown = "  [record](/zzz/) record\nread";
        Check(
            "TTS markdown 读音指令被忽略：既有原样保留，真实词元按引擎读音注音",
            Compose(registry, markdown) == "  [record](/zzz/) record[ˈrek.ərd]\nread[ˈriːd]",
            $"actual={AnnotatedTextFormatter.Compose(markdown, registry.Annotate(markdown, "en"))}");

        Check(
            "字面方括号区段不注音（上轮残留注音/代码下标不二次标注）",
            Compose(registry, "[ read ] read") == "[ read ] read[ˈriːd]",
            $"actual={AnnotatedTextFormatter.Compose("[ read ] read", registry.Annotate("[ read ] read", "en"))}");

        // 停用/难词过滤与 10 词阈值：长文本恢复过滤，短文本全注（沿用旧口径，不因换 provider 而变）。
        const string longText = "The ubiquitous government and treason are mentioned in the second paragraph of this document.";
        var longSpans = registry.Annotate(longText, "en");
        Check(
            "长文本：停用/高频词被过滤、难词照常注音",
            longSpans.Any(span => Span(longText, span) == "ubiquitous") &&
            longSpans.Any(span => Span(longText, span) == "treason") &&
            longSpans.All(span => Span(longText, span) is not ("The" or "the" or "and" or "are" or "of" or "in")),
            $"actual={AnnotatedTextFormatter.Compose(longText, longSpans)}");

        // 缺包/离线：指向不存在的目录时只能退化为纯文本，不回退旧 provider、不抛异常。
        using var missingHost = new NativePhoneticPackHost(
            packDirectory: language => Path.Combine(Path.GetTempPath(), "nani-kore-en-missing", language));
        var missing = missingHost.GetAnnotator("en")!;
        Check(
            "英语缺包：状态缺失、不可用、注音为空且不抛异常",
            missingHost.GetState("en") == NativePhoneticPackState.Missing &&
            !missing.IsAvailable &&
            missing.Annotate("The water is better in the city.", new AnnotationContext(AnnotationProfile.Load("en"))).Count == 0 &&
            missing.CountWords("The water is better in the city.") == 0);
    }

    /// <summary>
    /// G5 整词候选：默认读音排首位、词义重音差别保留、句重音伪多读折叠，
    /// 上游误选/缺项写成已知限制而不是回归。
    /// </summary>
    private static void TestEnglishNativeWholeWordCandidates()
    {
        Check(
            "候选折叠：句重音变化不算异读（They ˌðeɪ/ðeɪ）",
            EnglishReadingCandidates.Collapse("ˌðeɪ", ["ˌðeɪ", "ðeɪ"]).SequenceEqual(["ˌðeɪ"]));
        Check(
            "候选折叠：元音不同的强弱读法是真异读（the ðə/ðiː）",
            EnglishReadingCandidates.Collapse("ðə", ["ðə", "ðiː"]).SequenceEqual(["ðə", "ðiː"]));
        Check(
            "候选折叠：精确重复剔除、首项始终是本句读音",
            EnglishReadingCandidates.Collapse("ˈriːd", ["riːd", "ˈriːd", "ˈred"]).SequenceEqual(["ˈriːd", "ˈred"]));
        Check(
            "候选折叠：词义重音差别不被折叠（record 动词/名词）",
            EnglishReadingCandidates.Collapse("rə.ˈkɔːrd", ["rə.ˈkɔːrd", "ˈrek.ərd"]).SequenceEqual(["rə.ˈkɔːrd", "ˈrek.ərd"]));

        using var host = new NativePhoneticPackHost();
        using var registry = TextAnnotatorRegistry.Create(host);
        var annotator = registry.GetAnnotator("en");
        if (annotator is null || !annotator.IsAvailable)
        {
            Skip("en 原生语言包未就绪（deps/phonetics/misaki/en），跳过英语整词候选断言");
            return;
        }

        const string recordText = "They will record a new record tomorrow.";
        var recordSpans = registry.Annotate(recordText, "en");
        var verbRecord = recordSpans.First(span => Span(recordText, span) == "record" && span.Start == 10);
        var nounRecord = recordSpans.First(span => span.Start == 23);
        Check(
            "整词候选：record 动词处首项为本句读音，另一读音完整保留",
            verbRecord.WholeWord is { } verb &&
            verb.Start == verbRecord.Start && verb.Length == 6 &&
            verb.Reading == verbRecord.Reading && verb.Reading == "rə.ˈkɔːrd" &&
            verb.Candidates.SequenceEqual(["rə.ˈkɔːrd", "ˈrek.ərd"]),
            $"actual={verbRecord.Reading} {string.Join('|', verbRecord.WholeWord?.Candidates ?? [])}");
        Check(
            "整词候选：record 名词处按本句读音重排首位",
            nounRecord.WholeWord is { } noun &&
            noun.Candidates.SequenceEqual(["ˈrek.ərd", "rə.ˈkɔːrd"]),
            $"actual={nounRecord.Reading} {string.Join('|', nounRecord.WholeWord?.Candidates ?? [])}");
        Check(
            "整词候选：只有句重音变化时不再挂多读（They/will 无候选组）",
            recordSpans.First(span => span.Start == 0).WholeWord is null &&
            recordSpans.First(span => span.Start == 5).WholeWord is null);
        Check(
            "整词候选：单读音词不产生候选组（treason）",
            registry.Annotate("treason", "en").Single(span => span.Length == 7).WholeWord is null);

        const string readText = "I read a book yesterday, but please read it again.";
        var readSpans = registry.Annotate(readText, "en");
        Check(
            "整词候选：read 的两处读音都能在 hover 里看到（/red/ 与 /riːd/）",
            readSpans.First(span => span.Start == 2).WholeWord is { } past && past.Candidates.SequenceEqual(["ˈred", "ˈriːd"]) &&
            readSpans.First(span => span.Start == 36).WholeWord is { } present && present.Candidates.SequenceEqual(["ˈriːd", "ˈred"]));

        // 已知限制（不阻塞关闭，也不能写成已解决）：上游选音与缺项如实记录。
        var everyDay = registry.Annotate("I read every day.", "en").First(span => span.Start == 2);
        Check(
            "已知限制：I read every day. 上游选 /red/（现在时误选），候选里仍保留 /riːd/",
            everyDay.Reading == "ˈred" && everyDay.WholeWord is { } everyDayCandidates && everyDayCandidates.Candidates.Contains("ˈriːd"),
            $"actual={everyDay.Reading} {string.Join('|', everyDay.WholeWord?.Candidates ?? [])}");

        var leadText = "She has the lead role.";
        var leadSpans = registry.Annotate(leadText, "en");
        var lead = leadSpans.Single(span => Span(leadText, span) == "lead");
        Check(
            "已知限制：lead 的金属义 /led/ 不在词典候选里，本轮如实缺项、不凭空生成",
            lead.Reading == "ˈliːd" && lead.WholeWord is null,
            $"actual={lead.Reading} {string.Join('|', lead.WholeWord?.Candidates ?? [])}");
    }

    /// <summary>原文切片（断言里反复用，避免每处手写 Substring）。</summary>
    private static string Span(string text, AnnotationSpan span) => text.Substring(span.Start, span.Length);

    /// <summary>按生产口径（自动词数 → 短文本全注豁免）走注册表注音并拼装。</summary>
    private static string Compose(TextAnnotatorRegistry registry, string text) =>
        AnnotatedTextFormatter.Compose(text, registry.Annotate(text, "en"));
}
