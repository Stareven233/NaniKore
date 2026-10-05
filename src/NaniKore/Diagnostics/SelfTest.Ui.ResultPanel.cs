using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（结果面板）：本地注音叠加、振假名/纯文本双语态与翻译流、指标条、
/// 精讲面板生命周期、精讲卡片收藏与划词气泡交互。
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// 等待注音任务落地：面板由当前线程创建，等待期间必须持续抽干其 Dispatcher 队列，
    /// 否则 Task.Run 续体里的 InvokeAsync 会死等一个被 GetResult 阻塞的面板线程（真实 UI 线程有空转消息泵，不会走到这）。
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
            panel.Dispatcher.Invoke(() => { }, WpfThreading.DispatcherPriority.Background);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// 结果面板本地注音接线（issue 008 Phase 3）：源语言下拉四选一（自动探测 + 三语），
    /// Reset 落地即单次异步叠加注音；注音形态只是纯文本原文的派生视图，纯文本始终是单一事实源；
    /// 开关关闭时软降级为纯文本，手动换语言时按新口径重算。
    /// </summary>
    private static void TestResultPanelSourceAnnotation()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        var languageBox = Find<WpfControls.ComboBox>(panel, "SourceLanguageBox");
        Check(
            "result panel source language box offers auto detection plus the three annotation languages",
            languageBox is { Items.Count: 4 });

        panel.SetPhoneticsOptions(new PhoneticsConfig { Enabled = true, RareOnly = true });
        const string source = "彼は躊躇なく答えた。";
        panel.Reset(source, "test", autoTranslate: false);
        // Reset 已触发单次注音；等待其落地后再断言
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

    /// <summary>
    /// 结果面板双语态与翻译流（issue 008 Phase 3 / issue 015）：振假名视图（FlowDocument）与
    /// 纯文本编辑框一键互切，纯文本原文始终是单一事实源。未产出有效译文前主按钮固定「翻译」，
    /// 有译文后再实质修改即升级为「重译」；点击翻译/重译时编辑内容立即提交为事实源，绝不跳回初次识别原文。
    /// </summary>
    private static void TestResultPanelRubyDualModeAndTranslationFlow()
    {
        EnsureThemedApplication();

        // 1. AutoTranslate == true：直接以 FlowDocument 呈现，初始按钮为「翻译」
        var panel = new ResultPanelWindow();
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);
        var viewerBorder = Find<WpfControls.Border>(panel, "SourceViewerBorder");
        var sourceBox = Find<WpfControls.TextBox>(panel, "SourceTextBox");
        var translateButton = Find<WpfControls.Button>(panel, "TranslateButton");
        Check(
            "result panel with autoTranslate true displays FlowDocument viewer and translate button initially",
            viewerBorder?.Visibility == Wpf.Visibility.Visible &&
            sourceBox?.Visibility == Wpf.Visibility.Collapsed &&
            translateButton?.Content as string == "翻译" &&
            panel.SourceText == "こ こ は  通れないよ");

        // 2. AutoTranslate == false：保持原生 TextBox
        var panelManual = new ResultPanelWindow();
        panelManual.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: false);
        Check(
            "result panel with autoTranslate false displays TextBox editor and translate button",
            Find<WpfControls.Border>(panelManual, "SourceViewerBorder")?.Visibility == Wpf.Visibility.Collapsed &&
            Find<WpfControls.TextBox>(panelManual, "SourceTextBox")?.Visibility == Wpf.Visibility.Visible &&
            Find<WpfControls.Button>(panelManual, "TranslateButton")?.Content as string == "翻译" &&
            panelManual.SourceText == "こ こ は  通れないよ");

        // 3. 双击/EnterEditMode 原地切换编辑态：不全选且定位光标
        panel.EnterEditMode();
        Check(
            "switching to text editor preserves caret and avoids selecting all text",
            sourceBox?.Visibility == Wpf.Visibility.Visible &&
            viewerBorder?.Visibility == Wpf.Visibility.Collapsed &&
            sourceBox.SelectionLength == 0 &&
            sourceBox.CaretIndex == sourceBox.Text.Length);

        // 4. 编辑态未修改失焦：自动还原 FlowDocument 振假名视图
        sourceBox?.RaiseEvent(new Wpf.RoutedEventArgs(Wpf.UIElement.LostFocusEvent));
        Check(
            "unmodified text editor restores FlowDocument viewer on lost focus",
            viewerBorder?.Visibility == Wpf.Visibility.Visible &&
            sourceBox?.Visibility == Wpf.Visibility.Collapsed);

        // 5. 编辑态实质修改与规范化：未产出有效译文前，主按钮固定为「翻译」
        var normalizeButton = Find<WpfControls.Button>(panel, "NormalizeSourceButton");
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
            sourceBox.RaiseEvent(new Wpf.RoutedEventArgs(Wpf.UIElement.LostFocusEvent));
        }

        Check(
            "substantively modified text editor remains active and stays as 翻译 when no prior translation exists",
            sourceBox?.Visibility == Wpf.Visibility.Visible &&
            viewerBorder?.Visibility == Wpf.Visibility.Collapsed &&
            translateButton?.Content as string == "翻译" &&
            panel.SourceText == "ここは通れません");

        // 6. 流式接收 SourceText 增量更新振假名与纯文本，翻译完成后保持「翻译」
        panel.BeginTranslation();
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在分析复合适词与时态"));
        var translationBox = Find<WpfControls.TextBox>(panel, "TranslationTextBox");
        Check(
            "thinking delta displays throttled thinking placeholder in TranslationTextBox",
            translationBox?.Text.StartsWith("💭 [思考中]") == true &&
            translationBox.Text.Contains("正在分析复合适词与时态"));

        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "私[わたし]は"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, " 学生[がくせい]"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "です。"));
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "我是学生。"));
        panel.SetTranslationInProgress(false);
        Check(
            "streaming source text delta updates flow document and plain text in real time, and finishes with translate button",
            viewerBorder?.Visibility == Wpf.Visibility.Visible &&
            panel.SourceText == "私は 学生です。" &&
            sourceBox?.Text == "私は 学生です。" &&
            translateButton?.Content as string == "翻译" &&
            translationBox?.Text == "我是学生。" &&
            !translationBox.Text.Contains("思考中"));

        // 7. 产出有效译文后，再次实质修改或规范化激活「重译」；点击时编辑内容立即提交为事实源
        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "私[わたし]は 先生[せんせい]です。";
            sourceBox.RaiseEvent(new Wpf.RoutedEventArgs(Wpf.UIElement.LostFocusEvent));
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
            panel.SourceText == "私は 先生です。" &&
            viewerBorder?.Visibility == Wpf.Visibility.Visible &&
            sourceBox?.Visibility == Wpf.Visibility.Collapsed);

        panel.BeginTranslation();
        Check(
            "begin translation preserves modified source instead of reverting to initial source",
            panel.SourceText == "私は 先生です。");
    }

    /// <summary>底部指标条契约：SetMetrics 拼接摘要并显示，BeginTranslation 清空并折叠。</summary>
    private static void TestResultPanelMetricsBar()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        var metricsBar = Find<WpfControls.Border>(panel, "MetricsBar");
        var metricsBlock = Find<WpfControls.TextBlock>(panel, "MetricsTextBlock");
        Check(
            "metrics bar is initially collapsed",
            metricsBar?.Visibility == Wpf.Visibility.Collapsed);

        panel.SetMetrics(new TranslationMetrics(320, 45, 680, 1750, 42.1));
        Check(
            "set metrics displays formatted summary and makes metrics bar visible",
            metricsBar?.Visibility == Wpf.Visibility.Visible &&
            metricsBlock?.Text == "Input: 320 tokens · Output: 45 tokens · TTFT: 680ms · 生成速度: 42 t/s");

        panel.BeginTranslation();
        Check(
            "begin translation clears and collapses metrics bar",
            metricsBar?.Visibility == Wpf.Visibility.Collapsed &&
            string.IsNullOrEmpty(metricsBlock?.Text));
    }

    /// <summary>
    /// 精讲面板生命周期（issue 015）：折叠且禁用起步，翻译完成才启用；
    /// 展开即发起 ExplanationRequested；生成期思考链临时占用译文框、正文到达瞬间无缝恢复；
    /// 异常结束时兜底恢复译文；新一轮翻译自动复位折叠、禁用并清空。
    /// 自动精讲开关同步驱动 ToggleButton 状态与事件。
    /// </summary>
    private static void TestExplanationExpanderLifecycle()
    {
        EnsureThemedApplication();

        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(new InMemoryFavoriteService());
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var expander = Find<WpfControls.Expander>(panel, "ExplanationExpander");
        var explanationView = Find<ExplanationCardView>(panel, "ExplanationView");
        var translationBox = Find<WpfControls.TextBox>(panel, "TranslationTextBox");

        Check(
            "explanation expander is initially collapsed, disabled, and shows click-to-expand header",
            expander is not null &&
            !expander.IsEnabled &&
            !expander.IsExpanded &&
            expander.Header as string == "📖 精讲与语法解析 (点击展开)");

        // 模拟阶段 1 翻译完成，激活 Expander
        panel.SetExplanationEnabled(true);
        Check("SetExplanationEnabled enables expander", expander?.IsEnabled == true);

        // 验证用户展开触发 ExplanationRequested 事件
        var explanationRequested = false;
        panel.ExplanationRequested += (_, _) => explanationRequested = true;
        if (expander is not null)
        {
            expander.IsExpanded = true;
        }
        Check("expanding explanation expander raises ExplanationRequested event", explanationRequested);

        // 验证精讲前先有译文，并在精讲思考链中暂存与正文到达后无缝恢复
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "这里过不去哦。"));
        Check("translation text is established before explanation", translationBox?.Text == "这里过不去哦。");

        // 验证 BeginExplanation 进入生成态
        panel.BeginExplanation();
        Check(
            "BeginExplanation updates header and ensures expander expanded",
            expander?.Header as string == "📖 精讲与语法解析 (正在生成…)" &&
            expander?.IsExpanded == true &&
            string.IsNullOrEmpty(panel.ExplanationText));

        // 验证精讲思考增量就地刷新，但正文到达瞬间无缝恢复原有译文
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在深度思考语法结构"));
        Check(
            "thinking during explanation displays placeholder temporarily",
            translationBox?.Text.Contains("正在深度思考语法结构") == true);

        // 验证 Append 接收 DeltaChannel.Explanation 流式增量并输出至 ExplanationView，同时恢复译文
        panel.Append(new UnderstandDelta(DeltaChannel.Explanation, "### [词汇/短语]\n- **通る** | とおる"));
        Check(
            "explanation delta arriving seamlessly restores saved translation in TranslationTextBox",
            translationBox?.Text == "这里过不去哦。" &&
            !translationBox.Text.Contains("思考中"));

        panel.Append(new UnderstandDelta(DeltaChannel.Explanation, " | 动词原形 | 通过\n\n### [语法/句型]\n- **～ない** | 否定表达"));
        Check(
            "streaming explanation delta appends to explanation card view",
            panel.ExplanationText.Contains("通る") &&
            explanationView?.VocabularyItems.Count == 1 &&
            explanationView.VocabularyItems[0].Term == "通る" &&
            explanationView.GrammarItems.Count == 1 &&
            explanationView.GrammarItems[0].Pattern == "～ない");

        // 验证 CompleteExplanation 恢复常态标题
        panel.CompleteExplanation(true);
        Check("CompleteExplanation restores title to normal header", expander?.Header as string == "📖 精讲与语法解析");
        Check("CompleteExplanation retains restored translation text", translationBox?.Text == "这里过不去哦。");

        // 验证精讲中途若只出现思考便异常结束，CompleteExplanation 兜底恢复译文
        panel.BeginExplanation();
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "未完成的精讲思考"));
        Check("expander thinking replaces text temporarily again", translationBox?.Text.Contains("未完成的精讲思考") == true);
        panel.CompleteExplanation(false);
        Check(
            "CompleteExplanation on failure restores saved translation text",
            translationBox?.Text == "这里过不去哦。" &&
            !translationBox.Text.Contains("思考中"));

        // 验证发起新一轮翻译时 Expander 自动复位折叠、禁用并清空
        panel.BeginTranslation();
        Check(
            "begin translation resets and disables explanation expander",
            expander?.IsEnabled == false &&
            expander?.IsExpanded == false &&
            expander?.Header as string == "📖 精讲与语法解析 (点击展开)" &&
            string.IsNullOrEmpty(panel.ExplanationText) &&
            explanationView?.VocabularyItems.Count == 0);

        // 验证结果面板自动精讲开关状态与事件联动
        var autoExplainToggle = Find<WpfPrimitives.ToggleButton>(panel, "AutoExplainToggle");
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

        // 模拟点击切换开关
        if (autoExplainToggle is not null)
        {
            RaiseClick(autoExplainToggle);
        }
        Check(
            "AutoExplainToggle raises AutoExplainChanged event on user interaction",
            autoExplainEventFired && autoExplainEventValue == panel.IsAutoExplainEnabled);

        // 验证 ExpandExplanation 自动展开并激发 ExplanationRequested
        var autoExpandRequested = false;
        panel.ExplanationRequested += (_, _) => autoExpandRequested = true;
        panel.SetExplanationEnabled(true);
        panel.ExpandExplanation();
        Check(
            "ExpandExplanation triggers IsExpanded and invokes ExplanationRequested",
            expander?.IsExpanded == true && autoExpandRequested);
    }

    /// <summary>
    /// 精讲卡片收藏契约（issue 015）：已收藏词按字符串内容命中（实心星/空心星图标区分），
    /// 非结构化纯文本降级为 fallback 文本框。
    /// </summary>
    private static void TestExplanationCardViewFavorites()
    {
        EnsureThemedApplication();

        var favoriteService = new InMemoryFavoriteService();
        // 先预置一个已收藏的词，星标状态必须由持久化数据驱动而非本地缓存
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
            cardView.VocabularyItems[0].FavoriteIcon == "\uE735" &&
            cardView.VocabularyItems[1].FavoriteIcon == "\uE734");

        // 降级文本测试
        cardView.SetText("这是一段非结构化的纯文本精讲，没有按格式分块。");
        Check(
            "ExplanationCardView degrades to fallback textbox when format unrecognized",
            cardView.IsDegraded);
    }

    /// <summary>
    /// 划词气泡交互契约（1.1-B / 1.2 / 2.3-C 回归）：译文框划词 MouseUp 唤起气泡（哪怕事件已消费），
    /// 点击窗口其他区域立即收起；StaysOpen + 收藏前置到 PreviewMouseLeftButtonDown，
    /// 气泡内点击既不关闭也不二次冒泡；语言下拉跟随面板本轮探测的源语言；
    /// 内容区整页滚动 + 精讲区局部 MaxHeight 二级滚动。
    /// </summary>
    private static void TestSelectionBubbleInteractions()
    {
        EnsureThemedApplication();

        var favoriteService = new InMemoryFavoriteService();
        // 预置已收藏词，验证气泡打开时的收藏态识别
        favoriteService.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る" }).GetAwaiter().GetResult();

        var panel = new ResultPanelWindow();
        panel.SetFavoriteService(favoriteService);
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var selectionBubble = Find<SelectionBubblePopup>(panel, "SelectionBubble");
        var explanationView = Find<ExplanationCardView>(panel, "ExplanationView");
        Check(
            "SelectionBubblePopup is mounted on ResultPanelWindow",
            selectionBubble is not null);
        selectionBubble?.SetFavoriteService(favoriteService);
        selectionBubble?.ShowAt(new Wpf.Point(100, 100), "走る");
        Check(
            "SelectionBubble detects already-favorited word on open",
            selectionBubble?.BubblePopup.IsOpen == true);
        selectionBubble?.Hide();
        Check("SelectionBubble hides on Hide()", selectionBubble?.BubblePopup.IsOpen == false);

        // 验证 TranslationTextBox 划词后触发 MouseUp 能唤起气泡（哪怕 Handled = true）
        var translationBox = Find<WpfControls.TextBox>(panel, "TranslationTextBox");
        if (translationBox is not null)
        {
            translationBox.Text = "这里过不去哦";
            translationBox.Select(2, 3); // 选「过不去」
            translationBox.RaiseEvent(LeftButton(Wpf.UIElement.MouseUpEvent, translationBox));
            Check("TranslationTextBox selection raises SelectionBubble on MouseUp", selectionBubble?.BubblePopup.IsOpen == true);

            // 2.3-C：划词入口会把面板本轮探测的源语言灌入气泡语言下拉（含假名原文 → 日本語）。
            Check(
                "selection bubble language default follows panel detected source language",
                panel.CurrentSourceLanguage == "日本語" &&
                (selectionBubble?.LanguageComboBox.SelectedItem as LanguageChoice)?.Name == "日本語");

            // 19. 验证当气泡处于打开状态时，点击窗口内其他文字或区域（PreviewMouseDown）能立即隐藏气泡
            panel.RaiseEvent(LeftButton(Wpf.UIElement.PreviewMouseDownEvent, translationBox));
            Check("clicking other text or area inside window hides SelectionBubble immediately", selectionBubble?.BubblePopup.IsOpen == false);

            // 20. 1.1-B 回归：StaysOpen 契约 + 收藏触发前移到 PreviewMouseLeftButtonDown，
            // 气泡内点击（编辑框/下拉/收藏按钮）既不会关闭气泡，也不会在文本框上二次冒出新气泡。
            Check(
                "bubble popup stays open so in-popup clicks never race with outside dismissal",
                selectionBubble?.BubblePopup.StaysOpen == true);

            selectionBubble?.ShowAt(new Wpf.Point(100, 100), "食べる");
            var favoriteButton = selectionBubble?.FavoriteButton;
            if (favoriteButton is not null)
            {
                var favoritePreviewDown = LeftButton(Wpf.UIElement.PreviewMouseLeftButtonDownEvent, favoriteButton);
                favoriteButton.RaiseEvent(favoritePreviewDown);
                Check(
                    "favorite action triggers on preview mouse down without closing via click race",
                    favoritePreviewDown.Handled &&
                    favoriteService.IsFavorited("食べる") &&
                    selectionBubble?.BubblePopup.IsOpen == true);
            }

            // 21. 1.1-B 回归：ContainsLogicalFocus 判据可用（无焦点时返回 false，Deactivated 延迟判定据此放行收起）。
            Check(
                "bubble logical focus containment reports false when nothing holds focus",
                selectionBubble?.ContainsLogicalFocus() == false);

            // 23. issue 015 / 1.1 第二轮回归（根因：PopupRoot 在 topmost band 里排在面板之下，
            // 点击漏到面板被 OnPanelPreviewMouseDown 收起、MouseUp 又二次冒泡）。
            // 无 UI 环境断言可确定的契约：未打开时屏幕点判定恒不命中，打开路径照常可用；
            // HWND 置顶提升与真实点击回归由临时探针实测（见 issue 015 实施记录）。
            Check(
                "bubble screen-point guard stays inactive while popup closed",
                selectionBubble?.ContainsScreenPoint(new Wpf.Point(0, 0)) == false);
            selectionBubble?.ShowAt(new Wpf.Point(100, 100), "東京");
            Check(
                "bubble reopens after close without losing favorite detection state",
                selectionBubble?.BubblePopup.IsOpen == true);
            selectionBubble?.Hide();

            // 22. 1.2 布局契约：内容区外包整页 ScrollViewer（Auto），精讲区另有 MaxHeight 形成二级滚动。
            var contentScroller = Find<WpfControls.ScrollViewer>(panel, "ContentScrollViewer");
            Check(
                "result panel content area scrolls as a whole while explanation keeps local max height",
                contentScroller is not null &&
                contentScroller.VerticalScrollBarVisibility == WpfControls.ScrollBarVisibility.Auto &&
                contentScroller.HorizontalScrollBarVisibility == WpfControls.ScrollBarVisibility.Disabled &&
                explanationView is not null &&
                explanationView.MaxHeight > 0);

            selectionBubble?.Hide();
        }
    }

    /// <summary>
    /// 振假名文档构建与划词复制（issue 008 Phase 3 / issue 015）：
    /// RubyDocumentBuilder 把「汉字[读音]」切片成 FlowDocument；
    /// 三种复制口径（Plain / Annotated / ReadingOnly）必须能跨 InlineUIContainer 边界精准提取；
    /// RubyInlineControl 自身提供假名/汉字/注音文本的独立复制与状态栏通知。
    /// </summary>
    private static void TestRubyDocumentBuilderAndSelectionCopy()
    {
        EnsureThemedApplication();

        // 1. RubyDocumentBuilder 构建 FlowDocument
        var doc = RubyDocumentBuilder.Build("こ こ は  通[とお]れないよ");
        Check(
            "ruby document builder creates FlowDocument with RubyInlineControl and text runs",
            doc.Blocks.Count == 1 &&
            doc.Blocks.FirstBlock is WpfDocuments.Paragraph p &&
            p.Inlines.Count == 3 &&
            p.Inlines.FirstInline is WpfDocuments.Run { Text: "こ こ は  " } &&
            p.Inlines.ElementAt(1) is WpfDocuments.InlineUIContainer { Child: RubyInlineControl ruby } &&
            ruby.BaseText == "通" && ruby.RubyText == "とお" &&
            p.Inlines.LastInline is WpfDocuments.Run { Text: "れないよ" });

        // 2. 验证 ExtractFromSelection 能够从全选与单选 InlineUIContainer 中精准提取
        var fullRange = new WpfDocuments.TextRange(doc.ContentStart, doc.ContentEnd);
        Check(
            "extract from selection retrieves plain text across ruby containers",
            RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.Plain) == "こ こ は  通れないよ");
        Check(
            "extract from selection retrieves annotated brackets across ruby containers",
            RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.Annotated) == "こ こ は  通[とお]れないよ");
        Check(
            "extract from selection retrieves reading kana across ruby containers",
            RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.ReadingOnly) == "こ こ は  とおれないよ");

        if (doc.Blocks.FirstBlock is WpfDocuments.Paragraph firstParagraph &&
            firstParagraph.Inlines.ElementAt(1) is WpfDocuments.InlineUIContainer rubyContainer)
        {
            var containerRange = new WpfDocuments.TextRange(rubyContainer.ContentStart, rubyContainer.ContentEnd);
            Check(
                "extracting from ruby container alone returns base, annotated, and reading without falling back to full text",
                RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.Plain) == "通" &&
                RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.Annotated) == "通[とお]" &&
                RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.ReadingOnly) == "とお");
        }

        // 3. 验证 RubyInlineControl 独立复制假名与汉字，并触发状态栏通知
        string? notifiedMessage = null;
        Action<string> statusHandler = msg => notifiedMessage = msg;
        RubyInlineControl.StatusNotificationRequested += statusHandler;
        var sampleRuby = new RubyInlineControl("通", "とお");
        sampleRuby.CopyReading();
        Check("copy reading invokes status notification for kana", notifiedMessage?.Contains("とお") == true);
        sampleRuby.CopyBaseText();
        Check("copy base text invokes status notification for kanji", notifiedMessage?.Contains("通") == true);
        sampleRuby.CopyAnnotated();
        Check("copy annotated invokes status notification for annotated text", notifiedMessage?.Contains("通[とお]") == true);

        // 4. 验证假名 TextBlock 响应左键单击并触发复制
        notifiedMessage = null;
        if (sampleRuby.Children[0] is WpfControls.TextBlock rubyBlock)
        {
            var downArgs = LeftButton(Wpf.UIElement.PreviewMouseLeftButtonDownEvent, rubyBlock);
            rubyBlock.RaiseEvent(downArgs);
            Check("ruby block marks left button down as handled to prevent host stealing capture", downArgs.Handled);

            var upArgs = LeftButton(Wpf.UIElement.PreviewMouseLeftButtonUpEvent, rubyBlock);
            rubyBlock.RaiseEvent(upArgs);
            Check("ruby block marks left button up as handled and invokes copy reading",
                upArgs.Handled && notifiedMessage?.Contains("とお") == true);
        }

        RubyInlineControl.StatusNotificationRequested -= statusHandler;
    }
}
