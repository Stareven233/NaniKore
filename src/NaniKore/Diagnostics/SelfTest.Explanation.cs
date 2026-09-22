namespace NaniKore.Diagnostics;

using System;
using System.Threading.Tasks;
using NaniKore.Favorites;
using NaniKore.Translation;

internal static partial class SelfTest
{
    public static void RunExplanationTests()
    {
        Console.WriteLine("--- Running Explanation & Favorites Tests ---");

        TestExplanationParserStandard();
        TestExplanationParserFaultTolerant();
        TestExplanationParserDegradation();
        TestExplanationParserStreamingAccumulation();
        TestExplanationParserBuiltinSectionNames();
        TestFavoritesStringMatching();
        TestFavoritesEventsAndNoteFormat();
        TestRubyParserNoiseTolerance();
        TestExplanationPromptN3Filter();
        TestExplanationEducationalGrammarNormalization();
    }

    private static void TestExplanationParserStandard()
    {
        var markdown = """
            ### [词汇/短语]
            - **通る** | とおる | 一类动词 | 通过、走过。此处为可能态否定「通れない」
            - **よ** | - | 助词 | 柔化告知语气，非命令

            ### [语法/句型]
            - **～ない** | 否定表达。表示“不…”、“无法…”

            ### [语境与地道表达]
            句子语气柔和，中文译为“过不去哦”相比生硬的“不可通过”更契合口语日常场景。
            """;

        var doc = ExplanationParser.Parse(markdown);

        Check("ExplanationParser.Standard.IsStructured", doc.IsStructured);
        Check("ExplanationParser.Standard.VocabCount", doc.VocabularyCards.Count == 2);
        Check("ExplanationParser.Standard.GrammarCount", doc.GrammarCards.Count == 1);
        Check("ExplanationParser.Standard.Vocab1Term", doc.VocabularyCards[0].Term == "通る");
        Check("ExplanationParser.Standard.Vocab1Reading", doc.VocabularyCards[0].Reading == "とおる");
        Check("ExplanationParser.Standard.Vocab1Role", doc.VocabularyCards[0].Role == "一类动词");
        Check("ExplanationParser.Standard.Vocab1Meaning", doc.VocabularyCards[0].Meaning.Contains("通过"));
        Check("ExplanationParser.Standard.Vocab2ReadingHyphenToEmpty", doc.VocabularyCards[1].Reading == string.Empty);
        Check("ExplanationParser.Standard.Grammar1Pattern", doc.GrammarCards[0].Pattern == "～ない");
        Check("ExplanationParser.Standard.ContextContent", doc.ContextExplanation.Contains("过不去哦"));
    }

    private static void TestExplanationParserFaultTolerant()
    {
        // 变体标题（不带中括号、词汇行格式变体）
        var markdown = """
            ## 重点词汇
            * `run` | rʌn | verb | 跑步，奔跑
            - eat | 吃（只有两段容错）

            ### 文法
            - **can not** | 无法做到

            ### 语境分析
            这是日常口语表达。
            """;

        var doc = ExplanationParser.Parse(markdown);

        Check("ExplanationParser.FaultTolerant.IsStructured", doc.IsStructured);
        Check("ExplanationParser.FaultTolerant.VocabCount", doc.VocabularyCards.Count == 2);
        Check("ExplanationParser.FaultTolerant.Vocab1CodeTagClean", doc.VocabularyCards[0].Term == "run");
        Check("ExplanationParser.FaultTolerant.Vocab1Reading", doc.VocabularyCards[0].Reading == "rʌn");
        Check("ExplanationParser.FaultTolerant.Vocab2TwoPartsFallback", doc.VocabularyCards[1].Term == "eat" && doc.VocabularyCards[1].Meaning == "吃（只有两段容错）");
        Check("ExplanationParser.FaultTolerant.GrammarCount", doc.GrammarCards.Count == 1);
        Check("ExplanationParser.FaultTolerant.GrammarPattern", doc.GrammarCards[0].Pattern == "can not");
        Check("ExplanationParser.FaultTolerant.Context", doc.ContextExplanation.Contains("日常口语表达"));
    }

    private static void TestExplanationParserDegradation()
    {
        // 纯自然段落，没有任何微结构化标记
        var plainText = """
            这段话的意思是，我们不能从这里通过。
            请注意其中的语气非常日常自然。
            """;

        var doc = ExplanationParser.Parse(plainText);

        Check("ExplanationParser.Degradation.NonStructuredDetected", !doc.IsStructured);
        Check("ExplanationParser.Degradation.RawPreserved", doc.RawMarkdown == plainText);
        Check("ExplanationParser.Degradation.CardsEmpty", doc.VocabularyCards.Count == 0 && doc.GrammarCards.Count == 0);

        // 空白文本
        var emptyDoc = ExplanationParser.Parse("   \r\n   ");
        Check("ExplanationParser.Degradation.EmptyNotStructured", !emptyDoc.IsStructured);
    }

    private static void TestExplanationParserStreamingAccumulation()
    {
        // 模拟大模型打字流式分块输入
        var chunks = new[]
        {
            "### [词汇/短语]\n- **春",
            "** | はる | 名词 | 春天\n\n### [",
            "语法/句型]\n- **～そうだ** | 好像、似乎",
            "\n\n### [语境与地道表达]\n表达对季节的期待。"
        };

        var accumulated = new System.Text.StringBuilder();
        ExplanationDocument lastDoc = null!;

        foreach (var chunk in chunks)
        {
            accumulated.Append(chunk);
            lastDoc = ExplanationParser.Parse(accumulated.ToString());
        }

        Check("ExplanationParser.Streaming.FinalIsStructured", lastDoc.IsStructured);
        Check("ExplanationParser.Streaming.VocabComplete", lastDoc.VocabularyCards.Count == 1 && lastDoc.VocabularyCards[0].Term == "春");
        Check("ExplanationParser.Streaming.GrammarComplete", lastDoc.GrammarCards.Count == 1 && lastDoc.GrammarCards[0].Pattern == "～そうだ");
        Check("ExplanationParser.Streaming.ContextComplete", lastDoc.ContextExplanation.Contains("对季节的期待"));
    }

    private static void TestExplanationParserBuiltinSectionNames()
    {
        // 内置精讲提示词实际下发的小节名必须被完整识别：「翻译精讲」归属语境分析段，内容不得丢失
        var markdown = """
            ### [词汇/短语]
            - **通れない** | とおれない | 一类动词 | 「通る」可能态的否定形

            ### [语法/句型]
            - **～ない** | 否定表达

            ### [翻译精讲]
            结合语境与背景/语法知识分析解释为何如此翻译
            """;

        var doc = ExplanationParser.Parse(markdown);

        Check("ExplanationParser.BuiltinSections.IsStructured", doc.IsStructured);
        Check("ExplanationParser.BuiltinSections.VocabCount", doc.VocabularyCards.Count == 1);
        Check("ExplanationParser.BuiltinSections.GrammarCount", doc.GrammarCards.Count == 1);
        Check("ExplanationParser.BuiltinSections.TranslationAnalysisCaptured", doc.ContextExplanation.Contains("为何如此翻译"));
    }

    private static void TestFavoritesStringMatching()
    {
        var service = new InMemoryFavoriteService();

        var item1 = new FavoriteItem
        {
            Category = "单词",
            Content = "Apple",
            Note = "读音: /ˈæp.əl/\n释义: 苹果"
        };
        var item2 = new FavoriteItem
        {
            Category = "语法",
            Content = "～ない",
            Note = "释义: 否定"
        };

        service.AddFavoriteAsync(item1).GetAwaiter().GetResult();
        service.AddFavoriteAsync(item2).GetAwaiter().GetResult();

        // 字符串匹配（支持不区分大小写、忽略首尾空格）
        Check("Favorites.Match.Exact", service.IsFavorited("Apple"));
        Check("Favorites.Match.CaseInsensitive", service.IsFavorited("apple"));
        Check("Favorites.Match.Trimmed", service.IsFavorited("  Apple  "));
        Check("Favorites.Match.CategorySpecific", service.IsFavorited("Apple", "单词"));
        Check("Favorites.Match.CategoryMismatch", !service.IsFavorited("Apple", "语法"));
        Check("Favorites.Match.GrammarPattern", service.IsFavorited("～ない"));
        Check("Favorites.Match.NotFound", !service.IsFavorited("Banana"));

        // 移除测试
        service.RemoveFavoriteAsync("apple").GetAwaiter().GetResult();
        Check("Favorites.Match.AfterRemoval", !service.IsFavorited("Apple"));
    }

    private static void TestFavoritesEventsAndNoteFormat()
    {
        var service = new InMemoryFavoriteService();
        FavoriteItem? addedItem = null;
        string? removedContent = null;

        service.FavoriteAdded += (_, item) => addedItem = item;
        service.FavoriteRemoved += (_, content) => removedContent = content;

        var card = new ExplanationVocabularyCard
        {
            Term = "走る",
            Reading = "はしる",
            Role = "动词原形",
            Meaning = "奔跑，行进"
        };

        var note = card.ToFavoriteNote();
        Check("Favorites.NoteFormat.HasReading", note.Contains("读音: はしる"));
        Check("Favorites.NoteFormat.HasRole", note.Contains("原形: 动词原形"));
        Check("Favorites.NoteFormat.HasMeaning", note.Contains("释义: 奔跑，行进"));

        var favoriteItem = new FavoriteItem
        {
            Category = "单词",
            Content = card.Term,
            Note = note
        };

        service.AddFavoriteAsync(favoriteItem).GetAwaiter().GetResult();
        Check("Favorites.Event.AddedTriggered", addedItem != null && addedItem.Content == "走る");

        service.RemoveFavoriteAsync("走る").GetAwaiter().GetResult();
        Check("Favorites.Event.RemovedTriggered", removedContent == "走る");
    }

    private static void TestRubyParserNoiseTolerance()
    {
        // 测试主人提及的破损/多余字符用例：亡?[な]く?]な?]った
        var brokenText = "亡?[な]く?]な?]った";
        var segments = RubyTextParser.Parse(brokenText);

        Check("RubyParser.Noise.SegmentsCount", segments.Count == 2);
        Check("RubyParser.Noise.RubyExtracted", segments[0].Text == "亡" && segments[0].Ruby == "な");
        Check("RubyParser.Noise.PlainTrailingExtracted", segments[1].Text == "くなった" && segments[1].Ruby == null);

        var plain = RubyTextParser.ToPlainText(brokenText);
        var reading = RubyTextParser.ToReadingText(brokenText);
        var annotated = RubyTextParser.ToAnnotatedText(segments);

        Check("RubyParser.Noise.ToPlainTextClean", plain == "亡くなった");
        Check("RubyParser.Noise.ToReadingTextClean", reading == "なくなった");
        Check("RubyParser.Noise.ToAnnotatedTextClean", annotated == "亡[な]くなった");

        // 测试内部问号 [な?] 与汉字多问号 亡??[な]
        var innerNoise = "亡??[な?]";
        var innerSegments = RubyTextParser.Parse(innerNoise);
        Check("RubyParser.Noise.InnerAndMultiNoiseExtracted", innerSegments.Count == 1 && innerSegments[0].Text == "亡" && innerSegments[0].Ruby == "な");
    }

    private static void TestExplanationPromptN3Filter()
    {
        Check("Prompt.Explain.N3FilterInUserPrompt", PromptTemplates.DefaultExplainUserPrompt.Contains("N3 及以上"));
        Check("Prompt.Explain.N3FilterInSystemPrompt", PromptTemplates.ExplainStructureRule.Contains("JLPT N3"));
    }

    private static void TestExplanationEducationalGrammarNormalization()
    {
        // 词性归一化验证
        Check("EduGrammar.Normalize.Ichidan", ExplanationParser.NormalizeEducationalRole("一段动词") == "二类动词");
        Check("EduGrammar.Normalize.CompoundIchidan", ExplanationParser.NormalizeEducationalRole("动词，他动词，一段动词") == "二类动词");
        Check("EduGrammar.Normalize.Godan", ExplanationParser.NormalizeEducationalRole("五段活用动词") == "一类动词");
        Check("EduGrammar.Normalize.Sahen", ExplanationParser.NormalizeEducationalRole("サ变动词") == "三类动词");
        Check("EduGrammar.Normalize.IKoushi", ExplanationParser.NormalizeEducationalRole("い形容词") == "一类形容词");
        Check("EduGrammar.Normalize.NaKoushi", ExplanationParser.NormalizeEducationalRole("な形容词") == "二类形容词");
        Check("EduGrammar.Normalize.KeiyouDoushi", ExplanationParser.NormalizeEducationalRole("形容动词") == "二类形容词");
        Check("EduGrammar.Normalize.Noun", ExplanationParser.NormalizeEducationalRole("名词") == "名词");

        // 3段格式（缺失读音，直接给出词性）容错：亡くなる | 动词，他动词，一段动词 | 去世，死亡
        var threePartsMarkdown = """
            ### [词汇/短语]
            - **亡くなる** | 动词，他动词，一段动词 | 去世，死亡
            """;
        var doc = ExplanationParser.Parse(threePartsMarkdown);
        Check("EduGrammar.ThreeParts.VocabCount", doc.VocabularyCards.Count == 1);
        var card = doc.VocabularyCards[0];
        Check("EduGrammar.ThreeParts.Term", card.Term == "亡くなる");
        Check("EduGrammar.ThreeParts.ReadingEmpty", string.IsNullOrEmpty(card.Reading));
        Check("EduGrammar.ThreeParts.RoleNormalized", card.Role == "二类动词");
        Check("EduGrammar.ThreeParts.Meaning", card.Meaning == "去世，死亡");

        // ViewModel 标题与纯词性徽章验证
        var vm = new NaniKore.App.Controls.VocabularyCardViewModel(card, false);
        Check("EduGrammar.ViewModel.DisplayTitleNoReading", vm.DisplayTitle == "亡くなる");
        Check("EduGrammar.ViewModel.RoleBadgeText", vm.RoleBadgeText == "二类动词");

        var cardWithReading = new ExplanationVocabularyCard
        {
            Term = "通る",
            Reading = "とおる",
            Role = "一类动词",
            Meaning = "通过"
        };
        var vmWithReading = new NaniKore.App.Controls.VocabularyCardViewModel(cardWithReading, true);
        Check("EduGrammar.ViewModel.DisplayTitleWithReading", vmWithReading.DisplayTitle == "通る (とおる)");
        Check("EduGrammar.ViewModel.RoleBadgeTextWithReading", vmWithReading.RoleBadgeText == "一类动词");
    }
}

