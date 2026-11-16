using NaniKore.Phonetics;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunPhoneticsTests()
    {
        TestAnnotationProfileAndDedupe();
        TestAnnotationShortTextWordCount();
        TestAnnotationFormatter();
        TestIpaTranscription();
        TestEnglishPipeline();
        TestCmuDictionarySourceParser();
        TestCmuDictionaryCandidateIndex();
        TestPinyinToneConversion();
        TestChineseStructuredBoundaries();
        TestChinesePinyinExternalData();
        TestRegistryLanguageRouting();
        TestPhoneticLanguageCatalog();
        TestPhoneticDictionaryStates();
        TestRegistryResetAvailability();
        TestAnnotationPerformanceBudget();
        TestJapaneseWrittenKana();
        TestEnglishNativeAnnotation();
        TestEnglishNativeWholeWordCandidates();
        TestChinesePinyinDictionary();
        TestEnglishCandidateDatabase();
        RunNativePhoneticPackTests();
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

    /// <summary>词数统计与短文本全注豁免的输入口径：各标注器按自己的分词口径数候选词，阈值边界由 AnnotationProfile 裁决。</summary>
    private static void TestAnnotationShortTextWordCount()
    {
        Check("短文本全注阈值为 10", AnnotationProfile.ShortTextWordThreshold == 10);

        var registry = TextAnnotatorRegistry.CreateDefault();
        var english = registry.GetAnnotator("en");
        if (english is null || !english.IsAvailable)
        {
            Skip("en 词典未就绪（deps/phonetics/en），跳过英语词数与短语边界金样本");
        }
        else
        {
            Check(
                "英语词数：拉丁词元计数，单字母不计",
                english.CountWords("The ubiquitous government and treason") == 5 &&
                english.CountWords("I am a doctor") == 2 &&
                english.CountWords("Bob's device") == 2);

            var nineWords = "the quick brown fox jumps over the lazy dog";
            var tenWords = "the quick brown fox jumps over the lazy dog again";
            var nine = AnnotatedTextFormatter.Compose(nineWords, registry.Annotate(nineWords, "en"));
            var ten = AnnotatedTextFormatter.Compose(tenWords, registry.Annotate(tenWords, "en"));
            Check(
                "英语短语边界：9 词全注（停用词也注），10 词起回到仅难词",
                nine.StartsWith("the[", StringComparison.Ordinal) && !ten.Contains("the[", StringComparison.Ordinal),
                $"nine={nine} | ten={ten}");
        }

        var japanese = registry.GetAnnotator("ja");
        if (japanese is null || !japanese.IsAvailable)
        {
            Skip("ja 原生语言包未就绪（deps/phonetics/misaki/ja），跳过日语词数金样本");
        }
        else
        {
            // 分词口径换成 UniDic：日本語 = 日本 + 語，比 IPAdic 多一个候选词（issue 010 记录的差异）。
            Check(
                "日语词数：含汉字词面计数，纯假名与拉丁不计",
                japanese.CountWords("日本語を勉強する") == 3 &&
                japanese.CountWords("躊躇した") == 1 &&
                japanese.CountWords("これはiPhoneです") == 0 &&
                japanese.CountWords("薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬。") >= AnnotationProfile.ShortTextWordThreshold);
        }

        var chinese = registry.GetAnnotator("zh");
        if (chinese is null || !chinese.IsAvailable)
        {
            Skip("zh 词典未就绪（deps/phonetics/zh），跳过中文词数金样本");
        }
        else
        {
            Check(
                "中文词数：含汉字词面计数",
                chinese.CountWords("日本") == 1 &&
                chinese.CountWords("不行就去银行") == 4);
        }
    }

    /// <summary>按语言词典状态：三语一致，Ready 必须与清单完整性判定一致（哈希即版本）；未装配语言（如 ko）返回 Unsupported 而不是崩溃。</summary>
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
        Check(
            "三语都有下载清单，中文不再特例",
            PhoneticLanguages.All.All(language => PhoneticDependencyManifest.GetFiles(language.Code).Count > 0) &&
            PhoneticDependencyManifest.GetFiles(PhoneticDependencyManifest.ChineseCode)
                .Select(file => file.RelativePath)
                .SequenceEqual(["zh/zh_pinyin.txt.gz"]));
    }

    /// <summary>
    /// 中文拼音数据外置与按需下载（issue 012）：数据只在 `deps/phonetics/zh/zh_pinyin.txt.gz`，
    /// 缺失时纯文本降级（引擎为 null、区间为空、不抛异常），文件就绪时行为与内嵌版逐字一致；
    /// 闲置释放后重新读盘重建。
    /// </summary>
    private static void TestChinesePinyinExternalData()
    {
        var dataFile = PhoneticDependencyPaths.ChinesePinyinDataFile;
        var fileHash = File.Exists(dataFile)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dataFile)))
            : "<missing>";
        Check(
            "中文拼音数据按 deps 清单登记（哈希即版本）",
            PhoneticDependencyManifest.GetFiles(PhoneticDependencyManifest.ChineseCode) is
                [{ RelativePath: "zh/zh_pinyin.txt.gz", Sha256: var registered }] &&
            (fileHash == "<missing>" || fileHash == registered),
            $"file={fileHash}");

        // 缺包口径：不存在的路径必须安全退化为纯文本，不能抛异常
        var missingPath = Path.Combine(Path.GetTempPath(), "nani-kore-zh-missing", "zh_pinyin.txt.gz");
        Check(
            "缺包：引擎对不存在的文件返回 null、不抛异常",
            ChinesePinyinEngine.TryLoad(missingPath) is null &&
            ChinesePinyinEngine.TryLoad(string.Empty) is null);

        using var annotator = new ChinesePinyinAnnotator();
        if (!annotator.IsAvailable)
        {
            Check(
                "缺包：标注器不可用、注音为空、词数为 0",
                annotator.Annotate("不行就去银行", new AnnotationContext(AnnotationProfile.Load("zh"))).Count == 0 &&
                annotator.CountWords("不行就去银行") == 0);
            Skip("zh 拼音数据未就绪（deps/phonetics/zh），跳过中文金样本与冷/热基线");
            return;
        }

        // 每次装载新开一条文件流独立解析，旧实例不影响新实例
        var first = ChinesePinyinEngine.TryLoad(dataFile);
        var second = ChinesePinyinEngine.TryLoad(dataFile);
        Check(
            "引擎每次从磁盘重新装载，旧实例不影响新实例",
            first is not null && second is not null && !ReferenceEquals(first, second) &&
            first.Segment("银行") is [{ Syllables: [{ Reading: "yín" }, { Reading: "háng" }] }]);

        // 闲置释放后重建：重新读盘，注音结果不变（不把数据文件常驻等同于词表常驻）
        var before = Annotated(annotator, "不行就去银行", new AnnotationContext(AnnotationProfile.Load("zh", rareOnly: false)));
        annotator.Dispose();
        annotator.ResetAvailability();
        var after = Annotated(annotator, "不行就去银行", new AnnotationContext(AnnotationProfile.Load("zh", rareOnly: false)));
        Check(
            "释放后重新读盘，注音结果不变",
            before == "不行[bù xíng]就去银行[yín háng]" && after == before,
            $"before={before}, after={after}");

        // 冷启动/热态/重建时长与宿主内存增量：只打印实测值供 issue 记录，不断言时间阈值。
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var coldWatch = System.Diagnostics.Stopwatch.StartNew();
        var coldEngine = ChinesePinyinEngine.TryLoad(dataFile);
        coldWatch.Stop();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var warmWatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            _ = coldEngine?.Segment("不行就去银行");
        }

        warmWatch.Stop();
        var rebuildWatch = System.Diagnostics.Stopwatch.StartNew();
        var rebuiltEngine = ChinesePinyinEngine.TryLoad(dataFile);
        rebuildWatch.Stop();
        Console.WriteLine(
            $"[zh] 文件 {new FileInfo(dataFile).Length / 1024.0:F0}KB cold={coldWatch.Elapsed.TotalMilliseconds:F0}ms " +
            $"warm20={warmWatch.Elapsed.TotalMilliseconds:F1}ms rebuild={rebuildWatch.Elapsed.TotalMilliseconds:F0}ms " +
            $"托管堆增量={(heapAfter - heapBefore) / (1024 * 1024)}MB 当前堆={GC.GetTotalMemory(false) / (1024 * 1024)}MB 工作集={Environment.WorkingSet / (1024 * 1024)}MB");
        Check("重建的新引擎读音与冷加载一致", rebuiltEngine?.Segment("银行") is [{ Syllables: [{ Reading: "yín" }, { Reading: "háng" }] }]);
    }

    /// <summary>
    /// 性能预算（issue 008 Phase 4 验收）：全程 CPU、无网络。词典冷启动发生在首次调用，
    /// 故先各做一次预热；正式计时的对象是稳态单次注音（结果面板单次叠加的真实成本）。
    /// 预算放宽到 50ms/次——日常日语句子实测个位数毫秒，留一个数量级余量防抖。
    /// </summary>
    private static void TestAnnotationPerformanceBudget()
    {
        var registry = TextAnnotatorRegistry.CreateDefault();
        var warmups = new (string Language, string Text)[]
        {
            ("ja", "日本語を勉強する"),
            ("zh", "不行就去银行"),
            ("en", "The ubiquitous government"),
        };

        foreach (var (language, text) in warmups)
        {
            // 只重置本语言：注音器已换成原生语言包（010），重置全部会让其他语言把日语再拆一遍冷加载，
            // 1.3s 的冷启动就会稀里糊涂地跑进计时区。
            registry.ResetAvailability(language);
            _ = registry.GetAnnotator(language)?.Annotate(text, new AnnotationContext(AnnotationProfile.Load(language)));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (language, text) in warmups)
        {
            var profile = AnnotationProfile.Load(language);
            for (var i = 0; i < 20; i++)
            {
                _ = registry.GetAnnotator(language)?.Annotate(text, new AnnotationContext(profile));
            }
        }

        stopwatch.Stop();
        Check(
            $"three-language steady-state annotation stays within the CPU budget (60 calls in {stopwatch.ElapsedMilliseconds} ms)",
            stopwatch.ElapsedMilliseconds < 1000);
    }

    /// <summary>下载落地后的热生效口径：ResetAvailability 清闩锁后重新探测，注音结果不变。</summary>
    private static void TestRegistryResetAvailability()
    {
        var registry = TextAnnotatorRegistry.CreateDefault();
        registry.ResetAvailability();
        registry.ResetAvailability("zh");

        var english = registry.GetAnnotator("en");
        if (english is null || !english.IsAvailable)
        {
            // 注音词典是按需下载的可选依赖：没就绪时跳过，不报假失败（与金样本测试同一口径）。
            Skip("en 词典未就绪（deps/phonetics/en），跳过后半段英语断言");
            return;
        }

        Check(
            "重置可用性后英语注音不受影响",
            registry.Annotate("The ubiquitous government", "en").Count == 3);

        // 清闩锁只影响「是否重新探测词典」，不得改动注音行为本身
        var before = Annotated(english, "The ubiquitous government", new AnnotationContext(AnnotationProfile.Load("en")));
        english.ResetAvailability();
        english.ResetAvailability();
        var after = Annotated(english, "The ubiquitous government", new AnnotationContext(AnnotationProfile.Load("en")));
        Check(
            "重复重置可用性后注音结果不变",
            string.Equals(before, after, StringComparison.Ordinal));
    }

    private static void TestAnnotationProfileAndDedupe()
    {
        var ja = AnnotationProfile.Load("ja");
        Check("常用汉字表过滤：日语超高频汉字词不注", ja.IsSkipped("日本語"));
        Check("常用汉字表过滤：小1-2基础动词不注", ja.IsSkipped("行く"));
        Check("常用汉字表过滤：小1-2基础名词不注", ja.IsSkipped("先生"));
        Check("策展停用词过滤：出来る不注", ja.IsSkipped("出来る"));
        Check("叠字符号依附常用字不淘汰：人々不注", ja.IsSkipped("人々"));
        Check("小3进阶汉字注音：薬不淘汰", !ja.IsSkipped("薬"));
        Check("小3进阶汉字注音：投与不淘汰", !ja.IsSkipped("投与"));
        Check("小3进阶汉字注音：睡眠薬不淘汰", !ja.IsSkipped("睡眠薬"));
        Check("小3进阶汉字注音：過剰摂取不淘汰", !ja.IsSkipped("過剰摂取"));
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

        // 短文本全注豁免：候选词不足阈值时停用词表与难词过滤全部让位（主人要求短句全部注音）
        var shortJa = AnnotationProfile.Load("ja", wordCount: AnnotationProfile.ShortTextWordThreshold - 1);
        Check("短文本豁免：常用汉字词也注", !shortJa.IsSkipped("日本語"));
        Check("短文本豁免：策展停用词也注", !shortJa.IsSkipped("出来る"));
        Check("短文本豁免：恰足阈值仍过滤", AnnotationProfile.Load("ja", wordCount: AnnotationProfile.ShortTextWordThreshold).IsSkipped("日本語"));
        Check("短文本豁免：英语停用词在短语里也注", !AnnotationProfile.Load("en", wordCount: 3).IsSkipped("the"));
        Check("短文本豁免：总开关关闭时仍不注", AnnotationProfile.Load("ja", enabled: false, wordCount: 3).IsSkipped("躊躇"));

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

        // 绝对保真（Lossless Compose）断言：复杂混合文本与换行
        var complexMixedText = "Hello 123! これは【テスト】です。\r\n薬の過剰摂取で亡くなった被害者。\nLine 3: 漢字 and symbols & % * 100%!";
        var spansMixed = new[]
        {
            new AnnotationSpan(25, 1, "くすり"),
            new AnnotationSpan(27, 4, "かじょうせっしゅ"),
        };
        var composedComplex = AnnotatedTextFormatter.Compose(complexMixedText, spansMixed);
        Check(
            "绝对保真断言：ToPlainText(Compose(text, spans)) == text 恒等",
            RubyTextParser.ToPlainText(composedComplex) == complexMixedText,
            $"composed={composedComplex}");
    }

    private static void TestEnglishPipeline()
    {
        Check(
            "CMUdict ARPAbet 转 IPA（音节切分与重音归位）",
            ArpabetIpaConverter.Convert("Y UW0 B IH1 K W IH0 T AH0 S") == "juː.ˈbɪk.wɪ.təs",
            $"actual={ArpabetIpaConverter.Convert("Y UW0 B IH1 K W IH0 T AH0 S")}");

        Check(
            "CMUdict ARPAbet 转 IPA（重读 AH 记 ʌ）",
            ArpabetIpaConverter.Convert("G AH1 V ER0 M AH0 N T") == "ˈɡʌv.ɜːr.mənt",
            $"actual={ArpabetIpaConverter.Convert("G AH1 V ER0 M AH0 N T")}");

        var studiesCandidates = EnglishLemmatizer.Candidates("studies");
        Check(
            "词形还原候选覆盖屈折",
            studiesCandidates.Contains("study") &&
            EnglishLemmatizer.Candidates("abandoned").Contains("abandon") &&
            EnglishLemmatizer.Candidates("running").Contains("run"),
            $"studies -> {string.Join(",", studiesCandidates)}");
    }

    /// <summary>
    /// 词典式 IPA 规范化：词典里的历史 wire format（重音贴在元音前、无音节点、IY 不带 ː）
    /// 必须归一成主人熟悉的词典形态；且对已规范化的条目幂等（将来换哈希的新词典读出来不许被动过）。
    /// </summary>
    private static void TestCmuDictionarySourceParser()
    {
        var entries = CmuDictionarySourceParser.Parse([
            ";;; fixture",
            "lead L EH1 D",
            "lead(2) L IY1 D",
            "record R EH1 K ER0 D",
            "record(2) R IH0 K AO1 R D",
            "# invalid"
        ]).ToArray();

        Check(
            "CMUdict 空白分隔与行尾标签不混入发音",
            CmuDictionarySourceParser.Parse([
                "  # source comment",
                "  ;;; legacy comment",
                "  LEAD(2)\t L IY1 D # alternate pronunciation",
                "lead(0) L EH1 D",
                "lead(abc) L EH1 D",
                "lead(3) # missing pronunciation"
            ]).ToArray() is [{ Word: "lead", Variant: 2, Arpabet: "L IY1 D" }]);

        Check(
            "CMUdict 编号读音在解析层保留",
            entries.Length == 4 &&
            entries.Count(entry => entry.Word == "lead") == 2 &&
            entries.Any(entry => entry.Word == "lead" && entry.Variant == 2) &&
            entries.Count(entry => entry.IsDefault) == 2,
            $"actual={string.Join(";", entries.Select(entry => $"{entry.Word}({entry.Variant})"))}");
    }
    private static void TestCmuDictionaryCandidateIndex()
    {
        var index = EnglishPronunciationIndex.FromCmuEntries(
            CmuDictionarySourceParser.Parse([
                "lead L EH1 D",
                "lead(2) L IY1 D",
                "record R EH1 K ER0 D",
                "record(2) R IH0 K AO1 R D"
            ]),
            ArpabetIpaConverter.Convert);

        var unordered = EnglishPronunciationIndex.FromCmuEntries(
            CmuDictionarySourceParser.Parse(["lead(2) L IY1 D", "LEAD L EH1 D", "orphan(2) AO1 R F AH0 N"]),
            ArpabetIpaConverter.Convert);
        Check("CMUdict 候选按来源编号排序且查询不区分大小写",
            unordered.LookupAll("LEAD").Select(c => c.Variant).SequenceEqual([1, 2]) &&
            unordered.Lookup("LEAD") is { Variant: 1, Ipa: "ˈled" });
        Check("CMUdict 缺少主项时不把备选伪装成默认",
            unordered.LookupAll("orphan").Count == 1 && unordered.Lookup("orphan") is null &&
            unordered.LookupAll("missing").Count == 0 && unordered.Lookup("missing") is null);

        var lead = index.LookupAll("lead");
        var record = index.LookupAll("record");
        var defaultLead = index.Lookup("lead");
        Check(
            "CMUdict 候选索引保留全部编号读音并提供默认项",
            lead.Count == 2 && record.Count == 2 &&
            lead.Any(candidate => candidate.Variant == 2) &&
            record.Any(candidate => candidate.Variant == 2) &&
            defaultLead is { Variant: 1 } &&
            defaultLead.Value.Ipa == lead[0].Ipa,
            $"lead={string.Join(",", lead.Select(candidate => $"{candidate.Variant}:{candidate.Ipa}"))} record={record.Count}");
    }
    private static void TestIpaTranscription()
    {
        Check(
            "treason 归一为主人预期的词典式 IPA",
            IpaTranscription.Normalize("trˈizən") == "ˈtriː.zən",
            $"actual={IpaTranscription.Normalize("trˈizən")}");

        Check(
            "government 重读 schwa 记 ʌ",
            IpaTranscription.Normalize("ɡˈəvɜːrmənt") == "ˈɡʌv.ɜːr.mənt",
            $"actual={IpaTranscription.Normalize("ɡˈəvɜːrmənt")}");

        Check(
            "photograph 次重音与三辅音起首",
            IpaTranscription.Normalize("fˈoʊtəɡrˌæf") == "ˈfoʊ.tə.ˌɡræf",
            $"actual={IpaTranscription.Normalize("fˈoʊtəɡrˌæf")}");

        Check(
            "banana 重读短元音后首辅音收尾（美音口径）",
            IpaTranscription.Normalize("bənˈænə") == "bə.ˈnæn.ə",
            $"actual={IpaTranscription.Normalize("bənˈænə")}");

        Check(
            "strength 单音节不插音节点",
            IpaTranscription.Normalize("strˈeŋkθ") == "ˈstreŋkθ",
            $"actual={IpaTranscription.Normalize("strˈeŋkθ")}");

        Check(
            "规范化幂等：已规范化的条目再走一遍不变",
            IpaTranscription.Normalize(IpaTranscription.Normalize("trˈizən")) == "ˈtriː.zən" &&
            IpaTranscription.Normalize("juː.ˈbɪk.wɪ.təs") == "juː.ˈbɪk.wɪ.təs" &&
            IpaTranscription.Normalize("ˈfoʊ.tə.ˌɡræf") == "ˈfoʊ.tə.ˌɡræf",
            $"actual={IpaTranscription.Normalize(IpaTranscription.Normalize("trˈizən"))}");
    }

    private static void TestPinyinToneConversion()
    {
        Check("声调数字转符号", PinyinToneConverter.ConvertSyllable("ri4") == "rì", $"actual={PinyinToneConverter.ConvertSyllable("ri4")}");
        Check("多音节短语", PinyinToneConverter.ConvertPhrase("Ri4 Ben3") == "rì běn", $"actual={PinyinToneConverter.ConvertPhrase("Ri4 Ben3")}");
        Check("ou 标 o 位", PinyinToneConverter.ConvertSyllable("hao3") == "hǎo", $"actual={PinyinToneConverter.ConvertSyllable("hao3")}");
        Check("轻声去数字", PinyinToneConverter.ConvertSyllable("ma5") == "ma", $"actual={PinyinToneConverter.ConvertSyllable("ma5")}");
        Check("v 归一化为 ü", PinyinToneConverter.ConvertSyllable("lü3") == "lǚ", $"actual={PinyinToneConverter.ConvertSyllable("lü3")}");
    }

    /// <summary>词典无读音不臆造；扩展标量或非法代理项不能吞掉后续已知词的偏移。</summary>
    private static void TestChineseStructuredBoundaries()
    {
        using var annotator = new ChinesePinyinAnnotator();
        if (!annotator.IsAvailable)
        {
            Skip("zh 词典未就绪，跳过结构化 Unicode 边界实词典验证");
            return;
        }

        foreach (var boundary in new[] { "𠮷", "😀", "\ud800", "\udc00", "\ud800\ud800", "𠮷😀" })
        {
            var text = "银行" + boundary + "龃龉";
            var spans = annotator.Annotate(text, new AnnotationContext(AnnotationProfile.Load("zh", wordCount: 2)));
            var expected = 2 + boundary.Length;
            Check($"Chinese restores known 龃龉 after UTF16 boundary {string.Join('-', boundary.Select(c => ((int)c).ToString("X4")))}",
                spans.Any(s => s.Start == expected && s.Length == 2 && s.Reading == "jǔ yǔ" &&
                    s.Syllables is [{ Length: 1 }, { Length: 1 }] &&
                    s.Syllables[0].Start == expected && s.Syllables[1].Start == expected + 1) &&
                spans.All(s => s.Start == 0 || s.Start >= expected));
            Check($"Chinese CountWords shares Unicode boundary length {string.Join('-', boundary.Select(c => ((int)c).ToString("X4")))}",
                annotator.CountWords(text) == annotator.CountWords("银行") + annotator.CountWords("龃龉"));
        }
        var unknown = annotator.Annotate("㐀龃龉", new AnnotationContext(AnnotationProfile.Load("zh", wordCount: 2)));
        Check("Chinese real dictionary unknown feature is never displayed as pinyin",
            unknown.All(s => !s.Reading.Contains("KANJI", StringComparison.OrdinalIgnoreCase)) &&
            unknown.Any(s => s.Start == 1 && s.Length == 2 && s.Reading == "jǔ yǔ"));
        var bank = annotator.Annotate("去银行", new AnnotationContext(AnnotationProfile.Load("zh", rareOnly: false)));
        Check("Chinese real dictionary bank span excludes preceding unannotated 去",
            bank.Any(s => s.Start == 1 && s.Length == 2 && s.Reading == "yín háng" && s.Syllables?.Count == 2));
        // P2 多音数据端到端：真实词典下「银行」的「行」必须携带默认 háng 与备选 xíng，供提示色与 hover 候选菜单消费
        Check("Chinese real dictionary bank syllable carries default + alternative pronunciation candidates",
            bank.Any(s => s.Reading == "yín háng" && s.Syllables is { Count: 2 } syl &&
                syl[1].Reading == "háng" && syl[1].Candidates is { Count: > 1 } cand &&
                cand[0] == "háng" && cand.Contains("xíng")));
        var repeated = annotator.Annotate("银行银行", new AnnotationContext(AnnotationProfile.Load("zh", wordCount: 2)));
        Check("Chinese syllable mapping keeps word-level dedup", repeated.Count(s => s.Reading == "yín háng") == 1);
        var filtered = annotator.Annotate("去银行", new AnnotationContext(AnnotationProfile.Load("zh")));
        Check("Chinese syllable mapping preserves long-text rare-only filtering", filtered.Count == 0);
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

        var pinnedLanguage = "en";
        var english = registry.GetAnnotator(pinnedLanguage);
        if (english is null || !english.IsAvailable)
        {
            // 注音词典是按需下载的可选依赖：没就绪时跳过，不报假失败（与金样本测试同一口径）。
            Skip("en 词典未就绪（deps/phonetics/en），跳过英语路由金样本");
        }
        else
        {
            var pinned = registry.Annotate("The ubiquitous government", pinnedLanguage);
            Check(
                "手选英语：三个词的短语全注（含停用词与高频词）",
                pinned.Count == 3,
                $"actual={AnnotatedTextFormatter.Compose("The ubiquitous government", pinned)}");
        }
    }

    /// <summary>
    /// 日语书面振假名金样本（issue 010）：全走生产同一链——原生 UniDic 语言包 + 宿主信号切分。
    /// 短文本全注口径（wordCount 小于阈值）用于避开停用/难词过滤，只验证「词面→底字+书面假名」本身；
    /// 缺包时跳过而不是假失败，但「缺包仍然可用」必须验明为纯文本降级。
    /// 分词与旧 IPAdic 不同（日本+语、睡眠+药），断言按 UniDic 实结果写，不机械要求数量等价。
    /// </summary>
    private static void TestJapaneseWrittenKana()
    {
        using var host = new NativePhoneticPackHost();
        using var registry = TextAnnotatorRegistry.Create(host);
        var annotator = registry.GetAnnotator("ja");
        if (annotator is null || !annotator.IsAvailable)
        {
            Skip("ja 原生语言包未就绪（deps/phonetics/misaki/ja），跳过日语书面假名金样本");
            return;
        }

        // 短文本全注：2 个候选词远低于阈值，两张过滤器都让位，只凸显切分与书面假名。
        var shortText = AnnotationProfile.Load("ja", wordCount: 2);

        Check(
            "书面假名金样本：東京 → とうきょう（不从长音符猜 とーきょう）",
            Annotated(annotator, "東京へ行く。", new AnnotationContext(shortText)) == "東京[とうきょう]へ行[い]く。",
            $"actual={Annotated(annotator, "東京へ行く。", new AnnotationContext(shortText))}");

        Check(
            "书面假名金样本：躊躇 → ちゅうちょ",
            Annotated(annotator, "躊躇した", new AnnotationContext(shortText)) == "躊躇[ちゅうちょ]した",
            $"actual={Annotated(annotator, "躊躇した", new AnnotationContext(shortText))}");

        Check(
            "书面假名金样本：今日 → きょう",
            Annotated(annotator, "今日はいい天気です。", new AnnotationContext(shortText)) == "今日[きょう]はいい天気[てんき]です。",
            $"actual={Annotated(annotator, "今日はいい天気です。", new AnnotationContext(shortText))}");

        // 凍り付く：后接送假名 く 不在底字里，读音取书面假名 こおりつ（全音便的 こーりつ 不被接受）。
        var frozen = annotator.Annotate("凍り付く", new AnnotationContext(shortText));
        Check(
            "书面假名金样本：凍り付く → 底字 凍り付 / 读音 こおりつ",
            frozen is [{ Start: 0, Length: 3, Reading: "こおりつ" }],
            $"actual={AnnotatedTextFormatter.Compose("凍り付く", frozen)}");

        var tabeta = Annotated(annotator, "食べた", new AnnotationContext(shortText));
        Check("送假名切分：食べた → 食[た]べた", tabeta == "食[た]べた", $"actual={tabeta}");

        var oiwai = Annotated(annotator, "お祝い", new AnnotationContext(shortText));
        Check("送假名切分：お祝い → お祝[いわ]い", oiwai == "お祝[いわ]い", $"actual={oiwai}");

        var kaimonoshi = Annotated(annotator, "買い出しに行く", new AnnotationContext(shortText));
        Check(
            "内部送假名：買い出し → 買い出[かいだ]し",
            kaimonoshi == "買い出[かいだ]しに行[い]く",
            $"actual={kaimonoshi}");

        var ikimasu = Annotated(annotator, "行きます", new AnnotationContext(shortText));
        Check("送假名切分：行きます → 行[い]きます", ikimasu == "行[い]きます", $"actual={ikimasu}");

        var ocha = Annotated(annotator, "お茶を飲む", new AnnotationContext(shortText));
        Check("前缀假名剥离：お茶 → お茶[ちゃ]", ocha.Contains("お茶[ちゃ]"), $"actual={ocha}");

        // 复合词在 UniDic 里拆成两个词元：睡眠 + 药，不再是整词 睡眠薬[すいみんやく]。
        var nemurigusuri = Annotated(annotator, "睡眠薬", new AnnotationContext(shortText));
        Check(
            "复合词按 UniDic 词元分开注音：睡眠藥 → 睡眠[すいみん]薬[やく]",
            nemurigusuri == "睡眠[すいみん]薬[やく]",
            $"actual={nemurigusuri}");

        var mochikaeri = Annotated(annotator, "持ち帰り", new AnnotationContext(shortText));
        Check("内部送假名：持ち帰り → 持ち帰[もちかえ]り", mochikaeri == "持ち帰[もちかえ]り", $"actual={mochikaeri}");

        const string userSentence = "薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬。";
        var userAnnotated = Annotated(annotator, userSentence, new AnnotationContext(AnnotationProfile.Load("ja")));
        Check(
            "小1-2年级240字基准下难词正常注音",
            userAnnotated.Contains("薬[くすり]") &&
            userAnnotated.Contains("過剰[かじょう]") &&
            userAnnotated.Contains("摂取[せっしゅ]") &&
            userAnnotated.Contains("致死[ちし]量[りょう]") &&
            userAnnotated.Contains("睡眠[すいみん]薬[やく]"),
            $"actual={userAnnotated}");

        // 同词异音：日本在同一句里先读 にっぽん、后读 にほん，两处都得注（同音才去重）。
        var repeated = Annotated(annotator, "日本語を勉強する日本語", new AnnotationContext(shortText));
        Check(
            "同词异音补标、同词同音去重（语 只注首现）",
            repeated == "日本[にっぽん]語[ご]を勉強[べんきょう]する日本[にほん]語",
            $"actual={repeated}");

        var duplicate = annotator.Annotate("躊躇した。また躊躇した。", new AnnotationContext(shortText));
        Check(
            "同篇重复词形同读音只注首现",
            duplicate.Count == 1,
            $"actual={AnnotatedTextFormatter.Compose("躊躇した。また躊躇した。", duplicate)}");

        // 未知词、数字、拉丁与纯假名：只有合法汉字底字才产生注音。
        var mixedLatin = annotator.Annotate("これはiPhoneです", new AnnotationContext(shortText));
        Check(
            "中英混排：拉丁词元不被日语标注器认领",
            mixedLatin.Count == 0,
            $"actual={AnnotatedTextFormatter.Compose("これはiPhoneです", mixedLatin)}");

        var kanaOnly = annotator.Annotate("こんにちは、ふりがな", new AnnotationContext(shortText));
        Check("纯假名不注音", kanaOnly.Count == 0, $"actual={AnnotatedTextFormatter.Compose("こんにちは、ふりがな", kanaOnly)}");

        const string digits = "A株100株と3.14円";
        var digitSpans = annotator.Annotate(digits, new AnnotationContext(shortText));
        Check(
            "数字与拉丁字符不进入底字区间",
            digitSpans.Count > 0 &&
            digitSpans.All(span => digits.AsSpan(span.Start, span.Length).ToArray().All(RubyTextParser.IsCjkKanji)),
            $"actual={AnnotatedTextFormatter.Compose(digits, digitSpans)}");

        // Unicode 边界：emoji（代理项对）、换行与空白按原文 UTF-16 定位，区间不重叠且能对回原文。
        const string mixed = "😀東京\n\u3000大阪😀";
        var mixedSpans = annotator.Annotate(mixed, new AnnotationContext(shortText));
        Check(
            "emoji/换行/空白：区间按 UTF-16 定位且不重叠",
            mixedSpans.Count == 2 &&
            mixedSpans[0] is { Start: 2, Length: 2, Reading: "とうきょう" } &&
            mixedSpans[1] is { Start: 6, Length: 2, Reading: "おおさか" } &&
            mixedSpans.Zip(mixedSpans.Skip(1)).All(pair => pair.First.Start + pair.First.Length <= pair.Second.Start) &&
            string.Equals(AnnotatedTextFormatter.Compose(mixed, mixedSpans), "😀東京[とうきょう]\n\u3000大阪[おおさか]😀", StringComparison.Ordinal),
            $"actual={AnnotatedTextFormatter.Compose(mixed, mixedSpans)}");

        // 缺包行为：同一个宿主指向不存在的目录时只能退化为纯文本，不能抛异常也不能回退旧引擎。
        using var emptyHost = new NativePhoneticPackHost(
            packDirectory: language => Path.Combine(Path.GetTempPath(), "nani-kore-ja-missing", language));
        var missing = emptyHost.GetAnnotator("ja")!;
        Check(
            "缺包：状态缺失、不可用、注音为空且不抛异常",
            emptyHost.GetState("ja") == NativePhoneticPackState.Missing &&
            !missing.IsAvailable &&
            missing.Annotate("東京へ行く", new AnnotationContext(shortText)).Count == 0 &&
            missing.CountWords("東京へ行く") == 0);
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
        var mixedChinese = annotator.Annotate("持有300股A股股票", allContext);
        Check(
            "中文标注器严禁数字与英文字符混入底字区间",
            mixedChinese.All(s => "持有300股A股股票".AsSpan(s.Start, s.Length).ToArray().All(RubyTextParser.IsCjkKanji)),
            $"actual={AnnotatedTextFormatter.Compose("持有300股A股股票", mixedChinese)}");
    }

    /// <summary>
    /// 旧 en/ipa_dict.db 的候选库断言（数据保留、不再被 provider 使用，随旧 provider 在 014 下线）：
    /// 多读音词全部编号进库、默认查询取首项。英语注音生产路径已经是原生语言包，见 TestEnglishNativeAnnotation。
    /// </summary>
    private static void TestEnglishCandidateDatabase()
    {
        using var dictionary = EnglishIpaDictionary.TryOpen(PhoneticDependencyPaths.EnglishDictionaryFile);
        if (dictionary is null)
        {
            Skip("en 词典未就绪（deps/phonetics/en），跳过英语候选库断言");
            return;
        }

        var record = dictionary.LookupAll("record");
        var lead = dictionary.LookupAll("lead");
        var defaultRecord = dictionary.Lookup("record");
        Check(
            "英语候选库：多读音词全部编号进库且按 variant 升序（record≥3、lead=2）",
            record.Count >= 3 && lead.Count == 2 && record[0].Variant == 1 && record[0].Variant < record[1].Variant,
            $"record={record.Count} lead={lead.Count}");
        Check(
            "英语候选库：默认查询等于候选首项（variant=1）",
            record.Count > 0 && defaultRecord == record[0].Ipa,
            $"default={defaultRecord} first={(record.Count > 0 ? record[0].Ipa : "<none>")}");
        Check("英语候选库：未登录词候选为空", dictionary.LookupAll("zzzznotaword").Count == 0);
    }

    /// <summary>黄金样例辅助：一次标注一次拼装（AnnotationContext 的去重是单篇状态，重复调用会清空结果）。</summary>
    private static string Annotated(ITextAnnotator annotator, string text, AnnotationContext context)
    {
        var spans = annotator.Annotate(text, context);
        return AnnotatedTextFormatter.Compose(text, spans);
    }

    private static void Skip(string reason) => Console.WriteLine($"SKIP: {reason}");
}
