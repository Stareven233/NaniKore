using System.Windows;
using System.Windows.Forms;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Input;
using NaniKore.Phonetics;
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
        TestGeneralPagePhoneticsCards();
        TestResultPanelSourceAnnotation();
        TestSharedLanguageChoicesAndDetectionMapping();
        TestTranslationPagePromptEditing();
        TestSettingsCloseSizeMemory();
        TestSettingsCloseSizeWriteBackKeepsExternalUpdates();
        TestRubyDocumentBuilderAndDualMode();
        TestFavoritesSettingsPageFilteringAndEvents();
        TestWindowProperties();
    }

    /// <summary>
    /// 窗口行为与层级管理：设置窗口显示在任务栏且默认不置顶，最小化时进入任务栏；
    /// 结果面板恢复为非置顶窗口，避免强占层级遮挡其他程序。
    /// </summary>
    private static void TestWindowProperties()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        Check("result panel window is not topmost", !panel.Topmost);

        var settings = new SettingsWindow(() => new AppConfig(), _ => { }, (_, _) => Task.FromResult("ok"));
        Check("settings window shows in taskbar", settings.ShowInTaskbar);
        Check("settings window is not topmost", !settings.Topmost);
    }

    /// <summary>
    /// 注音词典卡片（issue 008 Phase 2）：三语按钮与本地状态联动（文案即状态），
    /// 无下载入口时禁用、有入口时启用；注音开关走配置读写闭环。
    /// </summary>
    private static void TestGeneralPagePhoneticsCards()
    {
        EnsureThemedApplication();

        var page = new GeneralPage();
        Check(
            "phonetics group hosts the enabled and rare-only switches",
            page.FindName("PhoneticsEnabledToggle") is System.Windows.Controls.Primitives.ToggleButton &&
            page.FindName("PhoneticsRareOnlyToggle") is System.Windows.Controls.Primitives.ToggleButton);

        foreach (var language in PhoneticLanguages.All)
        {
            var buttonName = $"PhoneticsDownload{char.ToUpperInvariant(language.Code[0])}{language.Code[1..]}";
            var button = (System.Windows.Controls.Button?)page.FindName(buttonName);
            Check(
                $"{language.Code} dictionary card button exists",
                button is not null);

            if (button is null)
            {
                continue;
            }

            var state = PhoneticDependencyManifest.GetState(language.Code);
            Check(
                $"{language.Code} dictionary card button mirrors the local manifest state and stays disabled without a downloader",
                button.Content as string == GeneralPage.PhoneticButtonLabel(state) &&
                !button.IsEnabled);
        }

        // 注入假下载器后按钮启用（自测不触发真实网络，仅验证接线）
        var withDownloader = new GeneralPage(downloadPhoneticDictionary: (_, _, _) => Task.CompletedTask);
        var jaButton = (System.Windows.Controls.Button?)withDownloader.FindName("PhoneticsDownloadJa");
        Check(
            "dictionary card button enables once a downloader is injected",
            jaButton?.IsEnabled == true);

        // 注音开关读写闭环
        var config = new AppConfig { Phonetics = new PhoneticsConfig { Enabled = false, RareOnly = false } };
        page.Load(config);
        var enabledToggle = (System.Windows.Controls.Primitives.ToggleButton?)page.FindName("PhoneticsEnabledToggle");
        var rareOnlyToggle = (System.Windows.Controls.Primitives.ToggleButton?)page.FindName("PhoneticsRareOnlyToggle");
        Check(
            "general page loads phonetics switches accurately",
            enabledToggle?.IsChecked == false && rareOnlyToggle?.IsChecked == false);
        enabledToggle!.IsChecked = true;
        var applied = new AppConfig();
        page.Apply(applied);
        Check(
            "general page applies phonetics switch changes to configuration",
            applied.Phonetics.Enabled && !applied.Phonetics.RareOnly);
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
        var languageBox = (System.Windows.Controls.ComboBox?)panel.FindName("SourceLanguageBox");
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
            panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        task.GetAwaiter().GetResult();
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
        var localPromptRow = (SettingsRow?)page.FindName("LocalPromptRow");
        var visionPromptRow = (SettingsRow?)page.FindName("VisionPromptRow");
        var restoreLocalButton = (System.Windows.Controls.Button?)page.FindName("RestorePromptButton");
        var restoreVisionButton = (System.Windows.Controls.Button?)page.FindName("RestoreVisionPromptButton");

        // 语言注音插件编辑器已随插件键退役（issue 008 Phase 3）：页面只剩 Base 与视觉 Base 两行
        Check(
            "translation settings retire the language plugin editor and keep base and vision rows",
            page.FindName("PluginLanguageBox") is null &&
            page.FindName("PluginPromptBox") is null &&
            page.FindName("RestorePluginPromptButton") is null &&
            localPromptBox?.Text == PromptTemplates.DefaultTranslationBasePrompt &&
            visionPromptBox?.Text == PromptTemplates.DefaultVisionBasePrompt);

        Check(
            "prompt restore buttons share the title row with concise black labels",
            restoreLocalButton?.Content as string == "恢复默认" &&
            restoreVisionButton?.Content as string == "恢复默认" &&
            ReferenceEquals(localPromptRow?.HeaderAction, restoreLocalButton) &&
            ReferenceEquals(visionPromptRow?.HeaderAction, restoreVisionButton) &&
            restoreLocalButton.Foreground is System.Windows.Media.SolidColorBrush { Color: var localColor } &&
            localColor == System.Windows.Media.Colors.Black &&
            restoreVisionButton.Foreground is System.Windows.Media.SolidColorBrush { Color: var visionColor } &&
            visionColor == System.Windows.Media.Colors.Black);

        // 视觉 Base 一键恢复默认
        visionPromptBox!.Text = "temporary";
        restoreVisionButton!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(
            "translation settings can restore the built-in vision base prompt",
            visionPromptBox.Text == PromptTemplates.DefaultVisionBasePrompt);

        localPromptBox!.Text = "Custom base prompt {{target_language}}";
        visionPromptBox.Text = "Custom vision {{target_language}}\n\n---TRANSLATION---\n\n<translation>";
        var legacyPluginConfig = new AppConfig
        {
            Prompts = new PromptConfig
            {
                JapanesePlugin = "Legacy ja-JP",
                EnglishPlugin = "Legacy en-US",
                ChinesePlugin = "Legacy zh-CN",
            },
        };
        page.Apply(config);
        Check(
            "translation settings save editable base and vision prompts while retired plugin values pass through untouched",
            config.Prompts.Base == "Custom base prompt {{target_language}}" &&
            config.Prompts.VisionBase == "Custom vision {{target_language}}\n\n---TRANSLATION---\n\n<translation>" &&
            config.Prompts.JapanesePlugin is { Length: 0 } &&
            config.Prompts.EnglishPlugin is { Length: 0 } &&
            config.Prompts.ChinesePlugin is { Length: 0 });

        // 旧配置里退役插件键的自定义值不得被设置页清空（数据留存，仅不参与组装）
        page.Load(legacyPluginConfig);
        page.Apply(legacyPluginConfig);
        Check(
            "retired plugin key values survive the settings page untouched",
            legacyPluginConfig.Prompts.JapanesePlugin == "Legacy ja-JP" &&
            legacyPluginConfig.Prompts.EnglishPlugin == "Legacy en-US" &&
            legacyPluginConfig.Prompts.ChinesePlugin == "Legacy zh-CN");
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

        // 2.3-A：目标语言下拉直接引用共享列表的同一批实例（母语写法），
        // 旧配置的中文名/语言代码别名仍能命中，无需配置迁移。
        var targetBox = (System.Windows.Controls.ComboBox?)page.FindName("TargetLanguageBox");
        Check(
            "general page target languages reference the shared native-name list",
            targetBox is not null &&
            targetBox.Items.Count == LanguageChoices.All.Count &&
            ReferenceEquals(targetBox.Items[3], LanguageChoices.All[3]) &&
            LanguageChoices.All[3].Name == "日本語");

        page.Load(new AppConfig { General = new GeneralConfig { TargetLanguage = "日语" } });
        Check(
            "general page resolves legacy chinese alias to native display name",
            targetBox!.SelectedItem is LanguageChoice legacyChoice && legacyChoice.Name == "日本語");
    }

    /// <summary>共享语言列表（issue 015 / 2.3）：母语写法唯一、旧别名命中、探测现算映射回列表项。</summary>
    private static void TestSharedLanguageChoicesAndDetectionMapping()
    {
        EnsureThemedApplication();

        Check(
            "shared language list exposes native display names in a stable order",
            LanguageChoices.All.Select(choice => choice.Name).SequenceEqual(new[]
            {
                "中文（简体）", "繁體中文", "English", "日本語", "한국어",
                "Русский", "Français", "Deutsch", "Español", "Português", "Italiano",
            }));
        Check(
            "shared language list display names are unique",
            LanguageChoices.All.Select(choice => choice.Name).Distinct().Count() == LanguageChoices.All.Count);

        // 旧配置值（中文习惯名、英文名、语言代码）全部仍可命中；真正的生造值不命中。
        Check(
            "shared language choices still match legacy configuration values",
            LanguageChoices.Find("日语")?.Name == "日本語" &&
            LanguageChoices.Find("ja")?.Name == "日本語" &&
            LanguageChoices.Find("英语")?.Name == "English" &&
            LanguageChoices.Find("en")?.Name == "English" &&
            LanguageChoices.Find("中文（繁体）")?.Name == "繁體中文" &&
            LanguageChoices.Find("中文（繁體）") is null &&
            LanguageChoices.Find("完全不成语言的值") is null);

        // 探测现算 → 映射回共享列表对应项（不是三档硬编码）：假名/纯汉字日语、简体独占字、仅拉丁、兜底。
        Check(
            "detection maps kana source to native japanese name",
            LanguageChoices.FromDetection(LanguageDetector.Detect("こ こ は  通[とお]れないよ", "中文（简体）")) == "日本語");
        Check(
            "detection maps pure hanzi japanese to native japanese name",
            LanguageChoices.FromDetection(LanguageDetector.Detect("進入禁止", "中文（简体）")) == "日本語");
        Check(
            "detection maps simplified chinese source to its native name",
            LanguageChoices.FromDetection(LanguageDetector.Detect("这是一个测试", "English")) == "中文（简体）");
        Check(
            "detection maps latin only source to english",
            LanguageChoices.FromDetection(LanguageDetector.Detect("Hello world", "中文（简体）")) == "English");
        Check(
            "detection falls back to japanese for empty or neutral text",
            LanguageChoices.FromDetection(LanguageDetector.Detect("", "日本語")) == "日本語");
    }

    /// <summary>主题资源与控件模板必须可用；顺带验证两份色板与两份控件字典都能解析。</summary>
    private static void TestThemeResourcesResolve()
    {
        EnsureThemedApplication();
        var application = System.Windows.Application.Current!;
        var expectedKeys = new[]
        {
            "Brush.Window.Background", "Brush.Card.Background", "Brush.Accent", "Brush.Accent.Subtle", "Brush.Text.Primary",
            "Font.Ui", "Radius.Control", "ToggleSwitch", "Button.Accent", "Nav.List", "TextBox.Multiline",
            "Badge.Tag",
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

    /// <summary>
    /// 收藏管理页：元数据、筛选/搜索、事件回流刷新与配置契约（Validate 恒空、Apply 无副作用）。
    /// 使用真实 SQLite 临时库，顺带验证设置页读到的是持久化后的数据。
    /// </summary>
    private static void TestFavoritesSettingsPageFilteringAndEvents()
    {
        EnsureThemedApplication();

        var databasePath = Path.Combine(Path.GetTempPath(), $"nanikore-ui-selftest-{Guid.NewGuid():N}.db");
        try
        {
            var service = new SqliteFavoriteService(databasePath);
            var page = new FavoritesPage(service);

            Check("favorites page exposes title and star glyph", page.Title == "收藏" && page.Glyph == "\uE734");
            Check("favorites page shows the live database path", page.DatabasePathText.Text == databasePath);
            Check("favorites page exposes database path copy button", page.CopyPathButton is not null);
            Check("favorites page validate never blocks saving", page.Validate() is null);

            // 布局契约（issue 015 / 2.2 + 主人追加要求）：横向导航条直铺，旧版两个 SettingsGroup 全部移除；
            // 关键词搜索条占星号列、填满剩余横向空间；语言筛选下拉先占位「全部」（2.3-C 填共享语言列表）。
            Check(
                "favorites page uses flat nav bar without settings groups",
                page.LanguageBox.SelectedItem as string == "全部" &&
                FindDescendants<NaniKore.App.Controls.SettingsGroup>(page).Count() == 0);

            page.Load(new AppConfig());

            service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "通る", Note = "读音: とおる" }).Wait();
            service.AddFavoriteAsync(new FavoriteItem { Category = "语法", Content = "～ない" }).Wait();
            service.AddFavoriteAsync(new FavoriteItem { Category = "短语", Content = "過ぎる" }).Wait();

            // 事件回流：收藏后无需手动点刷新，列表应自动跟上。
            Check("favorites page refreshes on favorite added event", page.FavoriteList.Items.Count == 3);
            Check("favorites page count text lists total", page.CountText.Text == "共 3 条收藏");

            // 类别筛选
            page.CategoryBox.SelectedItem = "语法";
            Check("favorites page filters by category", page.FavoriteList.Items.Count == 1);
            Check("favorites page count text reports filtered hits", page.CountText.Text == "筛选命中 1 / 3 条");

            // 关键词命中 note
            page.CategoryBox.SelectedItem = "全部";
            page.SearchBox.Text = "とおる";
            Check("favorites page searches note content", page.FavoriteList.Items.Count == 1);

            // 空结果露出温和提示
            page.SearchBox.Text = "不存在的词";
            Check("favorites page shows empty hint when nothing matches", page.EmptyHint.Visibility == Visibility.Visible);

            // 事件回流：删除后列表同样自动刷新
            page.SearchBox.Text = string.Empty;
            service.RemoveFavoriteAsync("～ない").Wait();
            Check("favorites page refreshes on favorite removed event", page.FavoriteList.Items.Count == 2);

            // 语言筛选（2.3-C）：与类别、关键词三者 AND；新收藏显式写入语言。
            service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "run", Language = "English" }).Wait();
            page.LanguageBox.SelectedItem = "English";
            Check("favorites page filters by language", page.FavoriteList.Items.Count == 1);
            page.LanguageBox.SelectedItem = "日本語";
            Check("favorites page language filter keeps japanese rows", page.FavoriteList.Items.Count == 2);
            page.LanguageBox.SelectedItem = "全部";
            Check("favorites page language filter restores all rows", page.FavoriteList.Items.Count == 3);

            // 就地编辑往返（issue 015 / 2.5）：进入编辑态 → 保存（归一化 + 多字段）→ 还原展示态。
            var editTarget = service.GetAllFavorites().First(x => x.Content == "通る");
            page.BeginEdit(editTarget);
            Check(
                "favorites page enters inline edit mode",
                page.FavoriteList.Items.OfType<FavoriteEditViewModel>().Count() == 1);

            var saved = page.SaveEditAsync(editTarget, "通[とお]る", "读音: とおる\n释义: 修订", "短语", "日本語")
                .GetAwaiter().GetResult();
            Check("favorites page inline edit saves successfully", saved);
            var stored = service.GetAllFavorites().Single(x => x.Id == editTarget.Id);
            Check(
                "favorites page inline edit persists normalized fields",
                stored.Content == "通る" && stored.Category == "短语" && stored.Note!.Contains("修订"));
            Check(
                "favorites page inline edit restores display mode",
                page.FavoriteList.Items.OfType<FavoriteDisplayViewModel>()
                    .Any(vm => vm.Item.Content == "通る" && vm.Item.Category == "短语"));

            // 归一化冲突：改后内容命中另一条收藏 → 拒绝且原行不动。
            var collision = service.GetAllFavorites().First(x => x.Content == "過ぎる");
            var rejected = page.SaveEditAsync(collision, "通る (とおる)", null, "单词", "日本語")
                .GetAwaiter().GetResult();
            Check("favorites page inline edit rejects normalized conflict", !rejected);
            Check(
                "favorites page inline edit keeps original on conflict",
                service.GetAllFavorites().Any(x => x.Id == collision.Id && x.Content == "過ぎる"));

            // Apply 不得触碰配置副本：收藏页不拥有任何配置字段，快照必须保持默认值
            var defaults = new AppConfig();
            var snapshot = new AppConfig();
            page.Apply(snapshot);
            Check(
                "favorites page apply leaves configuration untouched",
                snapshot.General.AutoExplain == defaults.General.AutoExplain &&
                snapshot.General.TargetLanguage == defaults.General.TargetLanguage &&
                snapshot.Pipeline.Mode == defaults.Pipeline.Mode &&
                snapshot.Hotkeys.Capture == defaults.Hotkeys.Capture &&
                snapshot.Profiles.Count == defaults.Profiles.Count);
        }
        finally
        {
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
                // 句柄延迟释放时由操作系统回收临时文件。
            }
        }
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
        panel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在分析复合适词与时态"));
        var transBox = (System.Windows.Controls.TextBox?)panel.FindName("TranslationTextBox");
        Check(
            "thinking delta displays throttled thinking placeholder in TranslationTextBox",
            transBox?.Text.StartsWith("💭 [思考中]") == true &&
            transBox.Text.Contains("正在分析复合适词与时态"));

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
            translateBtn?.Content as string == "翻译" &&
            transBox?.Text == "我是学生。" &&
            !transBox.Text.Contains("思考中"));

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

        // 8.1 再次点击翻译/重译或发起翻译时，编辑内容立即提交为单一事实源，绝不跳回初次识别到的原文
        var translateRequestedRaised = false;
        panel.TranslateRequested += (_, _) => translateRequestedRaised = true;
        translateBtn?.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(
            "clicking 重译 raises TranslateRequested with modified text intact",
            translateRequestedRaised &&
            panel.SourceText == "私は 先生です。" &&
            viewerBorder?.Visibility == System.Windows.Visibility.Visible &&
            sourceBox?.Visibility == System.Windows.Visibility.Collapsed);

        panel.BeginTranslation();
        Check(
            "begin translation preserves modified source instead of reverting to initial source",
            panel.SourceText == "私は 先生です。");

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

        // 验证精讲前先有译文，并在精讲思考链中暂存与正文到达后无缝恢复
        var expTransBox = (System.Windows.Controls.TextBox?)expPanel.FindName("TranslationTextBox");
        expPanel.Append(new UnderstandDelta(DeltaChannel.Translation, "这里过不去哦。"));
        Check("translation text is established before explanation", expTransBox?.Text == "这里过不去哦。");

        // 验证 BeginExplanation 进入生成态
        expPanel.BeginExplanation();
        Check(
            "BeginExplanation updates header and ensures expander expanded",
            expander?.Header as string == "📖 精讲与语法解析 (正在生成…)" &&
            expander?.IsExpanded == true &&
            string.IsNullOrEmpty(expPanel.ExplanationText));

        // 验证精讲思考增量就地刷新，但正文到达瞬间无缝恢复原有译文
        expPanel.Append(new UnderstandDelta(DeltaChannel.Thinking, "正在深度思考语法结构"));
        Check(
            "thinking during explanation displays placeholder temporarily",
            expTransBox?.Text.Contains("正在深度思考语法结构") == true);

        // 验证 Append 接收 DeltaChannel.Explanation 流式增量并输出至 ExplanationView，同时恢复译文
        expPanel.Append(new UnderstandDelta(DeltaChannel.Explanation, "### [词汇/短语]\n- **通る** | とおる"));
        Check(
            "explanation delta arriving seamlessly restores saved translation in TranslationTextBox",
            expTransBox?.Text == "这里过不去哦。" &&
            !expTransBox.Text.Contains("思考中"));

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
        Check("CompleteExplanation retains restored translation text", expTransBox?.Text == "这里过不去哦。");

        // 验证精讲中途若只出现思考便异常结束，CompleteExplanation 兜底恢复译文
        expPanel.BeginExplanation();
        expPanel.Append(new UnderstandDelta(DeltaChannel.Thinking, "未完成的精讲思考"));
        Check("expander thinking replaces text temporarily again", expTransBox?.Text.Contains("未完成的精讲思考") == true);
        expPanel.CompleteExplanation(false);
        Check(
            "CompleteExplanation on failure restores saved translation text",
            expTransBox?.Text == "这里过不去哦。" &&
            !expTransBox.Text.Contains("思考中"));

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

            // 18. 2.3-C：划词入口会把面板本轮探测的源语言灌入气泡语言下拉（含假名原文 → 日本語）。
            Check(
                "selection bubble language default follows panel detected source language",
                expPanel.CurrentSourceLanguage == "日本語" &&
                (selectionBubble?.LanguageComboBox.SelectedItem as LanguageChoice)?.Name == "日本語");

            // 19. 验证当气泡处于打开状态时，点击窗口内其他文字或区域（PreviewMouseDown）能立即隐藏气泡
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

            // 20. 1.1-B 回归：StaysOpen 契约 + 收藏触发前移到 PreviewMouseLeftButtonDown，
            // 气泡内点击（编辑框/下拉/收藏按钮）既不会关闭气泡，也不会在文本框上二次冒出新气泡。
            Check(
                "bubble popup stays open so in-popup clicks never race with outside dismissal",
                selectionBubble?.BubblePopup.StaysOpen == true);

            selectionBubble?.ShowAt(new System.Windows.Point(100, 100), "食べる");
            var favoriteButton = selectionBubble?.FavoriteButton;
            var favoritePreviewDown = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                Source = favoriteButton,
            };
            favoriteButton?.RaiseEvent(favoritePreviewDown);
            Check(
                "favorite action triggers on preview mouse down without closing via click race",
                favoritePreviewDown.Handled &&
                mockService.IsFavorited("食べる") &&
                selectionBubble?.BubblePopup.IsOpen == true);

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
                selectionBubble?.ContainsScreenPoint(new System.Windows.Point(0, 0)) == false);
            selectionBubble?.ShowAt(new System.Windows.Point(100, 100), "東京");
            Check(
                "bubble reopens after close without losing favorite detection state",
                selectionBubble?.BubblePopup.IsOpen == true);
            selectionBubble?.Hide();

            // 22. 1.2 布局契约：内容区外包整页 ScrollViewer（Auto），精讲区另有 MaxHeight 形成二级滚动。
            var contentScroller = (System.Windows.Controls.ScrollViewer?)expPanel.FindName("ContentScrollViewer");
            Check(
                "result panel content area scrolls as a whole while explanation keeps local max height",
                contentScroller is not null &&
                contentScroller.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto &&
                contentScroller.HorizontalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Disabled &&
                explanationView is not null &&
                explanationView.MaxHeight > 0);

            selectionBubble?.Hide();
        }
    }
}

