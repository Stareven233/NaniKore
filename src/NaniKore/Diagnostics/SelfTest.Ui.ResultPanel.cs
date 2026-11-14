using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Phonetics;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（结果面板）：本地注音叠加、振假名/纯文本双模态与翻译流、指标条、
/// 精讲面板生命周期、精讲卡片收藏与划词气泡交互。
/// </summary>
internal static partial class SelfTest
{
    private static void RunResultPanelUiTests()
    {
        TestResultPanelSourceAnnotation();
        TestStructuredChineseDisplayAndLayout();
        TestChineseMultiPronunciationHint();
        TestAnnotationInvalidation();
        TestResultPanelSubscriptionCleanup();
        TestResultPanelDetectedSourceLanguage();
        TestResultPanelRubyDualModeAndTranslationFlow();
        TestResultPanelResetDuringTranslation();
        TestResultPanelViewportLayout();
        TestResultPanelPlacementGeometry();
        TestAutomaticAnnotationPolicy();
        TestResultPanelAnnotateButton();
        TestResultPanelShortTextAnnotateAll();
        TestResultPanelMetricsBar();
        TestExplanationExpanderLifecycle();
        TestExplanationCardViewFavorites();
        TestSelectionBubbleInteractions();
        TestSelectionBubbleFavoriteNote();
        TestRubyDocumentBuilderAndSelectionCopy();
        TestResultPanelPolishBatch();
    }

    /// <summary>固定区间验证银行不吞前缀，逐字列仍是一个词级选区，字面括号完整保留。</summary>
    private static void TestStructuredChineseDisplayAndLayout()
    {
        const string source = "去银行[原文]，银行\n龃龉";
        AnnotationSpan[] spans = [new(1, 2, "yín háng",
            [new(1, 1, "yín"), new(2, 1, "háng")])];
        foreach (var fontSize in new[] { 14.0, 17.5, 28.0 })
        {
            var inlines = RubyDocumentBuilder.BuildInlines(source, spans, fontSize);
            var rubies = inlines.OfType<InlineUIContainer>().Select(c => c.Child).OfType<RubyInlineControl>().ToArray();
            Check($"structured bank keeps preceding 去 outside word ({fontSize})",
                inlines[0] is Run { Text: "去" } && rubies is [{ BaseText: "银行", RubyText: "yín háng" }]);
            Check($"structured plain copy preserves literal brackets ({fontSize})",
                RubyDocumentBuilder.ExtractFromInlines(inlines, 0, int.MaxValue, RubyCopyKind.Plain) ==
                source.Replace("\n", Environment.NewLine));
            Check($"structured annotation export preserves exact bank boundary ({fontSize})",
                AnnotatedTextFormatter.Compose(source, spans) == "去银行[yín háng][原文]，银行\n龃龉");
            Check($"structured bank selection copies one whole word in all formats ({fontSize})",
                RubyDocumentBuilder.ExtractFromInlines(inlines, 1, 2, RubyCopyKind.Plain) == "银行" &&
                RubyDocumentBuilder.ExtractFromInlines(inlines, 1, 2, RubyCopyKind.Annotated) == "银行[yín háng]" &&
                RubyDocumentBuilder.ExtractFromInlines(inlines, 1, 2, RubyCopyKind.ReadingOnly) == "yín háng");

            var viewer = new SelectableTextBlock { FontSize = fontSize, Inlines = inlines };
            viewer.Measure(Size.Infinity);
            viewer.Arrange(new Rect(viewer.DesiredSize));
            var ruby = rubies.Single();
            var blocks = ruby.Children.OfType<TextBlock>().Where(b => b.IsVisible && b.Text?.Length > 0).ToArray();
            var yin = blocks.Single(b => b.Text == "yín");
            var hang = blocks.Single(b => b.Text == "háng");
            var silver = blocks.Single(b => b.Text == "银");
            var walk = blocks.Single(b => b.Text == "行");
            static double Center(TextBlock b) => b.Bounds.Center.X;
            Check($"real Avalonia syllable columns center on original characters ({fontSize})",
                Math.Abs(Center(yin) - Center(silver)) <= 0.5 && Math.Abs(Center(hang) - Center(walk)) <= 0.5 &&
                yin.Bounds.Right <= hang.Bounds.Left + 0.1 && silver.Bounds.Right <= walk.Bounds.Left + 0.1,
                $"yin={yin.Bounds}, silver={silver.Bounds}, hang={hang.Bounds}, walk={walk.Bounds}, ruby={ruby.Bounds}");
            Check($"real Avalonia word contains syllable display and click surface ({fontSize})",
                blocks.All(b => b.Bounds.Left >= -0.1 && b.Bounds.Right <= ruby.Bounds.Width + 0.1) &&
                ruby.Children[0].Bounds.Width == ruby.Bounds.Width &&
                ruby.Children[0].Bounds.Height >= yin.Bounds.Height && ruby.DesiredSize.Height < fontSize * 3,
                $"word={ruby.Bounds}, hit={ruby.Children[0].Bounds}, children={string.Join(';', blocks.Select(b => $"{b.Text}:{b.Bounds}"))}");
            var following = new RubyInlineControl("龃龉", "jǔ yǔ", fontSize,
                [new(0, 1, "jǔ"), new(1, 1, "yǔ")]);
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            // 新容器复用相同生产构造路径，检验紧邻词和普通文字没有悬挂遮挡。
            var first = new RubyInlineControl("银行", "yín háng", fontSize, spans[0].Syllables, 1);
            var adjacent = new TextBlock { Text = "去", FontSize = fontSize };
            row.Children.Add(first);
            row.Children.Add(adjacent);
            row.Children.Add(following);
            row.Measure(Size.Infinity);
            row.Arrange(new Rect(row.DesiredSize));
            Check($"real Avalonia syllable word reserves width before adjacent text ({fontSize})",
                first.Bounds.Right <= adjacent.Bounds.Left + 0.1 && adjacent.Bounds.Right <= following.Bounds.Left + 0.1 &&
                following.Children.OfType<TextBlock>().Where(b => b.IsVisible).All(b => b.Bounds.Right <= following.Bounds.Width + 0.1));
            if (fontSize == 17.5)
            {
                var surface = new Border { Background = Brushes.White, Padding = new Thickness(12), Child = viewer };
                viewer.Foreground = Brushes.Black;
                surface.Measure(Size.Infinity);
                surface.Arrange(new Rect(surface.DesiredSize));
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(
                    (int)Math.Ceiling(surface.DesiredSize.Width), (int)Math.Ceiling(surface.DesiredSize.Height)));
                bitmap.Render(surface);
                Directory.CreateDirectory("artifacts");
                bitmap.Save("artifacts/p1-chinese-ruby-layout.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                Check("real Avalonia structured bank render smoke writes PNG",
                    new FileInfo("artifacts/p1-chinese-ruby-layout.png").Length > 100);
            }
        }

        var annotator = new FixtureChineseAnnotator();
        using var registry = new TextAnnotatorRegistry([annotator]);
        var panel = new ResultPanelWindow(registry);
        try
        {
            panel.SetPhoneticsOptions(new PhoneticsConfig { RareOnly = false });
            panel.Reset(source, "structured fixture");
            WaitForAnnotation(panel);
            Check("panel structured source and all full-copy formats preserve literal brackets",
                panel.SourceText == source && panel.GetSourceCopyText(RubyCopyKind.Plain) == source &&
                panel.GetSourceCopyText(RubyCopyKind.Annotated) == "去银行[yín háng][原文]，银行\n龃龉" &&
                panel.GetSourceCopyText(RubyCopyKind.ReadingOnly) == "去yín háng[原文]，银行\n龃龉");
            var viewer = Find<SelectableTextBlock>(panel, "SourceViewer")!;
            viewer.SelectionStart = 1;
            viewer.SelectionEnd = 2;
            var content = panel.GetSourceCopyText(RubyCopyKind.Plain);
            var note = panel.GetSourceCopyText(RubyCopyKind.ReadingOnly);
            var favorites = new InMemoryFavoriteService();
            panel.SetFavoriteService(favorites);
            var bubble = Find<SelectionBubblePopup>(panel, "SelectionBubble")!;
            bubble.ShowAt(viewer, new Rect(0, 0, 8, 8), content, note);
            RaiseClick(bubble.FavoriteBtn);
            Check("structured word selection favorite stores bank and whole reading",
                favorites.GetAllFavorites().Any(f => f.Content == "银行" && f.Note == "yín háng"));
            viewer.ClearSelection();
            RaiseClick(Find<Button>(panel, "AnnotateSourceButton")!);
            Check("hidden ruby retains structured full-copy reading and annotations",
                !panel.IsRubyVisible && panel.GetSourceCopyText(RubyCopyKind.Annotated).Contains("银行[yín háng][原文]") &&
                panel.GetSourceCopyText(RubyCopyKind.ReadingOnly).StartsWith("去yín háng[原文]"));
            panel.EnterEditMode();
            Find<TextBox>(panel, "SourceTextBox")!.Text = "[编辑]去银行";
            panel.CommitEditingSourceIfActive();
            _ = panel.AnnotateSourceAsync();
            WaitForAnnotation(panel);
            Check("editing and reannotation retain literal brackets and new bank offset",
                panel.SourceText == "[编辑]去银行" && panel.GetSourceCopyText(RubyCopyKind.Annotated) == "[编辑]去银行[yín háng]");
        }
        finally { panel.Close(); }
    }

    /// <summary>
    /// 中文多音提示色与 hover 候选菜单：多音字默认注音用警示色、单音字保持灰色；
    /// 悬停结束恢复各自底色（多音回警示色，不是统一抹灰）；候选面板逐条列出读音并用文字标注默认项，不靠颜色单独传达。
    /// </summary>
    private static void TestChineseMultiPronunciationHint()
    {
        EnsureThemedApplication();

        AnnotationSyllable[] syllables =
        [
            new(0, 1, "yín", ["yín"]),
            new(1, 1, "háng", ["háng", "xíng", "hàng"]),
        ];
        var ruby = new RubyInlineControl("银行", "yín háng", 17.5, syllables, 0);
        ruby.Measure(Size.Infinity);
        ruby.Arrange(new Rect(ruby.DesiredSize));

        var yin = ruby.Children.OfType<TextBlock>().Single(b => b.Text == "yín");
        var hang = ruby.Children.OfType<TextBlock>().Single(b => b.Text == "háng");
        var hint = RubyInlineControl.GetHintBrush(ruby);

        Check("single-pronunciation syllable keeps a neutral (non-hint) reading color",
            yin.Foreground is ISolidColorBrush yinBrush && yinBrush.Color != ((ISolidColorBrush)hint).Color);
        Check("multi-pronunciation syllable uses the warning hint color",
            hang.Foreground is ISolidColorBrush hangBrush && hangBrush.Color == ((ISolidColorBrush)hint).Color);

        Check("only the multi-pronunciation syllable contributes candidate entries",
            ruby.HasAlternativeReadings &&
            ruby.CandidateEntries.Count == 3 &&
            ruby.CandidateEntries.All(e => e.BaseChar == "行") &&
            ruby.CandidateEntries.Count(e => e.IsDefault) == 1 &&
            ruby.CandidateEntries.Single(e => e.IsDefault).Reading == "háng" &&
            ruby.CandidateEntries.Any(e => e.Reading == "xíng"));

        // 悬停高亮后恢复：多音字回到提示色，单音字回灰
        var accent = RubyInlineControl.GetAccentBrush(ruby);
        ruby.ApplyReadingHoverForTest(true);
        Check("hovering tints the multi-pronunciation reading with the accent color",
            ((ISolidColorBrush)hang.Foreground!).Color == ((ISolidColorBrush)accent).Color);
        ruby.ApplyReadingHoverForTest(false);
        Check("leaving hover restores the multi-pronunciation hint color instead of flat gray",
            ((ISolidColorBrush)hang.Foreground!).Color == ((ISolidColorBrush)hint).Color &&
            yin.Foreground is ISolidColorBrush yinRestored && yinRestored.Color != ((ISolidColorBrush)hint).Color);

        // hover 候选面板：列出全部读音，本句默认读音响橙色，与原文注音同色
        var tip = ruby.HoverPanelContent;
        var tipTexts = tip is null ? [] : FlattenText(tip).ToArray();
        Check("multi-pronunciation hover panel lists every char reading including the normal 银 alongside multi 行",
            tipTexts.Length > 0 &&
            tipTexts.Contains("yín") &&
            tipTexts.Contains("háng") && tipTexts.Contains("xíng") && tipTexts.Contains("hàng"));
        var defaultReading = FindTextBlock(tip, "háng");
        var altReading = FindTextBlock(tip, "xíng");
        var normalReading = FindTextBlock(tip, "yín");
        Check("hover panel highlights the in-context default reading in the same hint color as the source ruby",
            defaultReading?.Foreground is ISolidColorBrush dr && dr.Color == ((ISolidColorBrush)hint).Color &&
            altReading?.Foreground is ISolidColorBrush ar && ar.Color != ((ISolidColorBrush)hint).Color &&
            normalReading?.Foreground is ISolidColorBrush nr && nr.Color != ((ISolidColorBrush)hint).Color);

        // 悬浮面板容器（单个 Popup 承载的 Border）：去掉橙色描边与淡绿块，仅半透明中性底
        var tipBorder = ruby.HoverPanelBorder;
        Check("hover panel drops the orange frame and green block for a borderless translucent panel",
            tipBorder.BorderThickness == default &&
            tipBorder.Background is ISolidColorBrush panelBg && panelBg.Color.A is > 0 and < 255);

        // 单音字：共用同一套模板，逐字列出唯一读音，无多音高亮，也没有额外备选
        var mono = new RubyInlineControl("银行", "yín háng", 17.5,
            [new(0, 1, "yín", ["yín"]), new(1, 1, "háng", ["háng"])], 0);
        mono.Measure(Size.Infinity);
        var monoTip = mono.HoverPanelContent;
        var monoTexts = monoTip is null ? [] : FlattenText(monoTip).ToArray();
        Check("single-reading word lists its per-char readings with copy footer and no alternatives",
            !mono.HasAlternativeReadings &&
            monoTexts.Contains("yín") && monoTexts.Contains("háng") && !monoTexts.Contains("xíng") &&
            monoTexts.Any(t => t.Contains("点击复制")) && monoTexts.Any(t => t.Contains("右键")));
        Check("multi and single pronunciation panels share the same borderless translucent style",
            mono.HoverPanelBorder.BorderThickness == tipBorder.BorderThickness &&
            mono.HoverPanelBorder.CornerRadius == tipBorder.CornerRadius &&
            mono.HoverPanelBorder.Background is ISolidColorBrush mb && tipBorder.Background is ISolidColorBrush tb &&
            mb.Color.A == tb.Color.A);

        // 布局回归：外层 SelectableTextBlock 固定 LineHeight=36 时，注音控件不得继承行高被撑到整行而溢出原文区
        const string wrapText = "不行就去银行，银行里的人很多，长大以后再说。";
        var wrapSpans = new List<AnnotationSpan>();
        foreach (var word in new[] { "不行", "银行", "人", "多", "长大", "再说" })
        {
            var at = wrapText.IndexOf(word, StringComparison.Ordinal);
            while (at >= 0)
            {
                var syls = word.Select((_, k) => new AnnotationSyllable(at + k, 1, "x", ["x"])).ToArray();
                wrapSpans.Add(new AnnotationSpan(at, word.Length, string.Join(' ', word.Select(_ => "x")), syls));
                at = wrapText.IndexOf(word, at + word.Length, StringComparison.Ordinal);
            }
        }
        var wrapViewer = new SelectableTextBlock
        {
            FontSize = 15, LetterSpacing = 0.8, TextWrapping = TextWrapping.Wrap, LineHeight = 36, Width = 300,
        };
        wrapViewer.Inlines = RubyDocumentBuilder.BuildInlines(wrapText, wrapSpans, 15);
        wrapViewer.Measure(new Size(300, double.PositiveInfinity));
        wrapViewer.Arrange(new Rect(0, 0, 300, wrapViewer.DesiredSize.Height));
        var wrappedRubies = wrapViewer.Inlines.OfType<InlineUIContainer>()
            .Select(c => c.Child).OfType<RubyInlineControl>().ToArray();
        Check("ruby controls never inherit the fixed line height and stay inside the source region",
            wrappedRubies.Length > 0 && wrappedRubies.All(r =>
            {
                var p = r.TranslatePoint(new Point(0, 0), wrapViewer);
                return p is { } pt && pt.Y >= -0.5 && r.Bounds.Height <= 36.5 &&
                       pt.Y + r.Bounds.Height <= wrapViewer.DesiredSize.Height + 0.5;
            }),
            string.Join(";", wrappedRubies.Select(r =>
            {
                var p = r.TranslatePoint(new Point(0, 0), wrapViewer);
                return $"{r.BaseText}@y={p?.Y:F0} h={r.Bounds.Height:F0}";
            })));
    }

    /// <summary>在控件子树里按文本查找首个 TextBlock，供候选面板断言。</summary>
    private static TextBlock? FindTextBlock(Control? root, string text)
    {
        if (root is null)
        {
            return null;
        }

        if (root is TextBlock tb && tb.Text == text)
        {
            return tb;
        }

        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Border { Child: Control only } => [only],
            ContentControl { Content: Control content } => [content],
            _ => [],
        };

        foreach (var child in children)
        {
            var match = FindTextBlock(child, text);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>深度优先收集控件子树内所有 TextBlock 文本，供 hover 候选面板断言。</summary>
    private static IEnumerable<string> FlattenText(Control root)
    {
        if (root is TextBlock { Text: { Length: > 0 } text })
        {
            yield return text;
        }

        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Border { Child: Control only } => [only],
            ContentControl { Content: Control content } => [content],
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var nested in FlattenText(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>可控的词典边界替身：返回真实形状的区间，允许确定性地暂停旧请求。</summary>
    private sealed class FixtureChineseAnnotator : ITextAnnotator
    {
        public string LanguageCode => "zh";
        public bool IsAvailable => true;
        public int Calls;
        public ManualResetEventSlim? Started;
        public ManualResetEventSlim? Release;
        public void ResetAvailability() { }
        public int CountWords(string plainText) => 10;
        public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context)
        {
            Interlocked.Increment(ref Calls);
            var release = Release;
            Started?.Set();
            if (release is not null && !release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture release");
            var start = plainText.IndexOf("银行", StringComparison.Ordinal);
            return start >= 0 && context.ShouldEmit("银行", "yín háng")
                ? [new(start, 2, "yín háng", [new(start, 1, "yín"), new(start + 1, 1, "háng")])] : [];
        }
    }

    /// <summary>缓存配置快照与请求取消覆盖同文重置、语言、编辑、关闭及后发请求。</summary>
    private static void TestAnnotationInvalidation()
    {
        var annotator = new FixtureChineseAnnotator();
        using var registry = new TextAnnotatorRegistry([annotator]);
        var panel = new ResultPanelWindow(registry);
        try
        {
            var options = new PhoneticsConfig { RareOnly = false };
            panel.SetPhoneticsOptions(options);
            panel.Reset("去银行", "cache");
            WaitForAnnotation(panel);
            var firstCalls = annotator.Calls;
            _ = panel.AnnotateSourceAsync();
            WaitForAnnotation(panel);
            Check("same source and settings reuse panel annotation cache", annotator.Calls == firstCalls);
            options.RareOnly = true;
            panel.SetPhoneticsOptions(options);
            _ = panel.AnnotateSourceAsync();
            WaitForAnnotation(panel);
            Check("mutated config snapshot invalidates cache and reapplies rare-only filter",
                annotator.Calls == firstCalls + 1 && panel.AnnotatedSourceText == "去银行");
            options.RareOnly = false;
            panel.SetPhoneticsOptions(options);
            _ = panel.AnnotateSourceAsync();
            WaitForAnnotation(panel);
            Check("config change restores structured result for same source",
                panel.AnnotatedSourceText == "去银行[yín háng]");
        }
        finally { panel.Close(); }

        foreach (var change in new[] { "reset", "config", "language", "target", "edit", "close", "new request" })
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var controlled = new FixtureChineseAnnotator { Started = started, Release = release };
            using var controlledRegistry = new TextAnnotatorRegistry([controlled]);
            var current = new ResultPanelWindow(controlledRegistry);
            current.SetPhoneticsOptions(new PhoneticsConfig { RareOnly = false });
            current.Reset("去银行", "old request");
            var oldTask = current.PendingAnnotation!;
            Check($"old annotation reached controlled dictionary before {change}", started.Wait(TimeSpan.FromSeconds(5)));
            switch (change)
            {
                case "reset": current.Reset("去银行", "same source new request", autoAnnotate: false); break;
                case "config": current.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false }); break;
                case "language": Find<ComboBox>(current, "SourceLanguageBox")!.SelectedIndex = 2; break;
                case "target": current.SetTargetLanguage("English"); break;
                case "edit": current.EnterEditMode(); break;
                case "close": current.Close(); break;
                case "new request":
                    current.SetPhoneticsOptions(new PhoneticsConfig { RareOnly = true });
                    controlled.Started = null;
                    controlled.Release = null;
                    _ = current.AnnotateSourceAsync(manual: true);
                    WaitForAnnotation(current);
                    break;
            }
            release.Set();
            while (!oldTask.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            oldTask.GetAwaiter().GetResult();
            Check($"old same-source annotation cannot write after {change}",
                current.SourceText == "去银行" && current.AnnotatedSourceText == "去银行");
            if (change != "close") current.Close();
        }
    }

    /// <summary>关闭面板必须解除全局和收藏服务订阅，避免长生命周期事件保留整个窗口。</summary>
    private static void TestResultPanelSubscriptionCleanup()
    {
        EnsureThemedApplication();
        var service = new InMemoryFavoriteService();
        var rubyBefore = SubscriberCount(typeof(RubyInlineControl), null, "StatusNotificationRequested");
        var themeBefore = SubscriberCount(typeof(NaniKore.App.Theme.ThemeManager), null, "ThemeChanged");
        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(service);
        Check("result panel subscribes to ruby notifications",
            SubscriberCount(typeof(RubyInlineControl), null, "StatusNotificationRequested") == rubyBefore + 1);
        Check("explanation subscribes to favorite service",
            SubscriberCount(typeof(InMemoryFavoriteService), service, "FavoriteAdded") == 1);
        panel.Close();
        Check("closed panel removes ruby and theme subscriptions",
            SubscriberCount(typeof(RubyInlineControl), null, "StatusNotificationRequested") == rubyBefore &&
            SubscriberCount(typeof(NaniKore.App.Theme.ThemeManager), null, "ThemeChanged") == themeBefore);
        Check("closed panel removes all favorite service subscriptions",
            SubscriberCount(typeof(InMemoryFavoriteService), service, "FavoriteAdded") == 0 &&
            SubscriberCount(typeof(InMemoryFavoriteService), service, "FavoriteRemoved") == 0 &&
            SubscriberCount(typeof(InMemoryFavoriteService), service, "FavoriteUpdated") == 0);
    }

    // 仅在自测中检查事件根；窗口最终回收时机还受平台渲染队列影响，不以即时 GC 作为契约。
    private static int SubscriberCount(
        // BindingFlags 含运行期分支，裁剪分析器要求同时声明公共与非公共字段的保留范围。
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields)] Type type,
        object? instance,
        string eventName)
    {
        var field = type.GetField(eventName, System.Reflection.BindingFlags.NonPublic |
            (instance is null ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance));
        if (field is null)
        {
            throw new InvalidOperationException($"Missing event field: {type.Name}.{eventName}");
        }
        return (field.GetValue(instance) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>
    /// 等待注音任务落地：无头环境下持续抽干 Dispatcher 队列，避免死等。
    /// </summary>
    private static void WaitForAnnotation(ResultPanelWindow panel)
    {
        var task = panel.PendingAnnotation;
        if (task is null)
        {
            return;
        }

        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// 结果面板本地注音接线：源语言下拉四选一（自动探测 + 三语），
    /// Reset 落地即单次异步叠加注音；注音形态只是纯文本原文的派生视图，纯文本始终是单一事实源；
    /// 开关关闭时软降级为纯文本，手动换语言时按新口径重算。
    /// </summary>
    private static void TestResultPanelSourceAnnotation()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        var languageBox = Find<ComboBox>(panel, "SourceLanguageBox");
        Check(
            "result panel source language box offers auto detection plus the three annotation languages",
            languageBox is { Items.Count: 4 });

        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = true, RareOnly = true });
        const string source = "彼は躊躇なく答えた。";
        panel.Reset(source, "test", autoTranslate: false);
        WaitForAnnotation(panel);
        Check(
            "local annotation overlays ruby while the plain source stays the single source of truth",
            panel.AnnotatedSourceText.Length > 0 &&
            string.Equals(panel.SourceText, source, StringComparison.Ordinal) &&
            string.Equals(RubyTextParser.ToPlainText(panel.AnnotatedSourceText), source, StringComparison.Ordinal));

        // 手动换语言（索引 3 = 中文（简体））：同一份纯文本按新口径重算，事实源不变
        languageBox!.SelectedIndex = 3;
        WaitForAnnotation(panel);
        Check(
            "manual language switch re-annotates the same plain source without losing the source of truth",
            panel.AnnotatedSourceText.Length > 0 &&
            string.Equals(panel.SourceText, source, StringComparison.Ordinal) &&
            string.Equals(RubyTextParser.ToPlainText(panel.AnnotatedSourceText), source, StringComparison.Ordinal));

        // 总开关关闭：软降级为纯文本，不再叠加任何注音
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false, RareOnly = true });
        panel.Reset(source, "test", autoTranslate: false);
        WaitForAnnotation(panel);
        Check(
            "disabling annotation degrades the source view back to plain text",
            string.Equals(panel.AnnotatedSourceText, source, StringComparison.Ordinal));
    }

    /// <summary>自动探测标签随原文更新，独立于注音开关，并保持手选语言的粘性。</summary>
    private static void TestResultPanelDetectedSourceLanguage()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false });
        var languageBox = Find<ComboBox>(panel, "SourceLanguageBox")!;

        panel.Reset("こんにちは", "test", autoAnnotate: false);
        Check("auto language displays detected Japanese even with annotation disabled",
            languageBox.SelectedIndex == 0 && languageBox.SelectedItem?.ToString() == "日本語（自动）");

        panel.Reset("Hello world", "test", autoAnnotate: false);
        Check("auto language detects new source instead of pinning the previous result",
            languageBox.SelectedIndex == 0 && languageBox.SelectedItem?.ToString() == "English（自动）");

        languageBox.SelectedIndex = 3;
        panel.Reset("こんにちは", "test", autoAnnotate: false);
        Check("source changes preserve the manually selected language",
            languageBox.SelectedIndex == 3 && languageBox.SelectedItem?.ToString() == "中文（简体）");

        languageBox.SelectedIndex = 0;
        Check("switching back to auto displays the current detection",
            languageBox.SelectedItem?.ToString() == "日本語（自动）");

        panel.Reset(string.Empty, "test", autoAnnotate: false);
        Check("empty source clears the previous detected language",
            languageBox.SelectedIndex == 0 && languageBox.SelectedItem?.ToString() == "自动探测");

        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "Hello world"));
        Check("streamed transcription updates the detected source language",
            languageBox.SelectedIndex == 0 && languageBox.SelectedItem?.ToString() == "English（自动）");
    }

    /// <summary>
    /// 结果面板双语态与翻译流：振假名视图（SelectableTextBlock）与纯文本编辑框一键互切，
    /// 纯文本原文始终是单一事实源。未产出有效译文前主按钮固定「翻译」，
    /// 有译文后再实质修改即升级为「重译」；点击翻译/重译时编辑内容立即提交为事实源。
    /// </summary>
    private static void TestResultPanelRubyDualModeAndTranslationFlow()
    {
        EnsureThemedApplication();

        // 1. AutoTranslate == true：直接以阅读态呈现，初始按钮为「翻译」
        var panel = new ResultPanelWindow();
        panel.Reset("こ こ は  通れないよ", "OCR 完成", autoTranslate: true);
        var viewerBorder = Find<Border>(panel, "SourceViewerBorder");
        var sourceBox = Find<TextBox>(panel, "SourceTextBox");
        var translateButton = Find<Button>(panel, "TranslateButton");
        Check(
            "result panel with autoTranslate true displays SelectableTextBlock viewer and translate button initially",
            viewerBorder?.IsVisible == true &&
            sourceBox?.IsVisible == false &&
            translateButton?.Content as string == "翻译" &&
            panel.SourceText == "こ こ は  通れないよ");

        // 2. AutoTranslate == false：同样保持阅读态，消除垂直居中伪编辑状态
        var panelManual = new ResultPanelWindow();
        panelManual.Reset("こ こ は  通れないよ", "OCR 完成", autoTranslate: false);
        Check(
            "result panel with autoTranslate false displays SelectableTextBlock viewer initially",
            Find<Border>(panelManual, "SourceViewerBorder")?.IsVisible == true &&
            Find<TextBox>(panelManual, "SourceTextBox")?.IsVisible == false &&
            Find<Button>(panelManual, "TranslateButton")?.Content as string == "翻译" &&
            panelManual.SourceText == "こ こ は  通れないよ");

        // 3. 双击/EnterEditMode 原地切换编辑态：严禁全选且定位光标在末尾
        panel.EnterEditMode();
        Check(
            "switching to text editor preserves caret and avoids selecting all text",
            sourceBox?.IsVisible == true &&
            viewerBorder?.IsVisible == false &&
            Math.Abs(sourceBox.SelectionEnd - sourceBox.SelectionStart) == 0 &&
            sourceBox.CaretIndex == (sourceBox.Text?.Length ?? 0));

        // 4. 编辑态未修改失焦：自动还原阅读态
        sourceBox?.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Check(
            "unmodified text editor restores SelectableTextBlock viewer on lost focus",
            viewerBorder?.IsVisible == true &&
            sourceBox?.IsVisible == false);

        // 5. 编辑态实质修改与规范化：未产出有效译文前，主按钮固定为「翻译」
        var normalizeButton = Find<Button>(panel, "NormalizeSourceButton");
        if (normalizeButton is not null)
        {
            RaiseClick(normalizeButton);
        }
        Check(
            "normalizing source before any translation keeps translate button content as 翻译",
            translateButton?.Content as string == "翻译");

        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "ここは通れません";
            sourceBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        }

        Check(
            "substantively modified text editor remains active and stays as 翻译 when no prior translation exists",
            sourceBox?.IsVisible == true &&
            viewerBorder?.IsVisible == false &&
            translateButton?.Content as string == "翻译" &&
            panel.SourceText == "ここは通れません");

        // 6. 流式接收 SourceText 增量更新振假名与纯文本，翻译完成后保持「翻译」
        panel.BeginTranslation();
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在分析复合适词与时态"));
        var translationBox = Find<TextBox>(panel, "TranslationTextBox");
        Check(
            "thinking delta displays throttled thinking placeholder in TranslationTextBox",
            translationBox?.Text?.StartsWith("💭 [思考中]") == true &&
            translationBox.Text.Contains("正在分析复合适词与时态"));

        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "私[わたし]は"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, " 学生[がくせい]"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "です。"));
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "我是学生。"));
        panel.SetTranslationInProgress(false);
        Check(
            "streaming source text delta updates flow document and plain text in real time, and finishes with translate button",
            viewerBorder?.IsVisible == true &&
            panel.SourceText == "私は 学生です。" &&
            sourceBox?.Text == "私は 学生です。" &&
            translateButton?.Content as string == "翻译" &&
            translationBox?.Text == "我是学生。" &&
            !(translationBox.Text?.Contains("思考中") ?? false));

        // 7. 产出有效译文后，再次实质修改或规范化激活「重译」；点击时编辑内容立即提交为事实源
        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "私[わたし]は 先生[せんせい]です。";
            sourceBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        }
        Check(
            "substantively modified text editor triggers 重译 when previous translation exists",
            translateButton?.Content as string == "重译");

        var translateRequestedRaised = false;
        panel.TranslateRequested += (_, _) => translateRequestedRaised = true;
        if (translateButton is not null)
        {
            RaiseClick(translateButton);
        }
        Check(
            "clicking 重译 raises TranslateRequested with modified text intact",
            translateRequestedRaised &&
            panel.SourceText == "私[わたし]は 先生[せんせい]です。" &&
            viewerBorder?.IsVisible == true &&
            sourceBox?.IsVisible == false);

        panel.BeginTranslation();
        Check(
            "begin translation preserves modified source instead of reverting to initial source",
            panel.SourceText == "私[わたし]は 先生[せんせい]です。");
    }

    /// <summary>旧请求仍在取消时，新输入必须立即恢复操作状态，允许手动开始下一次翻译。</summary>
    private static void TestResultPanelResetDuringTranslation()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        try
        {
            var translate = Find<Button>(panel, "TranslateButton")!;
            var normalize = Find<Button>(panel, "NormalizeSourceButton")!;
            var annotate = Find<Button>(panel, "AnnotateSourceButton")!;
            var explanation = Find<Expander>(panel, "ExplanationExpander")!;
            var metrics = Find<TextBlock>(panel, "MetricsTextBlock")!;
            var error = Find<Expander>(panel, "ErrorDetailsExpander")!;
            foreach (var oldChannel in new[] { DeltaChannel.Thinking, DeltaChannel.Translation })
            {
                panel.Reset("输入 A", "等待手动翻译", autoTranslate: false, autoAnnotate: false);
                panel.BeginTranslation();
                panel.SetTranslationInProgress(true);
                panel.Append(new UnderstandDelta(oldChannel, "A 的未完成内容"));
                Check($"translation A disables all actions ({oldChannel})",
                    !translate.IsEnabled && !normalize.IsEnabled && !annotate.IsEnabled);

                // 宿主已使 A 失效，其 finally 不再负责面板；Reset 必须独立完成新输入的复位。
                panel.Reset("输入 B", "等待手动翻译", autoTranslate: false, autoAnnotate: false);
                Check($"new input restores all manual actions before A finishes ({oldChannel})",
                    translate.IsEnabled && normalize.IsEnabled && annotate.IsEnabled &&
                    translate.Content as string == "翻译");
                Check($"new input clears old output and auxiliary state ({oldChannel})",
                    panel.SourceText == "输入 B" && string.IsNullOrEmpty(panel.TranslationText) &&
                    !explanation.IsEnabled && !explanation.IsExpanded && !metrics.IsVisible && !error.IsVisible);

                string? requestedSource = null;
                EventHandler start = (_, _) =>
                {
                    requestedSource = panel.SourceText;
                    panel.BeginTranslation();
                    panel.SetTranslationInProgress(true);
                };
                panel.TranslateRequested += start;
                RaiseClick(translate);
                panel.TranslateRequested -= start;
                Check($"manual translation starts with B and disables actions ({oldChannel})",
                    requestedSource == "输入 B" && !translate.IsEnabled && !normalize.IsEnabled && !annotate.IsEnabled);
                panel.Append(new UnderstandDelta(DeltaChannel.Translation, "B 的译文"));
                panel.SetTranslationInProgress(false);
                Check($"translation B completes with usable actions ({oldChannel})",
                    panel.SourceText == "输入 B" && panel.TranslationText == "B 的译文" &&
                    translate.IsEnabled && normalize.IsEnabled && annotate.IsEnabled);
            }
        }
        finally
        {
            panel.Close();
        }
    }

    /// <summary>
    /// 独立分区高度调节契约：原文框与译文框各有一个底边调节把手，
    /// 拖动某把手只增减对应框的像素高度，互不影响；
    /// 精讲区不设把手，展开时独占窗口剩余高度（星号行），折叠时回 Auto 不撑页面；
    /// 页面不足一屏时用 MinHeight 撑满滚动视口，超出整页滚动。
    /// </summary>
    private static void TestResultPanelViewportLayout()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false, RareOnly = true });
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "这里过不去哦。"));
        panel.SetMetrics(new TranslationMetrics(320, 45, 680, 1750, 42.1));

        var scroller = Find<ScrollViewer>(panel, "ContentScrollViewer");
        var page = Find<Grid>(panel, "PageGrid");
        var sourceThumb = Find<Border>(panel, "SourceResizeThumb");
        var translationThumb = Find<Border>(panel, "TranslationResizeThumb");

        Check(
            "page hosts two independent region grips, both panes are pixel-height rows in the scroll viewer",
            page is not null &&
            sourceThumb is not null &&
            translationThumb is not null &&
            scroller is not null &&
            scroller.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto &&
            scroller.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled &&
            page.RowDefinitions.Count >= 6 &&
            page.RowDefinitions[1].Height.GridUnitType == GridUnitType.Pixel &&
            page.RowDefinitions[4].Height.GridUnitType == GridUnitType.Pixel);

        var sourceBefore = page!.RowDefinitions[1].Height.Value;
        var translationBefore = page!.RowDefinitions[4].Height.Value;

        // 原文把手向下调整 60 像素
        page.RowDefinitions[1].Height = new GridLength(sourceBefore + 60, GridUnitType.Pixel);
        Check(
            "dragging the source grip resizes only the source pane, translation untouched",
            Math.Abs(page.RowDefinitions[1].Height.Value - (sourceBefore + 60)) < 0.5 &&
            Math.Abs(page.RowDefinitions[4].Height.Value - translationBefore) < 0.5);

        // 译文把手向下调整 60 像素
        var sourceAfterSourceDrag = page.RowDefinitions[1].Height.Value;
        page.RowDefinitions[4].Height = new GridLength(translationBefore + 60, GridUnitType.Pixel);
        Check(
            "dragging the translation grip resizes only the translation pane, source untouched",
            Math.Abs(page.RowDefinitions[4].Height.Value - (translationBefore + 60)) < 0.5 &&
            Math.Abs(page.RowDefinitions[1].Height.Value - sourceAfterSourceDrag) < 0.5);

        // 精讲区：折叠不占位，展开即独吞剩余高度；页面 MinHeight 绑定滚动视口
        var expander = Find<Expander>(panel, "ExplanationExpander");
        Check(
            "collapsed explanation row keeps Auto height so no blank space stretches the page",
            page.RowDefinitions[6].Height.GridUnitType == GridUnitType.Auto);

        expander!.IsExpanded = true;
        Check(
            "expanding explanation turns its row into the single star row claiming all leftover height",
            page.RowDefinitions[6].Height.GridUnitType == GridUnitType.Star &&
            page.RowDefinitions[6].Height.Value == 1 &&
            page.RowDefinitions.Count(row => row.Height.IsStar) == 1 &&
            page.RowDefinitions[1].Height.GridUnitType == GridUnitType.Pixel &&
            page.RowDefinitions[4].Height.GridUnitType == GridUnitType.Pixel);

        expander.IsExpanded = false;
        Check(
            "collapsing explanation returns its row to Auto again",
            page.RowDefinitions[6].Height.GridUnitType == GridUnitType.Auto &&
            page.MinHeight >= 0);

        // 真实布局：窗口越高，精讲区拿到的剩余高度越大（前两段仍锁像素高度）
        var explanationView = Find<ExplanationCardView>(panel, "ExplanationView")!;
        expander.IsExpanded = true;
        var tallHeight = MeasureExplanationHeight(panel, page, explanationView, 900, 1100);
        var shortHeight = MeasureExplanationHeight(panel, page, explanationView, 900, 1000);
        Check(
            "explanation area grows with the window while source and translation keep their pixel heights",
            tallHeight > 150 &&
            tallHeight - shortHeight is > 50 and < 150 &&
            Math.Abs(page.RowDefinitions[1].Height.Value - (sourceBefore + 60)) < 0.5,
            $"tall={tallHeight:F0} short={shortHeight:F0}");

        // 长精讲也必须保持窗口分配的高度，超出的正文留在内部滚动。
        explanationView.SetText("## 语境\n" + string.Join("\n\n", Enumerable.Repeat("这是一段用于验证精讲内部滚动的长文本。", 100)));
        var longContentHeight = MeasureExplanationHeight(panel, page, explanationView, 900, 1000);
        Check(
            "long explanation stays within the allocated region",
            Math.Abs(longContentHeight - shortHeight) < 1,
            $"short={shortHeight:F0} long={longContentHeight:F0}");
        // 小窗口或把手拉高时保留最小精讲高度，通过整页滚动访问底部。
        var compactHeight = MeasureExplanationHeight(panel, page, explanationView, 900, 400);
        Check(
            "small viewport keeps explanation readable and allows whole-page scrolling",
            compactHeight >= explanationView.MinHeight && page.Bounds.Height > 400,
            $"explanation={compactHeight:F0} page={page.Bounds.Height:F0}");

        expander.IsExpanded = false;
        MeasureExplanationHeight(panel, page, explanationView, 900, 1000);
        Check(
            "collapsed explanation releases the viewport minimum height",
            page.MinHeight == 0 && page.RowDefinitions[6].Height.IsAuto);
    }

    /// <summary>
    /// 结果面板定位几何（issue 047 R07）：优先落在选区下方、下方不足上移、横向夹在工作区内；
    /// 窗口大于工作区时（小屏、任务栏压缩工作区、大屏拉宽后移至小屏）不得抛异常，
    /// 退化为对齐工作区左上角，保证标题栏与缩放握柄可达；负坐标副屏与混合 DPI 物理像素同样成立。
    /// </summary>
    private static void TestResultPanelPlacementGeometry()
    {
        // 常规几何：窗口放得下，贴在选区下方，横向夹取
        var work = new PixelRect(0, 0, 1920, 1040);
        var below = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(100, 100, 200, 40), work, 960, 660);
        Check("placement prefers right below the selection",
            below.X == 100 && below.Y == 148);

        var rightEdge = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(1900, 100, 200, 40), work, 960, 660);
        Check("placement clamps x to the work area right edge",
            rightEdge.X == work.Right - 960 && rightEdge.Y == 148);

        var flipped = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(100, 900, 200, 100), work, 960, 660);
        Check("placement flips above the selection when below does not fit",
            flipped.Y == 900 - 8 - 660 && flipped.X == 100);

        // 超高窗口：小笔记本工作区高度不足，对齐工作区顶部而不是抛异常
        var tiny = new PixelRect(0, 0, 1366, 552);
        var tooTall = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(100, 100, 200, 40), tiny, 960, 660);
        Check("window taller than the work area aligns to its top instead of throwing",
            tooTall.Y == tiny.Y && tooTall.X == 100);

        // 超宽窗口：大屏拉宽后移至小屏（高度仍放得下，只验证横向对齐）
        var tooWide = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(100, 100, 200, 40), tiny, 1400, 400);
        Check("window wider than the work area aligns to its left instead of throwing",
            tooWide.X == tiny.X && tooWide.Y == 148);

        // 两个方向都超出：落在工作区原点
        var tooBig = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(500, 400, 200, 40), tiny, 1600, 800);
        Check("window larger than the work area on both axes lands on the work area origin",
            tooBig.X == tiny.X && tooBig.Y == tiny.Y);

        // 负坐标副屏（主屏左侧）与更高 DPI 的物理像素工作区
        var leftScreen = new PixelRect(-1920, -100, 1600, 900);
        var negative = ResultPanelWindow.ResolvePanelPlacement(new PixelRect(-1800, -50, 200, 40), leftScreen, 960, 660);
        Check("negative-coordinate secondary screen keeps the panel fully inside its work area",
            negative.X >= leftScreen.X && negative.X + 960 <= leftScreen.Right &&
            negative.Y >= leftScreen.Y && negative.Y + 660 <= leftScreen.Bottom);

        // 边界几何矩阵：任何工作区/窗口/选区组合都不得抛异常，且窗口始终与工作区相交（可见、可操作）
        var areas = new[] { work, tiny, leftScreen, new PixelRect(-3840, 0, 1280, 720), new PixelRect(0, 0, 1024, 600) };
        var sizes = new[] { (440, 300), (960, 660), (1400, 660), (1600, 900) };
        PixelRect[] selections =
        [
            new(0, 0, 10, 10),
            new(int.MinValue / 4, int.MinValue / 4, 10, 10),
            new(1920, 1040, 10, 10),
        ];
        var matrixFailures = 0;
        foreach (var area in areas)
        {
            foreach (var (windowWidth, windowHeight) in sizes)
            {
                foreach (var selection in selections)
                {
                    var point = ResultPanelWindow.ResolvePanelPlacement(selection, area, windowWidth, windowHeight);
                    var intersectsWorkArea = point.X < area.Right && point.X + windowWidth > area.X &&
                                             point.Y < area.Bottom && point.Y + windowHeight > area.Y;
                    // 放得下时必须完整落入工作区；放不下时至少覆盖工作区，标题栏仍可达
                    var fullyInside = point.X >= area.X && point.X + windowWidth <= area.Right &&
                                      point.Y >= area.Y && point.Y + windowHeight <= area.Bottom;
                    var fits = windowWidth <= area.Width && windowHeight <= area.Height;
                    if (!intersectsWorkArea || (fits && !fullyInside))
                    {
                        matrixFailures++;
                    }
                }
            }
        }

        Check("placement geometry matrix never throws and always keeps the panel reachable",
            matrixFailures == 0,
            $"failures={matrixFailures}");
    }

    /// <summary>模拟滚动视口完成页面测量/排列，返回精讲卡片实际高度。</summary>
    private static double MeasureExplanationHeight(ResultPanelWindow panel, Grid page, ExplanationCardView view, double width, double height)
    {
        panel.ApplyPageMinHeight(height);
        // 模拟纵向滚动器：无限高度测量；页面顶端对齐，由内容或最小高度决定尺寸。
        page.Measure(new Size(width, double.PositiveInfinity));
        page.Arrange(new Rect(0, 0, width, page.DesiredSize.Height));
        return view.Bounds.Height;
    }

    /// <summary>自动开关控制所有自动请求；手动注音独立可用，总开关仍有最高优先级。</summary>
    private static void TestAutomaticAnnotationPolicy()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = true });
        const string source = "彼は躊躇なく答えた。";
        panel.Reset(source, "test", autoAnnotate: false);
        panel.AnnotateSourceAsync().GetAwaiter().GetResult();
        Check("translation completion cannot bypass disabled automatic annotation",
            panel.AnnotatedSourceText == source && !panel.IsRubyVisible);

        // 视觉路径起初无原文，随后原文流到达，也必须继续遵守自动开关。
        panel.Reset(string.Empty, "test", autoAnnotate: false);
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, source));
        panel.AnnotateSourceAsync().GetAwaiter().GetResult();
        Check("vision source arriving later does not bypass disabled automatic annotation",
            panel.AnnotatedSourceText == source && !panel.IsRubyVisible);

        panel.AnnotateSourceAsync(manual: true);
        WaitForAnnotation(panel);
        if (PhoneticAnnotatorRegistryHolder.Default.GetAnnotator("ja")?.IsAvailable == true)
        {
            Check("manual annotation still works when automatic annotation is off",
                panel.AnnotatedSourceText.Contains('['));
        }
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false });
        panel.Reset(source, "test", autoAnnotate: true);
        panel.AnnotateSourceAsync(manual: true).GetAwaiter().GetResult();
        Check("annotation master switch blocks manual and automatic annotation",
            panel.AnnotatedSourceText == source);
        panel.Close();
    }

    /// <summary>
    /// 显式注音按钮契约（与翻译解耦）：按钮位居「规范化」与「翻译」之间；
    /// 初始纯文本编辑态点击即提交事实源并切到假名阅读态，全程不发起翻译；
    /// 空原文温和提示；注音总开关关闭时软降级并指向设置。
    /// </summary>
    private static void TestResultPanelAnnotateButton()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = true, RareOnly = true });
        panel.Reset("彼は躊躇なく答えた。", "OCR 完成", autoTranslate: false);

        var annotateButton = Find<Button>(panel, "AnnotateSourceButton");
        var normalizeButton = Find<Button>(panel, "NormalizeSourceButton");
        var translateButton = Find<Button>(panel, "TranslateButton");
        var viewerBorder = Find<Border>(panel, "SourceViewerBorder");
        var statusBar = Find<TextBlock>(panel, "StatusTextBlock");

        // 工具栏顺序契约：规范化 → 注音 → 翻译
        Check(
            "annotate button sits between normalize and translate in the toolbar",
            annotateButton is not null &&
            normalizeButton is not null &&
            translateButton is not null &&
            annotateButton.Parent is StackPanel toolbar &&
            toolbar.Children.IndexOf(normalizeButton) is var normalizeIndex && normalizeIndex >= 0 &&
            toolbar.Children.IndexOf(annotateButton) == normalizeIndex + 1 &&
            toolbar.Children.IndexOf(translateButton) == normalizeIndex + 2);

        // 初始为纯文本编辑态：点注音即切到假名阅读态，且不发起翻译
        var translateRequested = false;
        panel.TranslateRequested += (_, _) => translateRequested = true;
        if (annotateButton is not null)
        {
            RaiseClick(annotateButton);
        }

        WaitForAnnotation(panel);
        Check(
            "clicking annotate switches plain edit mode into ruby reading view without translating",
            viewerBorder?.IsVisible == true &&
            !translateRequested &&
            string.Equals(panel.SourceText, "彼は躊躇なく答えた。", StringComparison.Ordinal));

        if (PhoneticAnnotatorRegistryHolder.Default.GetAnnotator("ja")?.IsAvailable == true)
        {
            Check(
                "clicking annotate overlays furigana while the plain source stays the single source of truth",
                panel.AnnotatedSourceText.Contains('[') &&
                string.Equals(RubyTextParser.ToPlainText(panel.AnnotatedSourceText), "彼は躊躇なく答えた。", StringComparison.Ordinal));
        }
        else
        {
            Skip("ja 词典未就绪（deps/phonetics/ja），跳过注音按钮的假名叠加断言");
        }

        // 空原文：温和提示
        panel.Reset(string.Empty, "等待 OCR", autoTranslate: false);
        if (annotateButton is not null)
        {
            RaiseClick(annotateButton);
        }

        Check(
            "annotate with empty source shows a gentle hint instead of failing",
            statusBar?.Text == "没有可注音的原文。");

        // 总开关关闭：软降级并提示
        panel.Reset("彼は躊躇なく答えた。", "OCR 完成", autoTranslate: false);
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = false, RareOnly = true });
        if (annotateButton is not null)
        {
            RaiseClick(annotateButton);
        }

        Check(
            "annotate with phonetics disabled degrades gracefully with a settings hint",
            statusBar?.Text?.Contains("设置-通用") == true &&
            string.Equals(panel.SourceText, "彼は躊躇なく答えた。", StringComparison.Ordinal));
    }

    /// <summary>
    /// 短文本全注豁免：可注音候选词不足 10 个时不再过滤难词，每个候选词都带读音。
    /// </summary>
    private static void TestResultPanelShortTextAnnotateAll()
    {
        EnsureThemedApplication();

        if (PhoneticAnnotatorRegistryHolder.Default.GetAnnotator("ja")?.IsAvailable != true)
        {
            Skip("ja 词典未就绪（deps/phonetics/ja），跳过短文本全注端到端断言");
            return;
        }

        var panel = new ResultPanelWindow();
        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = true, RareOnly = true });

        panel.Reset("日本語を勉強する", "OCR 完成", autoTranslate: false);
        WaitForAnnotation(panel);
        Check(
            "short source annotates every candidate word instead of filtering common ones",
            panel.AnnotatedSourceText == "日本語[にほんご]を勉強[べんきょう]する",
            $"actual={panel.AnnotatedSourceText}");

        const string longSource = "先生は日本語で問題を説明して、生徒たちは静かに聞いていた。その後みんなで復习を始めた。";
        panel.Reset(longSource, "OCR 完成", autoTranslate: false);
        WaitForAnnotation(panel);
        Check(
            "long source keeps the rare-only filtering for common words",
            panel.AnnotatedSourceText.Contains("説明[せつめい]") &&
            !panel.AnnotatedSourceText.Contains("先生[") &&
            !panel.AnnotatedSourceText.Contains("日本語[") &&
            !panel.AnnotatedSourceText.Contains("問題["),
            $"actual={panel.AnnotatedSourceText}");

        Check(
            "short-text annotation keeps the plain source untouched",
            string.Equals(panel.SourceText, longSource, StringComparison.Ordinal) &&
            string.Equals(RubyTextParser.ToPlainText(panel.AnnotatedSourceText), longSource, StringComparison.Ordinal));
    }

    /// <summary>底部指标条契约：SetMetrics 拼接摘要并显示，BeginTranslation 清空并折叠。</summary>
    private static void TestResultPanelMetricsBar()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        var metricsBar = Find<TextBlock>(panel, "MetricsTextBlock");
        var metricsBlock = Find<TextBlock>(panel, "MetricsTextBlock");
        var statusBlock = Find<TextBlock>(panel, "StatusTextBlock")!;
        var footer = Find<Grid>(panel, "FooterGrid")!;
        Check(
            "footer places plain metrics on the left and status on the right outside the scrolling page",
            metricsBlock?.Parent == footer && statusBlock.Parent == footer &&
            Grid.GetColumn(metricsBlock!) == 0 && Grid.GetColumn(statusBlock) == 1 &&
            Grid.GetRow(footer) == 2 && footer.Parent == Find<ScrollViewer>(panel, "ContentScrollViewer")?.Parent);
        panel.SetError("服务暂不可用", "完整错误信息");
        Check("footer preserves error emphasis and details",
            statusBlock.Text == "服务暂不可用" && statusBlock.Classes.Contains("status-error") &&
            Find<Expander>(panel, "ErrorDetailsExpander")?.IsVisible == true);
        panel.SetStatus(string.Empty);
        Check("empty status hides text and clears error details",
            !statusBlock.IsVisible && !statusBlock.Classes.Contains("status-error") &&
            Find<Expander>(panel, "ErrorDetailsExpander")?.IsVisible == false);
        Check(
            "metrics bar is initially collapsed",
            metricsBar?.IsVisible == false);

        panel.SetMetrics(new TranslationMetrics(320, 45, 680, 1750, 42.1));
        Check(
            "set metrics displays formatted summary and makes metrics bar visible",
            metricsBar?.IsVisible == true &&
            metricsBlock?.Text == "Input: 320 tokens · Output: 45 tokens · TTFT: 680ms · TPS: 42 t/s");

        panel.BeginTranslation();
        Check(
            "begin translation clears and collapses metrics bar",
            metricsBar?.IsVisible == false &&
            string.IsNullOrEmpty(metricsBlock?.Text));
    }

    /// <summary>
    /// 精讲面板生命周期：折叠且禁用起步，翻译完成才启用；
    /// 展开即发起 ExplanationRequested；生成期思考链临时占用译文框、正文到达瞬间无缝恢复。
    /// </summary>
    private static void TestExplanationExpanderLifecycle()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(new InMemoryFavoriteService());
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var expander = Find<Expander>(panel, "ExplanationExpander");
        var explanationView = Find<ExplanationCardView>(panel, "ExplanationView");
        var translationBox = Find<TextBox>(panel, "TranslationTextBox");

        Check(
            "explanation expander is initially collapsed, disabled, and shows click-to-expand header",
            expander is not null &&
            !expander.IsEnabled &&
            !expander.IsExpanded &&
            expander.Header as string == "📖 精讲与语法解析 (点击展开)");

        panel.SetExplanationEnabled(true);
        Check("SetExplanationEnabled enables expander", expander?.IsEnabled == true);

        var explanationRequested = false;
        panel.ExplanationRequested += (_, _) => explanationRequested = true;
        if (expander is not null)
        {
            expander.IsExpanded = true;
        }
        Check("expanding explanation expander raises ExplanationRequested event", explanationRequested);

        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "这里过不去哦。"));
        Check("translation text is established before explanation", translationBox?.Text == "这里过不去哦。");

        panel.BeginExplanation();
        Check(
            "BeginExplanation updates header and ensures expander expanded",
            expander?.Header as string == "📖 精讲与语法解析 (正在生成…)" &&
            expander?.IsExpanded == true &&
            string.IsNullOrEmpty(panel.ExplanationText));

        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在深度思考语法结构"));
        Check(
            "thinking during explanation displays placeholder temporarily",
            translationBox?.Text?.Contains("正在深度思考语法结构") == true);

        panel.Append(new UnderstandDelta(DeltaChannel.Explanation, "### [词汇/短语]\n- **通る** | とおる"));
        Check(
            "explanation delta arriving seamlessly restores saved translation in TranslationTextBox",
            translationBox?.Text == "这里过不去哦。" &&
            !(translationBox.Text?.Contains("思考中") ?? false));

        panel.Append(new UnderstandDelta(DeltaChannel.Explanation, " | 动词原形 | 通过\n\n### [语法/句型]\n- **～ない** | 否定表达"));
        Check(
            "streaming explanation delta appends to explanation card view",
            panel.ExplanationText.Contains("通る") &&
            explanationView?.VocabularyItems.Count == 1 &&
            explanationView.VocabularyItems[0].Term == "通る" &&
            explanationView.GrammarItems.Count == 1 &&
            explanationView.GrammarItems[0].Pattern == "～ない");

        panel.CompleteExplanation(true);
        Check("CompleteExplanation restores title to normal header", expander?.Header as string == "📖 精讲与语法解析");
        Check("CompleteExplanation retains restored translation text", translationBox?.Text == "这里过不去哦。");

        // 异常结束兜底恢复
        panel.BeginExplanation();
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "未完成的精讲思考"));
        Check("expander thinking replaces text temporarily again", translationBox?.Text?.Contains("未完成的精讲思考") == true);
        panel.CompleteExplanation(false);
        Check(
            "CompleteExplanation on failure restores saved translation text",
            translationBox?.Text == "这里过不去哦。" &&
            !(translationBox.Text?.Contains("思考中") ?? false));

        panel.BeginTranslation();
        Check(
            "begin translation resets and disables explanation expander",
            expander?.IsEnabled == false &&
            expander?.IsExpanded == false &&
            expander?.Header as string == "📖 精讲与语法解析 (点击展开)" &&
            string.IsNullOrEmpty(panel.ExplanationText) &&
            explanationView?.VocabularyItems.Count == 0);

        var autoExplainToggle = Find<ToggleSwitch>(panel, "AutoExplainToggle");
        Check(
            "AutoExplainToggle is resolved and initially matches Reset configuration",
            autoExplainToggle is not null &&
            !panel.IsAutoExplainEnabled);

        var autoExplainEventFired = false;
        var autoExplainEventValue = false;
        panel.AutoExplainChanged += (_, val) =>
        {
            autoExplainEventFired = true;
            autoExplainEventValue = val;
        };

        panel.SetAutoExplain(true);
        Check("SetAutoExplain updates toggle checked state", panel.IsAutoExplainEnabled && autoExplainToggle?.IsChecked == true);
        Check("AutoExplainChanged event fired and matches state", autoExplainEventFired && autoExplainEventValue);

        panel.ExpandExplanation();
        Check("ExpandExplanation triggers IsExpanded", expander?.IsExpanded == true);
    }

    /// <summary>
    /// 精讲卡片收藏契约：已收藏词按字符串内容命中（实心星/空心星图标区分）。
    /// </summary>
    private static void TestExplanationCardViewFavorites()
    {
        EnsureThemedApplication();

        var favoriteService = new InMemoryFavoriteService();
        favoriteService.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る" }).GetAwaiter().GetResult();

        var cardView = new ExplanationCardView();
        cardView.SetFavoriteService(favoriteService);
        cardView.SetText("""
            ### [词汇/短语]
            - **走る** | はしる | 动词 | 奔跑
            - **食べる** | たべる | 动词 | 吃
            """);

        Check(
            "ExplanationCardView matches existing favorites by string content",
            cardView.VocabularyItems.Count == 2 &&
            cardView.VocabularyItems[0].IsFavorited &&
            !cardView.VocabularyItems[1].IsFavorited &&
            cardView.VocabularyItems[0].FavoriteIcon == "★" &&
            cardView.VocabularyItems[1].FavoriteIcon == "☆");

        cardView.SetText("这是一段非结构化的纯文本精讲，没有按格式分块。");
        Check(
            "ExplanationCardView degrades to fallback textbox when format unrecognized",
            cardView.IsDegraded);
    }

    /// <summary>
    /// 划词气泡交互契约：划词唤起气泡、语言下拉跟随面板本轮探测源语言。
    /// </summary>
    private static void TestSelectionBubbleInteractions()
    {
        EnsureThemedApplication();

        var favoriteService = new InMemoryFavoriteService();
        favoriteService.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る" }).GetAwaiter().GetResult();

        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(favoriteService);
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var selectionBubble = Find<SelectionBubblePopup>(panel, "SelectionBubble");
        Check("SelectionBubblePopup is mounted on ResultPanelWindow", selectionBubble is not null);

        selectionBubble?.SetFavoriteService(favoriteService);
        selectionBubble?.ShowAt(new Point(100, 100), "走る");
        Check("SelectionBubble opens on ShowAt", selectionBubble?.PopupHost.IsOpen == true);
        selectionBubble?.Hide();
        Check("SelectionBubble hides on Hide()", selectionBubble?.PopupHost.IsOpen == false);

        var translationBox = Find<TextBox>(panel, "TranslationTextBox");
        if (translationBox is not null)
        {
            translationBox.Text = "这里过不去哦";
            translationBox.SelectionStart = 2;
            translationBox.SelectionEnd = 5;

            selectionBubble?.SetDefaultLanguage(panel.CurrentSourceLanguage);
            selectionBubble?.ShowAt(translationBox, new Rect(0, 0, 10, 10), "过不去");
            Check("SelectionBubble displays on selection", selectionBubble?.PopupHost.IsOpen == true);
            Check("selection bubble language default follows panel detected source language",
                panel.CurrentSourceLanguage == "日本語");

            // 验证选项点击放行与外部点击判定：杜绝点击气泡选项时被误判为取消划选
            var pointer = new Pointer(0, PointerType.Mouse, true);
            if (selectionBubble?.FavoriteBtn is { } favBtn)
            {
                var insideArgs = new PointerPressedEventArgs(
                    favBtn,
                    pointer,
                    favBtn,
                    new Point(5, 5),
                    0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None)
                {
                    RoutedEvent = InputElement.PointerPressedEvent
                };
                Check("contains pointer event recognizes click on favorite button inside bubble",
                    selectionBubble.ContainsPointerEvent(insideArgs));

                panel.OnPanelPointerPressedForTest(insideArgs);
                Check("tunnel pointer pressed for inside bubble does not dismiss bubble",
                    selectionBubble.PopupHost.IsOpen == true);

                RaiseClick(favBtn);
                Check("clicking favorite option inside bubble successfully favorites",
                    favoriteService.IsFavorited("过不去"));
            }

            // 模拟在气泡外部（窗口其他区域）点击：气泡正确关闭
            var outsideArgs = new PointerPressedEventArgs(
                panel,
                pointer,
                panel,
                new Point(10, 10),
                0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None)
            {
                RoutedEvent = InputElement.PointerPressedEvent
            };
            Check("contains pointer event correctly marks outside click as not inside bubble",
                !selectionBubble!.ContainsPointerEvent(outsideArgs));

            panel.OnPanelPointerPressedForTest(outsideArgs);
            Check("clicking outside bubble dismisses it",
                selectionBubble.PopupHost.IsOpen == false);
        }
    }

    /// <summary>
    /// 注音单词收藏备注：气泡经 ShowAt 收到备注时，收藏落库携带该备注；无备注入口收藏后 note 为空。
    /// </summary>
    private static void TestSelectionBubbleFavoriteNote()
    {
        EnsureThemedApplication();

        var favoriteService = new InMemoryFavoriteService();
        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(favoriteService);
        var bubble = Find<SelectionBubblePopup>(panel, "SelectionBubble");
        bubble?.SetFavoriteService(favoriteService);

        bubble?.ShowAt(panel, new Rect(0, 0, 8, 8), "通", "とお");
        if (bubble?.FavoriteBtn is { } btn1)
        {
            RaiseClick(btn1);
        }

        var withNote = favoriteService.GetAllFavorites().FirstOrDefault();
        Check(
            "favoriting a ruby word carries the reading into the note",
            withNote is not null &&
            withNote.Content == "通" &&
            withNote.Note == "とお");

        bubble?.ShowAt(panel, new Rect(0, 0, 8, 8), "食べる");
        if (bubble?.FavoriteBtn is { } btn2)
        {
            RaiseClick(btn2);
        }

        var withoutNote = favoriteService.GetAllFavorites().FirstOrDefault(item => item.Content == "食べる");
        Check(
            "favoriting without a reading leaves the note empty",
            withoutNote is not null &&
            string.IsNullOrEmpty(withoutNote.Note));
    }

    /// <summary>
    /// 振假名文档构建与划词复制：
    /// RubyDocumentBuilder 生成包含 RubyInlineControl 的 Inlines；
    /// 三种复制格式（Plain / Annotated / ReadingOnly）精准提取；
    /// RubyInlineControl 自身提供独立复制与状态栏通知。
    /// </summary>
    private static void TestRubyDocumentBuilderAndSelectionCopy()
    {
        EnsureThemedApplication();

        // 1. 构建 Inlines
        var inlines = RubyDocumentBuilder.BuildInlines("こ こ は  通[とお]れないよ");
        Check(
            "ruby document builder creates Inlines with RubyInlineControl and text runs",
            inlines.Count == 3 &&
            inlines[0] is Run { Text: "こ こ は  " } &&
            inlines[1] is InlineUIContainer { Child: RubyInlineControl ruby } &&
            ruby.BaseText == "通" && ruby.RubyText == "とお" &&
            inlines[2] is Run { Text: "れないよ" });

        // 2. 验证 ExtractFromInlines 能够精准提取
        // 全量提取
        Check(
            "extract from inlines retrieves plain text across ruby containers",
            RubyDocumentBuilder.ExtractFromInlines(inlines, 0, int.MaxValue, RubyCopyKind.Plain) == "こ こ は  通れないよ");
        Check(
            "extract from inlines retrieves annotated brackets across ruby containers",
            RubyDocumentBuilder.ExtractFromInlines(inlines, 0, int.MaxValue, RubyCopyKind.Annotated) == "こ こ は  通[とお]れないよ");
        Check(
            "extract from inlines retrieves reading kana across ruby containers",
            RubyDocumentBuilder.ExtractFromInlines(inlines, 0, int.MaxValue, RubyCopyKind.ReadingOnly) == "こ こ は  とおれないよ");

        // 仅包含 InlineUIContainer 的子选区（偏移位置 7，长度 1）
        Check(
            "extracting from ruby container alone returns base, annotated, and reading",
            RubyDocumentBuilder.ExtractFromInlines(inlines, 7, 8, RubyCopyKind.Plain) == "通" &&
            RubyDocumentBuilder.ExtractFromInlines(inlines, 7, 8, RubyCopyKind.Annotated) == "通[とお]" &&
            RubyDocumentBuilder.ExtractFromInlines(inlines, 7, 8, RubyCopyKind.ReadingOnly) == "とお");

        // 3. 验证 RubyInlineControl 独立复制
        string? notifiedMessage = null;
        Action<string> statusHandler = msg => notifiedMessage = msg;
        RubyInlineControl.StatusNotificationRequested += statusHandler;
        var sampleRuby = new RubyInlineControl("通", "とお");

        sampleRuby.CopyReadingAsync(null).GetAwaiter().GetResult();
        Check("copy reading invokes status notification for kana", notifiedMessage?.Contains("とお") == true);

        sampleRuby.CopyBaseTextAsync(null).GetAwaiter().GetResult();
        Check("copy base text invokes status notification for kanji", notifiedMessage?.Contains("通") == true);

        sampleRuby.CopyAnnotatedAsync(null).GetAwaiter().GetResult();
        Check("copy annotated invokes status notification for annotated text", notifiedMessage?.Contains("通[とお]") == true);

        // 4. 多语言注音识别
        var ipaRuby = new RubyInlineControl("treason", "ˈtriː.zən");
        Check("english reading kind is detected as ipa", ipaRuby.ReadingKind == RubyReadingKind.Ipa);
        Check("english base noun is 单词 rather than 汉字", RubyReadingTerms.Base(RubyReadingKind.Ipa) == "单词");
        Check("pinyin reading kind is detected", RubyReadingTerms.Detect("nǐ hǎo") == RubyReadingKind.Pinyin);
        Check("unrecognizable reading falls back to neutral 读音", RubyReadingTerms.Reading(RubyReadingTerms.Detect("123")) == "读音");

        notifiedMessage = null;
        ipaRuby.CopyReadingAsync(null).GetAwaiter().GetResult();
        Check("copy ipa reading notifies with 音标 wording", notifiedMessage?.Contains("音标") == true);

        RubyInlineControl.StatusNotificationRequested -= statusHandler;
    }

    /// <summary>
    /// 布置一段带注音文本，逐对比较同一行内相邻注音块的横向区间，验证它们不相交。
    /// 以真实排版结果为准，不依赖某组具体像素值，避免字体度量差异使断言变脸。
    /// </summary>
    private static bool RubyReadingsDoNotOverlap(string annotated)
    {
        var viewer = new SelectableTextBlock
        {
            FontSize = 17.5,
            TextWrapping = TextWrapping.NoWrap,
        };
        viewer.Inlines = RubyDocumentBuilder.BuildInlines(annotated, 17.5);
        viewer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        viewer.Arrange(new Rect(viewer.DesiredSize));

        double? previousRight = null;
        double? previousTop = null;
        foreach (var ruby in viewer.Inlines.OfType<InlineUIContainer>().Select(c => c.Child).OfType<RubyInlineControl>())
        {
            var rubyBlock = (TextBlock)ruby.Children[0];
            var origin = rubyBlock.TranslatePoint(new Point(0, 0), viewer);
            if (origin is null || rubyBlock.Bounds.Width <= 0)
            {
                return false;
            }

            var top = origin.Value.Y;
            var left = origin.Value.X;
            // 换行后的首块无需与上一行比较
            if (previousTop is { } lastTop && Math.Abs(top - lastTop) <= 1 && left < previousRight!.Value - 0.5)
            {
                return false;
            }

            previousTop = top;
            previousRight = left + rubyBlock.Bounds.Width;
        }

        return true;
    }

    /// <summary>
    /// 专项体验打磨回归测试：
    /// 1. 注音按钮中性外观（Classes 包含 neutral）；
    /// 2. PinButton 切换置顶状态并激发出 PinChanged 事件；
    /// 3. 原文区编辑模式按 Esc 提交保存而不是还原撤销；
    /// 4. RubyInlineControl 边距归零；
    /// 5. 结果面板失焦（Deactivated）不自动关闭。
    /// </summary>
    private static void TestResultPanelPolishBatch()
    {
        EnsureThemedApplication();

        // 1. RubyInlineControl 紧凑排版边距归零
        var sample = new RubyInlineControl("漢", "かん");
        Check("ruby inline control margin is zero for compact continuous layout", sample.Margin == new Thickness(0));

        // 2. 结果面板初始化与按钮样式
        var panel = new ResultPanelWindow();
        panel.SetPinned(false);
        var annotateBtn = Find<Button>(panel, "AnnotateSourceButton");
        var pinBtn = Find<Avalonia.Controls.Primitives.ToggleButton>(panel, "PinButton");
        Check("annotate source button is standard button and defaults to ruby visible", annotateBtn is not null && panel.IsRubyVisible);
        Check("pin button defaults to unpinned state", pinBtn?.IsChecked == false && !panel.Topmost);

        // 3. PinChanged 事件与置顶联动
        bool? pinChangedValue = null;
        panel.PinChanged += (_, val) => pinChangedValue = val;
        panel.SetPinned(true);
        Check("set pinned updates pin button and topmost state", pinBtn?.IsChecked == true && panel.Topmost);

        if (pinBtn is not null)
        {
            pinBtn.IsChecked = false;
        }
        Check("unpinning raises PinChanged with false and disables topmost", pinChangedValue == false && !panel.Topmost);

        // 4. 原文编辑模式下按 Esc：保存最新编辑内容，退出编辑模式
        panel.Reset("原始原文", "准备就绪", autoTranslate: false);
        var sourceBox = Find<TextBox>(panel, "SourceTextBox");
        var viewerBorder = Find<Border>(panel, "SourceViewerBorder");
        panel.EnterEditMode();
        Check("enters edit mode", sourceBox?.IsVisible == true && viewerBorder?.IsVisible == false);

        if (sourceBox is not null)
        {
            sourceBox.Text = "修改后的文本内容";
            // 模拟按 Esc 键
            sourceBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape
            });
        }

        Check("pressing escape in edit mode saves modified source instead of reverting",
            panel.SourceText == "修改后的文本内容" &&
            sourceBox?.IsVisible == false &&
            viewerBorder?.IsVisible == true);

        // 5. 验证 RubyInlineControl 紧凑底字测量与行高隔离
        var rubyWithLongReading = new RubyInlineControl("收", "おさむ", 15);
        rubyWithLongReading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 单字底字（15px 约 14~18px 宽）装不下三假名注音，改为就地扩宽容纳注音，不再向两侧悬挂压邻字
        Check("ruby inline control widens its own box to host an oversized reading instead of overhanging",
            rubyWithLongReading.DesiredSize.Width is > 24 and <= 40);
        // 总高度应在正常双行范围，严禁继承外层 40px 行高膨胀至 70px+
        Check("ruby inline control total height is compact without inheriting inflated line heights",
            rubyWithLongReading.DesiredSize.Height <= 42);

        // 5.1 长注音撑开底字字间距：二字底字遇长拼音时字缝被撑开，避免相邻词的注音互相压盖
        var rubyLongPinyin = new RubyInlineControl("已成", "yǐ chéng", 17.5);
        rubyLongPinyin.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rubyNoReading = new RubyInlineControl("已成", string.Empty, 17.5);
        rubyNoReading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check("long reading widens base letter spacing to host the ruby",
            rubyLongPinyin.DesiredSize.Width > rubyNoReading.DesiredSize.Width + 3);
        Check("single-character base keeps default letter spacing since there is no gap to widen",
            RubyInlineControl.ComputeBaseLetterSpacing(1, 15, 40, 17.5) == 0.8);
        // 上限兑现：注音再长，每个字缝最多只添半个底字字号的宽度
        Check("base letter spacing widening is clamped to half an em per gap",
            RubyInlineControl.ComputeBaseLetterSpacing(2, 30, 400, 17.5) <= 9.6);

        // 5.2 几何回归：同一行内相邻词的注音矩形禁止水平相交（主人报告的“已成功 yǐ chéng gōng”重叠）
        Check("adjacent pinyin rubies never overlap horizontally",
            RubyReadingsDoNotOverlap("已[yǐ]成[chéng]功[gōng]"));
        Check("adjacent kana rubies never overlap horizontally",
            RubyReadingsDoNotOverlap("通[とお]れ[れ]ない[ない]"));

        // 5. 失焦不触发关闭
        var closed = false;
        panel.Closed += (_, _) => closed = true;
        panel.OnPanelDeactivatedForTest();
        Check("deactivating result panel does not close the window", !closed);

        // 6. 原文区左内边距与悬挂注音保护：左内边距扩充为 14px，给首字注音留足左侧外展空间不被遮盖
        Check("source viewer border padding provides 14px left margin for hanging ruby",
            viewerBorder?.Padding.Left >= 14 && sourceBox?.Padding.Left >= 14);

        // 7. RubyInlineControl 悬停高亮安全解析：杜绝 UnsetValue 强转 InvalidCastException 致命崩溃
        var testRuby = new RubyInlineControl("通", "とお");
        var accentBrush = RubyInlineControl.GetAccentBrush(testRuby);
        Check("get accent brush safely resolves a valid brush without throwing", accentBrush is not null);

        // 8. 原文渲染区划选选区安全性：跨越注音控件划选与文本提取平稳无异常
        var sourceViewer = Find<SelectableTextBlock>(panel, "SourceViewer");
        if (sourceViewer is not null)
        {
            sourceViewer.SelectionStart = 0;
            sourceViewer.SelectionEnd = sourceViewer.Inlines?.Text?.Length ?? 0;
            var extracted = RubyDocumentBuilder.ExtractText(sourceViewer, RubyCopyKind.Plain);
            Check("source viewer selection across inlines extracts plain text without crash",
                extracted == "修改后的文本内容");
            sourceViewer.ClearSelection();
        }
    }
}
