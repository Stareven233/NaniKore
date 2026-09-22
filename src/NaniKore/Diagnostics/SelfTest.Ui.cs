using System.Windows.Forms;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Input;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunUiTests()
    {
        TestHotkeyGestureNormalization();
        TestHotkeyRecorderInteraction();
        TestThemeResourcesResolve();
        TestSettingsPagesUseAdaptiveLayout();
        TestGeneralPagePipelineDependencyVisibility();
        TestTranslationPagePromptEditing();
        TestSettingsCloseSizeMemory();
        TestSettingsCloseSizeWriteBackKeepsExternalUpdates();
        TestRubyDocumentBuilderAndDualMode();
    }

    /// <summary>
    /// 回归测试：关闭设置窗口必须“有变化才写盘”。
    /// 曾经无条件回写，导致“什么都不改、直接关闭”也会重写 config.yaml。
    /// </summary>
    private static void TestSettingsCloseSizeMemory()
    {
        var baseline = new WindowConfig { SettingsWidth = 800, SettingsHeight = 640 };

        Check(
            "settings close without any change resolves no size write-back",
            SettingsWindow.ResolvePersistedSize(baseline, 800, 640, 620, 460) is null);

        Check(
            "settings close tolerates sub-pixel DPI drift within the epsilon",
            SettingsWindow.ResolvePersistedSize(baseline, 800.6, 639.4, 620, 460) is null);

        Check(
            "settings close after a real resize resolves the new size to write back",
            SettingsWindow.ResolvePersistedSize(baseline, 1024.5, 700.25, 620, 460) is { Width: 1024.5, Height: 700.25 });

        Check(
            "settings close clamps the resolved size to the declared minimum",
            SettingsWindow.ResolvePersistedSize(baseline, 300, 200, 620, 460) is { Width: 620, Height: 460 });

        Check(
            "settings close skips invalid or not-yet-laid-out window sizes",
            SettingsWindow.ResolvePersistedSize(baseline, 0, 0, 620, 460) is null &&
            SettingsWindow.ResolvePersistedSize(baseline, double.NaN, 640, 620, 460) is null &&
            SettingsWindow.ResolvePersistedSize(baseline, double.PositiveInfinity, 640, 620, 460) is null);
    }

    /// <summary>
    /// 回归测试：设置窗口未保存、仅拖过尺寸就关闭时，尺寸记忆必须叠加在“实时当前配置”上。
    /// 曾经的缺陷：复制打开时的快照整份写盘，会把期间其他入口
    /// （结果卡 AutoExplainChanged → NaniKoreApp.ApplySettings → AtomicWrite）的修改静默回滚。
    /// </summary>
    private static void TestSettingsCloseSizeWriteBackKeepsExternalUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.2.11
                general:
                  auto_explain: false
                  target_language: ja
                windows:
                  settings_width: 800
                  settings_height: 640
                """);
            var store = new ConfigStore(directory);

            // 设置窗口打开时宿主提供实时配置访问器；快照语义由编辑副本承担。
            AppConfig LatestConfig() => store.Current;
            var openTimeSnapshot = AppConfigCloner.Clone(LatestConfig());
            var working = AppConfigCloner.Clone(LatestConfig());

            // 页面编辑只进 _working：模拟用户改过目标语言但始终没点保存。
            working.General.TargetLanguage = "未保存的页面编辑";

            // 外部入口写盘：结果卡拨动 AutoExplainToggle → 直接改 Current 并 AtomicWrite。
            var external = LatestConfig();
            external.General.AutoExplain = true;
            store.AtomicWrite(external);

            // 关闭时未保存 + 真的拖过尺寸：基线必须取实时配置而不是打开时的快照。
            var baseline = SettingsWindow.ResolveCloseWriteBackBaseline(settingsSaved: false, working, LatestConfig());
            Check(
                "unsaved settings close picks the live config instead of the stale open-time snapshot",
                !ReferenceEquals(baseline, openTimeSnapshot) &&
                ReferenceEquals(baseline, store.Current) &&
                baseline.General.AutoExplain);

            // 走完整的尺寸决策与写盘路径，验证落盘内容的新鲜度。
            if (SettingsWindow.ResolvePersistedSize(baseline.Windows, 1024, 700, 620, 460) is { } size)
            {
                var next = AppConfigCloner.Clone(baseline);
                next.Windows.SettingsWidth = size.Width;
                next.Windows.SettingsHeight = size.Height;
                store.AtomicWrite(next);
            }

            var reloaded = new ConfigStore(directory);
            Check(
                "external auto_explain update survives an unsaved settings close with a resize",
                reloaded.Current.General.AutoExplain);
            Check(
                "unsaved settings close still records the resized window dimensions",
                reloaded.Current.Windows.SettingsWidth == 1024 &&
                reloaded.Current.Windows.SettingsHeight == 700);
            Check(
                "unsaved settings close still discards page edits made in the working copy",
                reloaded.Current.General.TargetLanguage == "ja");

            // 已保存过的关闭路径仍旧以编辑副本为基线，行为不回退。
            Check(
                "saved settings close keeps using the working copy as write-back baseline",
                ReferenceEquals(SettingsWindow.ResolveCloseWriteBackBaseline(settingsSaved: true, working, store.Current), working));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestTranslationPagePromptEditing()
    {
        var config = new AppConfig();
        var page = new TranslationPage();
        page.Load(config);

        var localPromptBox = (System.Windows.Controls.TextBox?)page.FindName("PromptBox");
        var visionPromptBox = (System.Windows.Controls.TextBox?)page.FindName("VisionPromptBox");
        var pluginLanguageBox = (System.Windows.Controls.ComboBox?)page.FindName("PluginLanguageBox");
        var pluginPromptBox = (System.Windows.Controls.TextBox?)page.FindName("PluginPromptBox");
        var localPromptRow = (SettingsRow?)page.FindName("LocalPromptRow");
        var visionPromptRow = (SettingsRow?)page.FindName("VisionPromptRow");
        var restoreLocalButton = (System.Windows.Controls.Button?)page.FindName("RestorePromptButton");
        var restorePluginButton = (System.Windows.Controls.Button?)page.FindName("RestorePluginPromptButton");
        var restoreVisionButton = (System.Windows.Controls.Button?)page.FindName("RestoreVisionPromptButton");

        Check(
            "translation settings show built-in Base and vision prompts for empty config values",
            localPromptBox?.Text == PromptTemplates.DefaultTranslationPrompt &&
            visionPromptBox?.Text == PromptTemplates.DefaultVisionOneshotPrompt);

        Check(
            "translation settings load Japanese plugin prompt by default with correct language options",
            pluginLanguageBox?.Items.Count == 2 &&
            pluginLanguageBox.SelectedIndex == 0 &&
            pluginPromptBox?.Text == PromptTemplates.DefaultJapanesePlugin);

        Check(
            "prompt restore buttons share the title row with concise black labels",
            restoreLocalButton?.Content as string == "恢复默认" &&
            restorePluginButton?.Content as string == "恢复默认" &&
            restoreVisionButton?.Content as string == "恢复默认" &&
            ReferenceEquals(localPromptRow?.HeaderAction, restoreLocalButton) &&
            ReferenceEquals(visionPromptRow?.HeaderAction, restoreVisionButton) &&
            restoreLocalButton.Foreground is System.Windows.Media.SolidColorBrush { Color: var localColor } &&
            localColor == System.Windows.Media.Colors.Black &&
            restorePluginButton.Foreground is System.Windows.Media.SolidColorBrush { Color: var pluginColor } &&
            pluginColor == System.Windows.Media.Colors.Black &&
            restoreVisionButton.Foreground is System.Windows.Media.SolidColorBrush { Color: var visionColor } &&
            visionColor == System.Windows.Media.Colors.Black);

        // 修改日文插件草稿
        pluginPromptBox!.Text = "Custom Japanese plugin";
        // 切换到英文插件
        pluginLanguageBox!.SelectedIndex = 1;
        Check(
            "switching plugin language displays default English plugin prompt and preserves Japanese draft",
            pluginPromptBox.Text == PromptTemplates.DefaultEnglishPlugin);

        // 修改英文插件
        pluginPromptBox.Text = "Custom English plugin";
        // 测试英文插件一键恢复默认
        restorePluginButton!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(
            "plugin prompt restore button resets English plugin to default template",
            pluginPromptBox.Text == PromptTemplates.DefaultEnglishPlugin);

        // 切换回日文插件，确认刚才的草稿被完整保存
        pluginLanguageBox.SelectedIndex = 0;
        Check(
            "switching back to Japanese plugin restores the modified draft",
            pluginPromptBox.Text == "Custom Japanese plugin");

        visionPromptBox!.Text = "temporary";
        restoreVisionButton!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(
            "translation settings can restore the built-in vision prompt",
            visionPromptBox.Text == PromptTemplates.DefaultVisionOneshotPrompt);

        localPromptBox!.Text = "Custom base prompt {{target_language}}";
        visionPromptBox.Text = "Custom vision {{target_language}}\n\n---\n\n<translation>";
        page.Apply(config);
        Check(
            "translation settings save editable base, plugin and vision prompts into app config",
            config.Prompts.Base == "Custom base prompt {{target_language}}" &&
            config.Prompts.JapanesePlugin == "Custom Japanese plugin" &&
            config.Prompts.EnglishPlugin == PromptTemplates.DefaultEnglishPlugin &&
            config.Prompts.VisionOneshot == "Custom vision {{target_language}}\n\n---\n\n<translation>");
    }

    private static void TestHotkeyGestureNormalization()
    {
        Check(
            "gesture parsing normalizes modifier aliases and casing",
            HotkeyGesture.Normalize("control+shift+q") == "Ctrl+Shift+Q" &&
            HotkeyGesture.Normalize("windows + alt + f9") == "Alt+Win+F9");

        Check(
            "gesture parsing rejects empty, modifier-only and unsupported keys",
            RejectsGesture(string.Empty) && RejectsGesture("Ctrl") && RejectsGesture("Ctrl+Shift+PrintScreen"));

        Check(
            "captured combinations reuse the registration parser",
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, Keys.Q, out var captured, out _) &&
            captured == "Ctrl+Shift+Q" &&
            HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl | HotkeyGesture.ModShift, (uint)Keys.Q));

        Check(
            "modifier-only and unmodified keys are refused with a readable reason",
            !HotkeyGesture.TryCapture(HotkeyGesture.ModControl, Keys.ControlKey, out _, out var modifierOnly) &&
            modifierOnly.Length > 0 &&
            !HotkeyGesture.TryCapture(0, Keys.Q, out _, out var missingModifier) &&
            missingModifier.Length > 0);

        Check(
            "held modifiers echo a partial combination while recording",
            HotkeyGesture.FormatPreview(HotkeyGesture.ModControl | HotkeyGesture.ModShift) == "Ctrl+Shift+…" &&
            HotkeyGesture.FormatPreview(0).Length == 0);
    }

    private static void TestHotkeyRecorderInteraction()
    {
        var recorder = new HotkeyRecorderBox { Gesture = "Ctrl+Shift+A" };
        var suspensions = new List<bool>();
        recorder.RecordingChanged += (_, recording) => suspensions.Add(recording);

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(Keys.ShiftKey, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "holding modifiers echoes the partial combination without ending recording",
            recorder.IsRecording && recorder.DisplayText == "Ctrl+Shift+…" && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(Keys.Back, HotkeyGesture.ModControl);
        Check(
            "recording keeps state for backspace input",
            recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(Keys.Q, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "recording stores the normalized combination and releases the global hotkey once",
            !recorder.IsRecording &&
            recorder.Gesture == "Ctrl+Shift+Q" &&
            recorder.DisplayText == "Ctrl+Shift+Q" &&
            suspensions.SequenceEqual([true, false]));

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(Keys.Escape, 0);
        Check(
            "escape restores the gesture that was active before recording",
            !recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+Q");

        var page = new HotkeyPage();
        page.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "alt+f9" } });
        var config = new AppConfig();
        page.Apply(config);
        Check(
            "hotkey page normalizes stored gestures and validates before applying",
            page.Validate() is null && config.Hotkeys.Capture == "Alt+F9");
    }

    /// <summary>
    /// WPF 化后的布局约定：页面根元素必须按内容自适应高度（由宿主 ScrollViewer 负责滚动），
    /// 且所有设置行都通过 SettingsRow 承载，从而共享同一套 DPI 与字体缩放行为。
    /// </summary>
    private static void TestSettingsPagesUseAdaptiveLayout()
    {
        // 主题字典必须真的能解析并套到控件上；没有 Application 资源时模板缺失，测得高度会恒为 0。
        EnsureThemedApplication();

        foreach (var page in new ISettingsPage[] { new GeneralPage(), new TranslationPage(), new HotkeyPage(), new AboutPage() })
        {
            var view = page.View;
            page.Load(new AppConfig());
            view.Measure(new System.Windows.Size(760, double.PositiveInfinity));
            var rows = FindDescendants<SettingsRow>(view).ToList();
            var groups = FindDescendants<SettingsGroup>(view).ToList();
            Check(
                $"{page.Title} page builds an adaptive card layout without fixed pixel positions",
                double.IsNaN(view.Width) &&
                double.IsNaN(view.Height) &&
                view.DesiredSize.Height > 0 &&
                groups.Count > 0 &&
                rows.TrueForAll(row => double.IsNaN(row.Height) && !string.IsNullOrEmpty(row.Header)));
        }

        // 说明文字与字段同属一行，缩放时不会像旧版那样与输入框错位。
        var general = new GeneralPage();
        general.Load(new AppConfig());
        var describedRows = FindDescendants<SettingsRow>(general.View)
            .Count(row => !string.IsNullOrEmpty(row.Description));
        Check(
            "general page attaches descriptions to the rows they explain",
            describedRows > 0);
    }

    private static void TestGeneralPagePipelineDependencyVisibility()
    {
        EnsureThemedApplication();
        var page = new GeneralPage();
        var config = new AppConfig { Pipeline = new PipelineConfig { Mode = "vision_oneshot" } };
        page.Load(config);
        Check(
            "general page hides local OCR dependencies for vision pipeline",
            page.FindName("LocalOcrDependenciesGroup") is System.Windows.Controls.Control { Visibility: System.Windows.Visibility.Collapsed });

        page.Load(new AppConfig { Pipeline = new PipelineConfig { Mode = "local_ocr" } });
        Check(
            "general page shows local OCR dependencies for local pipeline",
            page.FindName("LocalOcrDependenciesGroup") is System.Windows.Controls.Control { Visibility: System.Windows.Visibility.Visible });

        var autoExplainPage = new GeneralPage();
        var autoExplainConfig = new AppConfig { General = new GeneralConfig { AutoExplain = true } };
        autoExplainPage.Load(autoExplainConfig);
        var autoExplainToggle = (System.Windows.Controls.Primitives.ToggleButton?)autoExplainPage.FindName("AutoExplainToggle");
        Check(
            "general page loads and applies AutoExplain setting accurately",
            autoExplainToggle?.IsChecked == true);

        autoExplainToggle!.IsChecked = false;
        var appliedConfig = new AppConfig();
        autoExplainPage.Apply(appliedConfig);
        Check(
            "general page applies AutoExplain toggle change to configuration",
            !appliedConfig.General.AutoExplain);
    }

    /// <summary>主题资源与控件模板必须可用；顺带验证两份色板与两份控件字典都能解析。</summary>
    private static void TestThemeResourcesResolve()
    {
        EnsureThemedApplication();
        var application = System.Windows.Application.Current!;
        var expectedKeys = new[]
        {
            "Brush.Window.Background", "Brush.Card.Background", "Brush.Accent", "Brush.Text.Primary",
            "Font.Ui", "Radius.Control", "ToggleSwitch", "Button.Accent", "Nav.List", "TextBox.Multiline",
        };
        Check(
            "theme dictionaries expose every semantic key used by the settings pages",
            expectedKeys.All(key => application.TryFindResource(key) is not null));

        // 自绘控件必须有隐式样式，否则运行时会退回系统默认观感。
        Check(
            "custom controls resolve their implicit fluent styles",
            application.TryFindResource(typeof(SettingsRow)) is System.Windows.Style &&
            application.TryFindResource(typeof(SettingsGroup)) is System.Windows.Style &&
            application.TryFindResource(typeof(NumberBox)) is System.Windows.Style &&
            application.TryFindResource(typeof(InfoBar)) is System.Windows.Style &&
            application.TryFindResource(typeof(HotkeyRecorderBox)) is System.Windows.Style);
    }

    /// <summary>无 UI 自测里也需要一个 WPF Application 承载资源；不调用 Run，只做资源解析。</summary>
    private static void EnsureThemedApplication()
    {
        if (System.Windows.Application.Current is null)
        {
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        NaniKore.App.Theme.ThemeManager.Initialize(System.Windows.Application.Current!);
    }

    /// <summary>逻辑树遍历：页面尚未渲染时视觉树为空，断言必须走逻辑子元素。</summary>
    private static IEnumerable<T> FindDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static bool RejectsGesture(string gesture)
    {
        try
        {
            HotkeyGesture.Parse(gesture);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void TestRubyDocumentBuilderAndDualMode()
    {
        EnsureThemedApplication();

        // 1. RubyDocumentBuilder 构建 FlowDocument
        var doc = RubyDocumentBuilder.Build("こ こ は  通[とお]れないよ");
        Check(
            "ruby document builder creates FlowDocument with RubyInlineControl and text runs",
            doc.Blocks.Count == 1 &&
            doc.Blocks.FirstBlock is System.Windows.Documents.Paragraph p &&
            p.Inlines.Count == 3 &&
            p.Inlines.FirstInline is System.Windows.Documents.Run { Text: "こ こ は  " } &&
            p.Inlines.ElementAt(1) is System.Windows.Documents.InlineUIContainer { Child: RubyInlineControl ruby } &&
            ruby.BaseText == "通" && ruby.RubyText == "とお" &&
            p.Inlines.LastInline is System.Windows.Documents.Run { Text: "れないよ" });

        // 2. ResultPanelWindow 联动 AutoTranslate == true (直接以 FlowDocument 呈现，且初始按钮为“翻译”)
        var panel = new ResultPanelWindow();
        panel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var viewerBorder = (System.Windows.Controls.Border?)panel.FindName("SourceViewerBorder");
        var sourceBox = (System.Windows.Controls.TextBox?)panel.FindName("SourceTextBox");
        var translateBtn = (System.Windows.Controls.Button?)panel.FindName("TranslateButton");

        Check(
            "result panel with autoTranslate true displays FlowDocument viewer and translate button initially",
            viewerBorder?.Visibility == System.Windows.Visibility.Visible &&
            sourceBox?.Visibility == System.Windows.Visibility.Collapsed &&
            translateBtn?.Content as string == "翻译" &&
            panel.SourceText == "こ こ は  通れないよ");

        // 3. ResultPanelWindow 联动 AutoTranslate == false (保持原生 TextBox)
        var panelManual = new ResultPanelWindow();
        panelManual.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: false);

        var manualViewerBorder = (System.Windows.Controls.Border?)panelManual.FindName("SourceViewerBorder");
        var manualSourceBox = (System.Windows.Controls.TextBox?)panelManual.FindName("SourceTextBox");
        var manualTranslateBtn = (System.Windows.Controls.Button?)panelManual.FindName("TranslateButton");

        Check(
            "result panel with autoTranslate false displays TextBox editor and translate button",
            manualViewerBorder?.Visibility == System.Windows.Visibility.Collapsed &&
            manualSourceBox?.Visibility == System.Windows.Visibility.Visible &&
            manualTranslateBtn?.Content as string == "翻译" &&
            panelManual.SourceText == "こ こ は  通れないよ");

        // 4. 双击/EnterEditMode原地切换编辑态：不全选且定位光标
        panel.EnterEditMode();
        Check(
            "switching to text editor preserves caret and avoids selecting all text",
            sourceBox?.Visibility == System.Windows.Visibility.Visible &&
            viewerBorder?.Visibility == System.Windows.Visibility.Collapsed &&
            sourceBox.SelectionLength == 0 &&
            sourceBox.CaretIndex == sourceBox.Text.Length);

        // 5. 编辑态未修改失焦：自动还原 FlowDocument 振假名视图
        sourceBox?.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.UIElement.LostFocusEvent));
        Check(
            "unmodified text editor restores FlowDocument viewer on lost focus",
            viewerBorder?.Visibility == System.Windows.Visibility.Visible &&
            sourceBox?.Visibility == System.Windows.Visibility.Collapsed);

        // 6. 编辑态实质修改与规范化：未产出有效译文前，主按钮固定为“翻译”
        var normalizeBtn = (System.Windows.Controls.Button?)panel.FindName("NormalizeSourceButton");
        normalizeBtn?.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(
            "normalizing source before any translation keeps translate button content as 翻译",
            translateBtn?.Content as string == "翻译");

        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "ここは通れません";
            sourceBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.UIElement.LostFocusEvent));
        }

        Check(
            "substantively modified text editor remains active and stays as 翻译 when no prior translation exists",
            sourceBox?.Visibility == System.Windows.Visibility.Visible &&
            viewerBorder?.Visibility == System.Windows.Visibility.Collapsed &&
            translateBtn?.Content as string == "翻译" &&
            panel.SourceText == "ここは通れません");

        // 7. 流式接收 SourceText 增量更新振假名与纯文本，翻译完成后保持“翻译”
        panel.BeginTranslation();
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "私[わたし]は"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, " 学生[がくせい]"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "です。"));
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "我是学生。"));
        panel.SetTranslationInProgress(false);
        Check(
            "streaming source text delta updates flow document and plain text in real time, and finishes with translate button",
            viewerBorder?.Visibility == System.Windows.Visibility.Visible &&
            panel.SourceText == "私は 学生です。" &&
            sourceBox?.Text == "私は 学生です。" &&
            translateBtn?.Content as string == "翻译");

        // 8. 产出有效译文后，再次实质修改或规范化激活“重译”
        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "私[わたし]は 先生[せんせい]です。";
            sourceBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.UIElement.LostFocusEvent));
        }
        Check(
            "substantively modified text editor triggers 重译 when previous translation exists",
            translateBtn?.Content as string == "重译");

        // 8.5 验证 SetMetrics 与 ClearMetrics 控制底部指标面板状态
        var metricsBar = (System.Windows.Controls.Border?)panel.FindName("MetricsBar");
        var metricsBlock = (System.Windows.Controls.TextBlock?)panel.FindName("MetricsTextBlock");
        Check(
            "metrics bar is initially collapsed",
            metricsBar?.Visibility == System.Windows.Visibility.Collapsed);

        panel.SetMetrics(new TranslationMetrics(320, 45, 680, 1750, 42.1));
        Check(
            "set metrics displays formatted summary and makes metrics bar visible",
            metricsBar?.Visibility == System.Windows.Visibility.Visible &&
            metricsBlock?.Text == "Input: 320 tokens · Output: 45 tokens · TTFT: 680ms · 生成速度: 42 t/s");

        panel.BeginTranslation();
        Check(
            "begin translation clears and collapses metrics bar",
            metricsBar?.Visibility == System.Windows.Visibility.Collapsed &&
            string.IsNullOrEmpty(metricsBlock?.Text));

        // 9. 验证 ExtractFromSelection 能够从全选与单选 InlineUIContainer 中精准提取
        var fullRange = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd);
        var plainAll = RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.Plain);
        var annotatedAll = RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.Annotated);
        var readingAll = RubyDocumentBuilder.ExtractFromSelection(fullRange, RubyCopyKind.ReadingOnly);
        Check(
            "extract from selection retrieves plain text across ruby containers",
            plainAll == "こ こ は  通れないよ");
        Check(
            "extract from selection retrieves annotated brackets across ruby containers",
            annotatedAll == "こ こ は  通[とお]れないよ");
        Check(
            "extract from selection retrieves reading kana across ruby containers",
            readingAll == "こ こ は  とおれないよ");

        if (doc.Blocks.FirstBlock is System.Windows.Documents.Paragraph firstParagraph &&
            firstParagraph.Inlines.ElementAt(1) is System.Windows.Documents.InlineUIContainer rubyContainer)
        {
            var containerRange = new System.Windows.Documents.TextRange(rubyContainer.ContentStart, rubyContainer.ContentEnd);
            var singlePlain = RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.Plain);
            var singleAnnotated = RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.Annotated);
            var singleReading = RubyDocumentBuilder.ExtractFromSelection(containerRange, RubyCopyKind.ReadingOnly);
            Check(
                "extracting from ruby container alone returns base, annotated, and reading without falling back to full text",
                singlePlain == "通" && singleAnnotated == "通[とお]" && singleReading == "とお");
        }

        // 10. 验证 RubyInlineControl 独立复制假名与汉字，并触发状态栏通知
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

        // 11. 验证假名 TextBlock 响应左键单击并触发复制
        notifiedMessage = null;
        if (sampleRuby.Children[0] is System.Windows.Controls.TextBlock rubyBlock)
        {
            var downArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                Source = rubyBlock,
            };
            rubyBlock.RaiseEvent(downArgs);
            Check("ruby block marks left button down as handled to prevent host stealing capture", downArgs.Handled);

            var upArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
                Source = rubyBlock,
            };
            rubyBlock.RaiseEvent(upArgs);
            Check("ruby block marks left button up as handled and invokes copy reading",
                upArgs.Handled && notifiedMessage?.Contains("とお") == true);
        }

        RubyInlineControl.StatusNotificationRequested -= statusHandler;

        // 12. 验证 ExplanationExpander 交互生命周期与流式输出
        var expPanel = new ResultPanelWindow();
        var favService = new InMemoryFavoriteService();
        expPanel.SetFavoriteService(favService);
        expPanel.Reset("こ こ は  通[とお]れないよ", "OCR 完成", autoTranslate: true);

        var expander = (System.Windows.Controls.Expander?)expPanel.FindName("ExplanationExpander");
        var explanationView = (ExplanationCardView?)expPanel.FindName("ExplanationView");
        var selectionBubble = (SelectionBubblePopup?)expPanel.FindName("SelectionBubble");

        Check(
            "explanation expander is initially collapsed, disabled, and shows click-to-expand header",
            expander is not null &&
            !expander.IsEnabled &&
            !expander.IsExpanded &&
            expander.Header as string == "📖 精讲与语法解析 (点击展开)");

        // 模拟阶段 1 翻译完成，激活 Expander
        expPanel.SetExplanationEnabled(true);
        Check("SetExplanationEnabled enables expander", expander?.IsEnabled == true);

        // 验证用户展开触发 ExplanationRequested 事件
        var explanationRequested = false;
        expPanel.ExplanationRequested += (_, _) => explanationRequested = true;
        if (expander is not null)
        {
            expander.IsExpanded = true;
        }
        Check("expanding explanation expander raises ExplanationRequested event", explanationRequested);

        // 验证 BeginExplanation 进入生成态
        expPanel.BeginExplanation();
        Check(
            "BeginExplanation updates header and ensures expander expanded",
            expander?.Header as string == "📖 精讲与语法解析 (正在生成…)" &&
            expander?.IsExpanded == true &&
            string.IsNullOrEmpty(expPanel.ExplanationText));

        // 验证 Append 接收 DeltaChannel.Explanation 流式增量并输出至 ExplanationView
        expPanel.Append(new UnderstandDelta(DeltaChannel.Explanation, "### [词汇/短语]\n- **通る** | とおる"));
        expPanel.Append(new UnderstandDelta(DeltaChannel.Explanation, " | 动词原形 | 通过\n\n### [语法/句型]\n- **～ない** | 否定表达"));
        Check(
            "streaming explanation delta appends to explanation card view",
            expPanel.ExplanationText.Contains("通る") &&
            explanationView?.VocabularyItems.Count == 1 &&
            explanationView.VocabularyItems[0].Term == "通る" &&
            explanationView.GrammarItems.Count == 1 &&
            explanationView.GrammarItems[0].Pattern == "～ない");

        // 验证 CompleteExplanation 恢复常态标题
        expPanel.CompleteExplanation(true);
        Check("CompleteExplanation restores title to normal header", expander?.Header as string == "📖 精讲与语法解析");

        // 验证发起新一轮翻译时 Expander 自动复位折叠、禁用并清空
        expPanel.BeginTranslation();
        Check(
            "begin translation resets and disables explanation expander",
            expander?.IsEnabled == false &&
            expander?.IsExpanded == false &&
            expander?.Header as string == "📖 精讲与语法解析 (点击展开)" &&
            string.IsNullOrEmpty(expPanel.ExplanationText) &&
            explanationView?.VocabularyItems.Count == 0);

        // 13. 验证 ResultPanelWindow 自动精讲开关状态与事件
        var autoExplainToggle = (System.Windows.Controls.Primitives.ToggleButton?)expPanel.FindName("AutoExplainToggle");
        Check(
            "AutoExplainToggle is resolved and initially matches Reset configuration",
            autoExplainToggle is not null &&
            !expPanel.IsAutoExplainEnabled);

        var autoExplainEventFired = false;
        var autoExplainEventValue = false;
        expPanel.AutoExplainChanged += (_, val) =>
        {
            autoExplainEventFired = true;
            autoExplainEventValue = val;
        };

        expPanel.SetAutoExplain(true);
        Check("SetAutoExplain updates toggle checked state", expPanel.IsAutoExplainEnabled && autoExplainToggle?.IsChecked == true);

        // 模拟点击切换开关
        autoExplainToggle!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(
            "AutoExplainToggle raises AutoExplainChanged event on user interaction",
            autoExplainEventFired && autoExplainEventValue == expPanel.IsAutoExplainEnabled);

        // 验证 ExpandExplanation 自动展开并激发 ExplanationRequested
        var autoExpandRequested = false;
        expPanel.ExplanationRequested += (_, _) => autoExpandRequested = true;
        expPanel.SetExplanationEnabled(true);
        expPanel.ExpandExplanation();
        Check(
            "ExpandExplanation triggers IsExpanded and invokes ExplanationRequested",
            expander?.IsExpanded == true && autoExpandRequested);

        // 14. 验证 ExplanationCardView 收藏与降级行为
        var testCardView = new ExplanationCardView();
        var mockService = new InMemoryFavoriteService();
        testCardView.SetFavoriteService(mockService);

        // 先预置一个已收藏的词
        mockService.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る" }).GetAwaiter().GetResult();

        testCardView.SetText("""
            ### [词汇/短语]
            - **走る** | はしる | 动词 | 奔跑
            - **食べる** | たべる | 动词 | 吃
            """);

        Check(
            "ExplanationCardView matches existing favorites by string content",
            testCardView.VocabularyItems.Count == 2 &&
            testCardView.VocabularyItems[0].IsFavorited &&
            !testCardView.VocabularyItems[1].IsFavorited &&
            testCardView.VocabularyItems[0].FavoriteIcon == "\uE735" &&
            testCardView.VocabularyItems[1].FavoriteIcon == "\uE734");

        // 降级文本测试
        testCardView.SetText("这是一段非结构化的纯文本精讲，没有按格式分块。");
        Check(
            "ExplanationCardView degrades to fallback textbox when format unrecognized",
            testCardView.IsDegraded);

        // 15. 验证 SelectionBubblePopup 划词气泡
        Check(
            "SelectionBubblePopup is mounted on ResultPanelWindow",
            selectionBubble is not null);
        selectionBubble?.SetFavoriteService(mockService);
        selectionBubble?.ShowAt(new System.Windows.Point(100, 100), "走る");
        Check(
            "SelectionBubble detects already-favorited word on open",
            selectionBubble?.BubblePopup.IsOpen == true);
        selectionBubble?.Hide();
        Check("SelectionBubble hides on Hide()", selectionBubble?.BubblePopup.IsOpen == false);

        // 16. 验证 TranslationTextBox 划词后触发 MouseUp 能唤起气泡（哪怕 Handled = true）
        var translationBox = (System.Windows.Controls.TextBox?)expPanel.FindName("TranslationTextBox");
        if (translationBox is not null)
        {
            translationBox.Text = "这里过不去哦";
            translationBox.Select(2, 3); // 选“过不去”
            var mouseUpArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseUpEvent,
                Source = translationBox
            };
            translationBox.RaiseEvent(mouseUpArgs);
            Check("TranslationTextBox selection raises SelectionBubble on MouseUp", selectionBubble?.BubblePopup.IsOpen == true);

            // 17. 验证当气泡处于打开状态时，点击窗口内其他文字或区域（PreviewMouseDown）能立即隐藏气泡
            var clickOtherArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseDownEvent,
                Source = translationBox
            };
            expPanel.RaiseEvent(clickOtherArgs);
            Check("clicking other text or area inside window hides SelectionBubble immediately", selectionBubble?.BubblePopup.IsOpen == false);

            selectionBubble?.Hide();
        }
    }
}

