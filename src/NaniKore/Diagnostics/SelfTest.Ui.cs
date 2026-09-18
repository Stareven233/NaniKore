using System.Windows.Forms;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
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
        TestRubyDocumentBuilderAndDualMode();
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

        // 6. 编辑态实质修改：保持 TextBox 并激活重译按钮
        panel.EnterEditMode();
        if (sourceBox is not null)
        {
            sourceBox.Text = "ここは通れません";
            sourceBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.UIElement.LostFocusEvent));
        }

        Check(
            "substantively modified text editor remains active and prompts for re-translation",
            sourceBox?.Visibility == System.Windows.Visibility.Visible &&
            viewerBorder?.Visibility == System.Windows.Visibility.Collapsed &&
            translateBtn?.Content as string == "重译" &&
            panel.SourceText == "ここは通れません");

        // 7. 流式接收 SourceText 增量更新振假名与纯文本，翻译完成后按钮转为“重译”
        panel.BeginTranslation();
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "私[わたし]は"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, " 学生[がくせい]"));
        panel.Append(new UnderstandDelta(DeltaChannel.SourceText, "です。"));
        panel.Append(new UnderstandDelta(DeltaChannel.Translation, "我是学生。"));
        panel.SetTranslationInProgress(false);
        Check(
            "streaming source text delta updates flow document and plain text in real time, and finishes with re-translate button",
            viewerBorder?.Visibility == System.Windows.Visibility.Visible &&
            panel.SourceText == "私は 学生です。" &&
            sourceBox?.Text == "私は 学生です。" &&
            translateBtn?.Content as string == "重译");
    }
}

