using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Ocr.Models;
using NaniKore.Overlay;
using NaniKore.Phonetics;
using NaniKore.Translation;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（设置页与窗口）：主题资源、自适应卡片布局、各设置页绑定契约、
/// 设置窗口尺寸记忆回写、收藏管理页筛选与事件回流、窗口层级行为。
/// </summary>
internal static partial class SelfTest
{
    private static void RunSettingsUiTests()
    {
        EnsureThemedApplication();
        TestSettingsPagesUseAdaptiveLayout();
        TestGeneralPageResourceIdleDuration();
        TestGeneralPagePipelineDependencyVisibility();
        TestGeneralPagePhoneticsCards();
        TestWindowBackgroundConfiguration();
        TestSharedLanguageChoicesAndDetectionMapping();
        TestTranslationPagePromptEditing();
        TestAboutPageRepositoryEntry();
        TestSettingsCloseSizeMemory();
        TestSettingsSaveSnapshots();
        TestSettingsCloseSizeWriteBackKeepsExternalUpdates();
        TestFavoritesSettingsPageFilteringAndEvents();
        TestFontsConfiguration();
        TestWindowProperties();
        TestBorderlessWindowResize();
    }

    /// <summary>
    /// 无边框窗口缩放：设置窗与结果面板都是自绘边框（<c>WindowDecorations="None"</c>），
    /// 关掉系统装饰就一并失去系统缩放边框，必须由 <see cref="WindowResizeGrip"/> 补齐四边/四角拖拽；
    /// 同时验证握柄摆位与缩放纯决策（只改被拖方向、按最小/最大尺寸收敛、西/北边反向位移保持对边不动）。
    /// </summary>
    private static void TestBorderlessWindowResize()
    {
        EnsureThemedApplication();

        // 1. 两个自绘边框窗口都必须挂上握柄层，否则窗口回退成「只能移动、不能改尺寸」
        Check(
            "borderless result panel hosts the window resize grip layer",
            FindDescendants<WindowResizeGrip>(new ResultPanelWindow()).Any());
        Check(
            "borderless settings window hosts the window resize grip layer",
            FindDescendants<WindowResizeGrip>(NewSettingsWindow()).Any());

        // 2. 八向命中区：四角等于握柄厚度，四边铺满剩余长度，中间留空不遮挡内容
        var grip = new WindowResizeGrip();
        grip.Measure(new Size(800, 600));
        grip.Arrange(new Rect(0, 0, 800, 600));
        var zones = grip.Children.Select(child => child.Bounds).ToList();
        Check(
            "resize grip splits the window edges into eight corner and side zones",
            zones.Count == 8 &&
            IsSameRect(zones[0], new Rect(0, 0, 12, 12)) &&
            IsSameRect(zones[1], new Rect(12, 0, 776, 6)) &&
            IsSameRect(zones[2], new Rect(788, 0, 12, 12)) &&
            IsSameRect(zones[3], new Rect(0, 12, 6, 576)) &&
            IsSameRect(zones[4], new Rect(794, 12, 6, 576)) &&
            IsSameRect(zones[5], new Rect(0, 588, 12, 12)) &&
            IsSameRect(zones[6], new Rect(12, 594, 776, 6)) &&
            IsSameRect(zones[7], new Rect(788, 588, 12, 12)),
            string.Join(";", zones));
        Check(
            "resize grip zones carry distinct cursors",
            grip.Children.All(child => child.Cursor is not null) &&
            grip.Children.Select(child => child.Tag).OfType<WindowEdge>().Distinct().Count() == 8);

        // 3. 缩放纯决策：只有被拖动的方向参与计算，对边保持不动
        const double infinite = double.PositiveInfinity;
        Check(
            "dragging the east edge grows width only",
            WindowResizeGrip.ResolveSize(WindowEdge.East, 960, 660, 40, 25, 440, 300, infinite, infinite) == (1000, 660));
        Check(
            "dragging the south edge grows height only",
            WindowResizeGrip.ResolveSize(WindowEdge.South, 960, 660, 40, 25, 440, 300, infinite, infinite) == (960, 685));
        Check(
            "dragging the north west corner grows both directions inversely",
            WindowResizeGrip.ResolveSize(WindowEdge.NorthWest, 960, 660, -40, -25, 440, 300, infinite, infinite) == (1000, 685));

        // 4. 最小/最大尺寸收敛：拖过头不会把窗口压成不可操作
        Check(
            "resize respects the declared minimum size",
            WindowResizeGrip.ResolveSize(WindowEdge.SouthEast, 960, 660, -5000, -5000, 440, 300, infinite, infinite) == (440, 300));
        Check(
            "resize respects the declared maximum size",
            WindowResizeGrip.ResolveSize(WindowEdge.SouthEast, 960, 660, 5000, 5000, 440, 300, 1280, 800) == (1280, 800));

        // 5. 西/北边缩放时窗口位置反向位移，让对边（右/下）钉在原处；东/南边不动位置
        Check(
            "dragging the west edge pins the opposite edge by moving the window",
            WindowResizeGrip.ResolvePosition(WindowEdge.West, new PixelPoint(100, 100), 960, 660, 920, 660, 1) == new PixelPoint(140, 100));
        Check(
            "dragging the north edge pins the opposite edge by moving the window",
            WindowResizeGrip.ResolvePosition(WindowEdge.North, new PixelPoint(100, 100), 960, 660, 960, 690, 1) == new PixelPoint(100, 70));
        Check(
            "dragging the east edge leaves the window position untouched",
            WindowResizeGrip.ResolvePosition(WindowEdge.East, new PixelPoint(100, 100), 960, 660, 1000, 660, 1) == new PixelPoint(100, 100));
        Check(
            "west edge window shift converts logical size to physical pixels",
            WindowResizeGrip.ResolvePosition(WindowEdge.West, new PixelPoint(100, 100), 960, 660, 940, 660, 1.5) == new PixelPoint(130, 100));
    }

    /// <summary>Rect 比较走亚像素容差，避免布局取整差异误报。</summary>
    private static bool IsSameRect(Rect actual, Rect expected) =>
        Math.Abs(actual.X - expected.X) < 0.01 &&
        Math.Abs(actual.Y - expected.Y) < 0.01 &&
        Math.Abs(actual.Width - expected.Width) < 0.01 &&
        Math.Abs(actual.Height - expected.Height) < 0.01;

    /// <summary>
    /// 关于页 GitHub 入口：图标按钮常驻在品牌卡内、命中测试可见，目标地址固定指向项目仓库；
    /// 只验证按钮与地址常量，不真的拉起浏览器。
    /// </summary>
    private static void TestAboutPageRepositoryEntry()
    {
        EnsureThemedApplication();
        var page = new AboutPage();
        page.Load(new AppConfig());
        var view = page.View;
        _ = new Window { Content = view, Width = 760, Height = 600 };
        view.Measure(new Avalonia.Size(760, double.PositiveInfinity));

        var button = Find<Button>(view, "RepositoryButton");
        Check(
            "about page exposes a clickable GitHub repository button",
            button is { IsVisible: true, IsEnabled: true } &&
            ToolTip.GetTip(button) is string tip && tip.Contains(AboutPage.RepositoryUrl, StringComparison.Ordinal));
        Check(
            "about page repository button targets the project GitHub repository",
            AboutPage.RepositoryUrl == "https://github.com/Stareven233/NaniKore");
    }

    /// <summary>
    /// 布局约定：页面根元素必须按内容自适应高度（由宿主 ScrollViewer 负责滚动），
    /// 且所有设置行都通过 SettingsRow 承载，从而共享同一套 DPI 与字体缩放行为。
    /// </summary>
    private static void TestSettingsPagesUseAdaptiveLayout()
    {
        EnsureThemedApplication();

        foreach (var page in new ISettingsPage[] { new GeneralPage(), new TranslationPage(), new HotkeyPage(), new AboutPage() })
        {
            var view = page.View;
            page.Load(new AppConfig());
            var hostWindow = new Window { Content = view, Width = 760, Height = 600 };
            view.Measure(new Avalonia.Size(760, double.PositiveInfinity));
            var rows = FindDescendants<SettingsRow>(view).Distinct().ToList();
            var groups = FindDescendants<SettingsGroup>(view).Distinct().ToList();
            Check(
                $"{page.Title} page builds an adaptive card layout without fixed pixel positions",
                double.IsNaN(view.Width) &&
                double.IsNaN(view.Height) &&
                view.DesiredSize.Height > 0 &&
                groups.Count > 0 &&
                rows.TrueForAll(row => double.IsNaN(row.Height) && !string.IsNullOrEmpty(row.Header)));
        }

        var general = new GeneralPage();
        general.Load(new AppConfig());
        var generalWindow = new Window { Content = general.View, Width = 760, Height = 600 };
        var describedRows = FindDescendants<SettingsRow>(general.View)
            .Distinct()
            .Count(row => !string.IsNullOrEmpty(row.Description));
        Check(
            "general page attaches descriptions to the rows they explain",
            describedRows > 0);

        var hotkeys = new HotkeyPage();
        hotkeys.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "ctrl+shift+a", Paste = "f1" } });
        var hotkeyWindow = new Window { Content = hotkeys.View, Width = 760, Height = 600 };
        Check(
            "hotkey page hosts two independent recorder boxes bound to capture and paste",
            FindDescendants<HotkeyRecorderBox>(hotkeys.View).Distinct().Count() == 2 &&
            Find<HotkeyRecorderBox>(hotkeys, "CaptureRecorder") is { Gesture: "Ctrl+Shift+A", AllowUnbind: false } &&
            Find<HotkeyRecorderBox>(hotkeys, "PasteRecorder") is { Gesture: "F1", AllowUnbind: true });

        hotkeys.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "Ctrl+OemPlus", Paste = "control+=" } });
        Check("hotkey page rejects duplicate gestures expressed through aliases",
            hotkeys.Validate() is { } duplicateError && duplicateError.Contains("相同", StringComparison.Ordinal));
        hotkeys.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "Ctrl+OemPlus", Paste = "Ctrl+Add" } });
        Check("hotkey page keeps main-keyboard and numpad plus bindings independent", hotkeys.Validate() is null);
    }

    /// <summary>回收设置始终可见；自定义输入先报错再保存，绝不以默认值吞掉非法输入。</summary>
    private static void TestGeneralPageResourceIdleDuration()
    {
        var page = new GeneralPage();
        var config = new AppConfig();
        page.Load(config);
        var box = Find<AutoCompleteBox>(page, "LocalResourceIdleTimeoutBox")!;
        Check("general page loads the never-reclaim default and duration presets",
            box.Text == "0" && box.ItemsSource!.Cast<string>().SequenceEqual(new[] { "0", "30s", "10m", "24h" }) &&
            !box.IsTextCompletionEnabled && page.Validate() is null);

        foreach (var mode in new[] { "local_ocr", "vision_oneshot" })
        {
            page.SetPipelineMode(mode);
            Check($"local resource settings remain visible in {mode}",
                Find<Control>(page, "LocalResourcesGroup") is { IsVisible: true } && box.IsVisible);
        }

        config.General.LocalResourceIdleTimeout = "24h";
        page.Load(config);
        Check("general page loads the configured resource idle duration", box.Text == "24h");
        foreach (var invalid in new[] { "", " ", "-1", "1.5m", "1 s", "9223372036854775807h" })
        {
            box.Text = invalid;
            Check($"general page rejects idle duration '{invalid}' without normalizing input or configuration",
                page.Validate() is { } error && error.Contains("闲置回收", StringComparison.Ordinal) &&
                box.Text == invalid && config.General.LocalResourceIdleTimeout == "24h");
        }

        foreach (var (input, expected) in new[] { (" 7M ", "7m"), ("42", "42"), ("0", "0") })
        {
            box.Text = input;
            var applied = AppConfigCloner.Clone(config);
            Check($"general page accepts custom idle duration '{input}'", page.Validate() is null);
            page.Apply(applied);
            Check($"general page saves trimmed lowercase idle duration '{expected}' without mutating source",
                applied.General.LocalResourceIdleTimeout == expected && config.General.LocalResourceIdleTimeout == "24h");
            page.Load(applied);
            Check($"general page reloads applied idle duration '{expected}'", box.Text == expected);
        }
    }

    /// <summary>
    /// 通用页流水线依赖可见性：本地 OCR 依赖组只在 local_ocr 模式露出；
    /// 依赖按钮文案直接映射本地清单状态，无下载入口时禁用；翻译页行为选项读写闭环。
    /// </summary>
    private static void TestGeneralPagePipelineDependencyVisibility()
    {
        EnsureThemedApplication();
        var page = new GeneralPage();
        var config = new AppConfig { Pipeline = new PipelineConfig { Mode = "vision_oneshot" } };
        page.Load(config);
        Check(
            "general page hides local OCR dependencies for vision pipeline",
            page.FindControl<Control>("LocalOcrDependenciesGroup") is { IsVisible: false });

        page.Load(new AppConfig { Pipeline = new PipelineConfig { Mode = "local_ocr" } });
        Check(
            "general page shows local OCR dependencies for local pipeline",
            page.FindControl<Control>("LocalOcrDependenciesGroup") is { IsVisible: true });

        var ocrButton = Find<Button>(page, "DownloadDependenciesButton");
        var ocrStatus = Find<InfoBar>(page, "DependencyStatus");
        page.RefreshDependenciesSync();
        var isOcrComplete = OcrDependencyManifest.IsComplete();
        Check(
            "ocr dependency button mirrors local manifest state and stays disabled without a downloader",
            ocrButton?.Content as string == GeneralPage.OcrButtonLabel(isOcrComplete) &&
            ocrButton?.IsEnabled == false);
        Check(
            "ocr dependency status bar does not display separate readiness hint on open",
            ocrStatus?.IsOpen == false);

        var pageWithOcrDownloader = new GeneralPage(downloadDependencies: (_, _) => Task.CompletedTask);
        var enabledOcrButton = Find<Button>(pageWithOcrDownloader, "DownloadDependenciesButton");
        Check(
            "ocr dependency button enables once a downloader is injected",
            enabledOcrButton?.IsEnabled == true);

        // 跨页切换必须反映未保存的流程选择，并保留通用页已编辑的字段。
        var settings = NewSettingsWindow();
        var host = Find<ContentControl>(settings, "PageHost")!;
        var generalPage = (GeneralPage)host.Content!;
        Find<AutoCompleteBox>(generalPage, "UiFontBox")!.Text = "Consolas";
        settings.SelectTab(SettingsTab.Translation);
        var translationPage = (TranslationPage)host.Content!;
        Find<ComboBox>(translationPage, "PipelineModeBox")!.SelectedItem = "vision_oneshot";
        settings.SelectTab(SettingsTab.General);
        Check(
            "returning to general reflects the unsaved vision pipeline without resetting font edits",
            Find<Control>(generalPage, "LocalOcrDependenciesGroup") is { IsVisible: false } &&
            Find<AutoCompleteBox>(generalPage, "UiFontBox")!.Text == "Consolas");
        settings.SelectTab(SettingsTab.Translation);
        Find<ComboBox>(translationPage, "PipelineModeBox")!.SelectedItem = "local_ocr";
        settings.SelectTab(SettingsTab.General);
        Check(
            "returning to general restores local OCR dependencies for the local pipeline",
            Find<Control>(generalPage, "LocalOcrDependenciesGroup") is { IsVisible: true });

        var autoExplainPage = new TranslationPage();
        var autoExplainConfig = new AppConfig { General = new GeneralConfig { AutoExplain = true } };
        autoExplainPage.Load(autoExplainConfig);
        var autoExplainToggle = Find<ToggleSwitch>(autoExplainPage, "AutoExplainToggle");
        Check(
            "translation page loads and applies AutoExplain setting accurately",
            autoExplainToggle?.IsChecked == true);

        autoExplainToggle!.IsChecked = false;
        var appliedConfig = new AppConfig();
        autoExplainPage.Apply(appliedConfig);
        Check(
            "translation page applies AutoExplain toggle change to configuration",
            !appliedConfig.General.AutoExplain);

        var pasteAutoTranslatePage = new TranslationPage();
        pasteAutoTranslatePage.Load(new AppConfig { General = new GeneralConfig { PasteAutoTranslate = false } });
        var pasteAutoTranslateToggle = Find<ToggleSwitch>(pasteAutoTranslatePage, "PasteAutoTranslateToggle");
        Check(
            "translation page loads the paste selection auto-translate switch accurately",
            pasteAutoTranslateToggle?.IsChecked == false);

        pasteAutoTranslateToggle!.IsChecked = true;
        var pasteAppliedConfig = new AppConfig();
        pasteAutoTranslatePage.Apply(pasteAppliedConfig);
        Check(
            "translation page applies the paste selection auto-translate switch to configuration",
            pasteAppliedConfig.General.PasteAutoTranslate);

        var targetBox = Find<ComboBox>(autoExplainPage, "TargetLanguageBox");
        var targetItems = targetBox?.ItemsSource?.Cast<LanguageChoice>().ToList() ?? [];
        Check(
            "translation page target languages reference the shared native-name list",
            targetBox is not null &&
            targetItems.Count == LanguageChoices.All.Count &&
            ReferenceEquals(targetItems[3], LanguageChoices.All[3]) &&
            LanguageChoices.All[3].Name == "日本語");

        autoExplainPage.Load(new AppConfig { General = new GeneralConfig { TargetLanguage = "日语" } });
        Check(
            "translation page resolves legacy chinese alias to native display name",
            targetBox!.SelectedItem is LanguageChoice legacyChoice && legacyChoice.Name == "日本語");
    }

    /// <summary>
    /// 注音词典卡片：三语按钮与本地状态联动（文案即状态），
    /// 无下载入口时禁用、有入口时启用；注音开关走配置读写闭环。
    /// </summary>
    private static void TestGeneralPagePhoneticsCards()
    {
        EnsureThemedApplication();

        var page = new GeneralPage();
        Check(
            "phonetics group hosts the enabled and rare-only switches",
            page.FindControl<Control>("PhoneticsEnabledToggle") is ToggleSwitch &&
            page.FindControl<Control>("PhoneticsRareOnlyToggle") is ToggleSwitch);

        page.RefreshDependenciesSync();
        foreach (var language in PhoneticLanguages.All)
        {
            var buttonName = $"PhoneticsDownload{char.ToUpperInvariant(language.Code[0])}{language.Code[1..]}";
            var button = Find<Button>(page, buttonName);
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

        var withDownloader = new GeneralPage(downloadPhoneticDictionary: (_, _, _) => Task.CompletedTask);
        var jaButton = Find<Button>(withDownloader, "PhoneticsDownloadJa");
        Check(
            "dictionary card button enables once a downloader is injected",
            jaButton?.IsEnabled == true);

        // 原生语言包卡片（issue 009）：按钮文案跟着状态机走，未注入下载器时只读。
        foreach (var language in NativePhoneticManifest.SupportedLanguages)
        {
            var buttonName = language == NativePhoneticManifest.JapaneseCode ? "NativePackJa" : "NativePackEn";
            var button = Find<Button>(page, buttonName);
            var expected = NativePhoneticPackHostHolder.Default.GetState(language);
            Check(
                $"{language} native pack card mirrors the host state and stays disabled without a downloader",
                button is not null &&
                button.Content as string == GeneralPage.NativePackButtonLabel(expected) &&
                !button.IsEnabled);
        }

        var withNativeDownloader = new GeneralPage(downloadNativePhoneticPack: (_, _, _) => Task.CompletedTask);
        Check(
            "native pack card enables once a downloader is injected",
            Find<Button>(withNativeDownloader, "NativePackJa")?.IsEnabled == true &&
            Find<Button>(withNativeDownloader, "NativePackEn")?.IsEnabled == true);

        var config = new AppConfig { Phonetics = new PhoneticsConfig { Enabled = false, RareOnly = false } };
        page.Load(config);
        var enabledToggle = Find<ToggleSwitch>(page, "PhoneticsEnabledToggle");
        var rareOnlyToggle = Find<ToggleSwitch>(page, "PhoneticsRareOnlyToggle");
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

    /// <summary>背景设置读写与真实解码校验；临时图片同时验证两个已创建窗口的动态资源热更新。</summary>
    private static void TestWindowBackgroundConfiguration()
    {
        WithTempConfigDirectory((directory, _) =>
        {
            var imagePath = Path.Combine(directory, "背景 image.png");
            // 1×1 PNG：真实解码不依赖仓库素材、网络或用户配置。
            File.WriteAllBytes(imagePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var relativePath = Path.Combine("images", "..", "背景 image.png");
            Check(
                "background relative paths resolve against the application directory",
                WindowBackgroundResources.ResolvePath("  " + relativePath + "  ") ==
                Path.Combine(AppContext.BaseDirectory, "背景 image.png"));
            Check(
                "background absolute paths remain absolute after trimming",
                WindowBackgroundResources.ResolvePath("  " + imagePath + "  ") == imagePath);

            var page = new GeneralPage();
            page.Load(new AppConfig
            {
                Windows = new WindowConfig
                {
                    BackgroundImage = relativePath,
                    BackgroundBlur = 24.5,
                    BackgroundOpacity = 0.35,
                    PanelBackgroundOpacity = 0.45,
                },
            });
            var pathBox = Find<TextBox>(page, "BackgroundImageBox")!;
            var blurBox = Find<NumericUpDown>(page, "BackgroundBlurBox")!;
            var opacityBox = Find<NumericUpDown>(page, "BackgroundOpacityBox")!;
            var panelOpacityBox = Find<NumericUpDown>(page, "PanelBackgroundOpacityBox")!;
            Check(
                "general page loads background settings and both opacity percentages",
                pathBox.Text == relativePath && blurBox.Value == 24.5m &&
                opacityBox.Value == 35m && panelOpacityBox.Value == 45m);
            // 临时目录与程序可能分属不同盘符；解码校验使用绝对路径，路径基准单独断言。
            pathBox.Text = imagePath;
            Check("general page accepts a decodable background image", page.Validate() is null);

            pathBox.Text = "  " + imagePath + "  ";
            blurBox.Value = 12.5m;
            opacityBox.Value = 62.5m;
            panelOpacityBox.Value = 27.5m;
            var applied = new AppConfig();
            page.Apply(applied);
            Check(
                "general page trims background paths and converts opacity percentage when applying",
                applied.Windows.BackgroundImage == imagePath &&
                applied.Windows.BackgroundBlur == 12.5 && applied.Windows.BackgroundOpacity == 0.625 &&
                applied.Windows.PanelBackgroundOpacity == 0.275);
            Check("general page accepts an absolute background image", page.Validate() is null);

            pathBox.Text = Path.Combine(directory, "missing.png");
            Check("general page rejects a missing background image", !string.IsNullOrEmpty(page.Validate()));
            var corruptPath = Path.Combine(directory, "corrupt.png");
            File.WriteAllText(corruptPath, "not an image");
            pathBox.Text = corruptPath;
            Check("general page rejects a corrupt background image", !string.IsNullOrEmpty(page.Validate()));

            pathBox.Text = "   ";
            Check("general page accepts clearing the background path", page.Validate() is null);
            page.Apply(applied);
            Check(
                "general page clears an existing background without resetting blur or either opacity",
                applied.Windows.BackgroundImage == string.Empty &&
                applied.Windows.BackgroundBlur == 12.5 && applied.Windows.BackgroundOpacity == 0.625 &&
                applied.Windows.PanelBackgroundOpacity == 0.275);
            blurBox.Value = null;
            Check("general page rejects an empty background blur", !string.IsNullOrEmpty(page.Validate()));
            blurBox.Value = 0;
            opacityBox.Value = null;
            Check("general page rejects an empty background opacity", !string.IsNullOrEmpty(page.Validate()));
            opacityBox.Value = 0;
            panelOpacityBox.Value = null;
            Check("general page rejects an empty panel background opacity", !string.IsNullOrEmpty(page.Validate()));
            // 保留控件自身的范围限制，验证用户可输入的两个端点及百分比落盘。
            Check("panel background opacity editor exposes the full percentage range",
                panelOpacityBox.Minimum == 0 && panelOpacityBox.Maximum == 100);
            foreach (var percent in new[] { 0m, 100m })
            {
                panelOpacityBox.Value = percent;
                Check($"general page accepts panel background opacity at {percent}%", page.Validate() is null);
                page.Apply(applied);
                Check($"general page applies panel background opacity at {percent}%",
                    applied.Windows.PanelBackgroundOpacity == (double)percent / 100);
            }
            Check("general page accepts zero blur and fully transparent background", page.Validate() is null);

            TestSharedWindowBackgroundResources(imagePath);
        });
    }

    /// <summary>直接检查窗口图片层，确保资源变化确实传到现有控件，且效果不施加到正文。</summary>
    private static void TestSharedWindowBackgroundResources(string imagePath)
    {
        var app = Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        WindowBackgroundResources.Apply(new WindowConfig { PanelBackgroundOpacity = 0.2 });
        var settings = NewSettingsWindow();
        var result = new ResultPanelWindow();
        // 背景是根内容网格的首层，避免误取标题栏图标而掩盖绑定回归。
        var layers = new[] { settings, (Window)result }
            .Select(window => ((Grid)((Border)window.Content!).Child!).Children.OfType<Image>().Single())
            .ToArray();
        try
        {
            Check("both windows initially have no background image", layers.All(image => image.Source is null));
            Check("both existing background layers stretch to fill their windows",
                layers.All(image => image.Stretch == Stretch.Fill));

            var page = (GeneralPage)Find<ContentControl>(settings, "PageHost")!.Content!;
            page.Measure(new Size(760, double.PositiveInfinity));
            // 必须找到实际 SettingsGroup 模板的卡片，防止只验证资源而漏掉模板选择器失效。
            var groups = FindDescendants<SettingsGroup>(page).Distinct().ToArray();
            foreach (var group in groups)
            {
                group.ApplyTemplate();
            }
            var cards = groups.SelectMany(group => group.GetVisualDescendants().OfType<Border>()
                .Where(border => ReferenceEquals(border.TemplatedParent, group) && border.Classes.Contains("card")))
                .ToArray();
            Check("background test realizes real settings group cards", groups.Length > 0 && cards.Length == groups.Length);
            var sidebar = Find<ListBox>(settings, "NavigationList")!.Parent as Border;
            Check("background test locates the real settings sidebar", sidebar is not null);
            var titleBars = layers.Select(image => ((Grid)image.Parent!).Children.OfType<Grid>()
                .Single(grid => Grid.GetRow(grid) == 0)).ToArray();
            var inputs = new[]
            {
                Find<TextBox>(page, "BackgroundImageBox")!,
                Find<TextBox>(result, "SourceTextBox")!,
                Find<TextBox>(result, "TranslationTextBox")!,
            };
            Check("background test locates settings and result textboxes", inputs.All(input => input is not null));
            CheckPanels("without image", 1);

            var config = new WindowConfig
            {
                BackgroundImage = imagePath,
                BackgroundBlur = 18.5,
                BackgroundOpacity = 0.35,
                PanelBackgroundOpacity = 0.45,
            };
            WindowBackgroundResources.Apply(config);
            Dispatcher.UIThread.RunJobs();
            var source = layers[0].Source;
            Check(
                "existing settings and result windows share one decoded background image",
                source is Avalonia.Media.Imaging.Bitmap bitmap && bitmap.PixelSize == new PixelSize(1, 1) &&
                ReferenceEquals(source, layers[1].Source));
            Check(
                "both background image layers receive blur and opacity without intercepting input",
                layers.All(image => image.Opacity == 0.35 && image.Effect is BlurEffect { Radius: 18.5 } &&
                                    image.Stretch == Stretch.Fill && !image.IsHitTestVisible));
            CheckPanels("with valid image", 0.45);
            Check(
                "background effects leave window content opacity and effects unchanged",
                new[] { settings, (Window)result }.All(window =>
                    window.Opacity == 1 && window.Effect is null &&
                    window.Content is Border { Opacity: 1, Effect: null }));

            config.BackgroundBlur = 0;
            config.BackgroundOpacity = 0.625;
            config.PanelBackgroundOpacity = 0.275;
            WindowBackgroundResources.Apply(config);
            Dispatcher.UIThread.RunJobs();
            Check(
                "background appearance hot updates reuse the shared image without recreating windows",
                layers.All(image => ReferenceEquals(image.Source, source) && image.Opacity == 0.625 &&
                                    image.Effect is BlurEffect { Radius: 0 }));
            CheckPanels("after opacity update", 0.275);

            // 面板与图片透明度独立，两个端点也必须落到现有画刷，不能让正文整体透明。
            foreach (var opacity in new[] { 0d, 1d })
            {
                config.PanelBackgroundOpacity = opacity;
                WindowBackgroundResources.Apply(config);
                Dispatcher.UIThread.RunJobs();
                CheckPanels($"at panel opacity {opacity}", opacity);
                Check($"panel opacity {opacity} leaves image appearance unchanged",
                    layers.All(image => ReferenceEquals(image.Source, source) && image.Opacity == 0.625));
            }

            config.PanelBackgroundOpacity = 0.275;
            config.BackgroundImage = string.Empty;
            WindowBackgroundResources.Apply(config);
            Dispatcher.UIThread.RunJobs();
            Check(
                "clearing the background hot removes images from both existing windows",
                layers.All(image => image.Source is null));
            CheckPanels("after clearing image", 1);

            // 先恢复有效图片，确保缺失路径真正撤掉已有图片及其透明底色。
            config.BackgroundImage = imagePath;
            WindowBackgroundResources.Apply(config);
            Dispatcher.UIThread.RunJobs();
            CheckPanels("after restoring image", 0.275);
            config.BackgroundImage = Path.Combine(Path.GetDirectoryName(imagePath)!, "missing.png");
            WindowBackgroundResources.Apply(config);
            Dispatcher.UIThread.RunJobs();
            Check("missing background removes images from both existing windows",
                layers.All(image => image.Source is null));
            CheckPanels("after missing image", 1);

            void CheckPanels(string state, double expectedOpacity)
            {
                Check($"effective panel opacity {state}",
                    TryFindResource<object>(app, "WindowBackground.PanelOpacity", app.ActualThemeVariant)
                        is double opacity && opacity == expectedOpacity);
                // 在同一批控件上往返切换主题，验证动态资源既更新透明度又保留当前主题 RGB。
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    CheckBrush("sidebar", sidebar?.Background, "Sidebar");
                    Check($"sidebar content stays opaque {state} {variant}",
                        sidebar is { Opacity: 1, Effect: null });
                    foreach (var titleBar in titleBars)
                    {
                        CheckBrush("window title bar", titleBar.Background, "Sidebar");
                        Check($"title bar content stays opaque {state} {variant}",
                            titleBar.Opacity == 1 && titleBar.Effect is null);
                    }
                    foreach (var card in cards)
                    {
                        CheckBrush("settings group card", card.Background, "Card");
                        Check($"card content stays opaque {state} {variant}", card.Opacity == 1 && card.Effect is null);
                    }
                    foreach (var input in inputs)
                    {
                        CheckBrush($"textbox {input?.Name}", input?.Background, "Control");
                        Check($"textbox content stays opaque {state} {variant}",
                            input is { Opacity: 1, Effect: null } &&
                            input.Foreground is ISolidColorBrush foreground && foreground.Opacity == 1 &&
                            TryFindResource<ISolidColorBrush>(app, "Brush.Text.Primary", variant) is { } text &&
                            foreground.Color == text.Color);
                    }

                    void CheckBrush(string target, IBrush? actual, string role)
                    {
                        var panel = TryFindResource<SolidColorBrush>(app, $"Brush.Panel.{role}", variant);
                        var normal = TryFindResource<SolidColorBrush>(app, $"Brush.{role}.Background", variant);
                        Check($"{target} uses themed panel background {state} {variant}",
                            actual is SolidColorBrush brush && panel is not null && normal is not null &&
                            brush.Opacity == expectedOpacity && panel.Opacity == expectedOpacity &&
                            brush.Color == normal.Color && panel.Color == normal.Color &&
                            normal.Color.A == 255 && normal.Opacity == 1,
                            $"actual={actual}, opacity={(actual as SolidColorBrush)?.Opacity}, expected={expectedOpacity}");
                    }
                }
            }
        }
        finally
        {
            // 释放共享位图并恢复主题，避免后续 UI 自测继承本例资源。
            WindowBackgroundResources.Apply(new WindowConfig());
            app.RequestedThemeVariant = originalTheme;
            Dispatcher.UIThread.RunJobs();
            settings.Close();
            result.Close();
        }
    }

    /// <summary>共享语言列表：母语写法唯一、旧别名命中、探测现算映射回列表项。</summary>
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

        Check(
            "shared language choices still match legacy configuration values",
            LanguageChoices.Find("日语")?.Name == "日本語" &&
            LanguageChoices.Find("ja")?.Name == "日本語" &&
            LanguageChoices.Find("英语")?.Name == "English" &&
            LanguageChoices.Find("en")?.Name == "English" &&
            LanguageChoices.Find("中文（繁体）")?.Name == "繁體中文" &&
            LanguageChoices.Find("中文（繁體）") is null &&
            LanguageChoices.Find("完全不成语言的值") is null);

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

    /// <summary>
    /// 翻译设置页提示词编辑：页面保留 Base 与视觉 Base 两行；
    /// 恢复默认按钮与标题行共享；旧配置里退役插件键的自定义值不得被清空。
    /// </summary>
    private static void TestTranslationPagePromptEditing()
    {
        var config = new AppConfig();
        var page = new TranslationPage();
        page.Load(config);

        var localPromptBox = Find<TextBox>(page, "PromptBox");
        var visionPromptBox = Find<TextBox>(page, "VisionPromptBox");
        var localPromptRow = Find<SettingsRow>(page, "LocalPromptRow");
        var visionPromptRow = Find<SettingsRow>(page, "VisionPromptRow");
        var restoreLocalButton = Find<Button>(page, "RestorePromptButton");
        var restoreVisionButton = Find<Button>(page, "RestoreVisionPromptButton");

        Check(
            "translation settings retire the language plugin editor and keep base and vision rows",
            page.FindControl<Control>("PluginLanguageBox") is null &&
            page.FindControl<Control>("PluginPromptBox") is null &&
            page.FindControl<Control>("RestorePluginPromptButton") is null &&
            localPromptBox?.Text == PromptTemplates.DefaultTranslationBasePrompt &&
            visionPromptBox?.Text == PromptTemplates.DefaultVisionBasePrompt);

        Check(
            "prompt restore buttons share the title row with concise labels",
            restoreLocalButton?.Content as string == "恢复默认" &&
            restoreVisionButton?.Content as string == "恢复默认" &&
            ReferenceEquals(localPromptRow?.HeaderAction, restoreLocalButton) &&
            ReferenceEquals(visionPromptRow?.HeaderAction, restoreVisionButton));

        visionPromptBox!.Text = "temporary";
        RaiseClick(restoreVisionButton!);
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

        page.Load(legacyPluginConfig);
        page.Apply(legacyPluginConfig);
        Check(
            "retired plugin key values survive the settings page untouched",
            legacyPluginConfig.Prompts.JapanesePlugin == "Legacy ja-JP" &&
            legacyPluginConfig.Prompts.EnglishPlugin == "Legacy en-US" &&
            legacyPluginConfig.Prompts.ChinesePlugin == "Legacy zh-CN");
    }

    /// <summary>
    /// 字体可配置化（issue 033）：配置→资源→控件三层的实际生效链与设置页输入控件语义。
    /// </summary>
    private static void TestFontsConfiguration()
    {
        EnsureThemedApplication();
        if (Application.Current is not { } app)
        {
            Check("Fonts.ApplicationAvailable", false);
            return;
        }

        // 1. 模板默认值进运行期配置（文本模板本体随发布包带出，直接读当前程序目录那份）
        var templatePath = Path.Combine(AppContext.BaseDirectory, AppPaths.TemplateFileName);
        Check("Fonts.TemplateFileShipped", File.Exists(templatePath));
        if (File.Exists(templatePath))
        {
            WithTempConfigDirectory((dataDirectory, templateDirectory) =>
            {
                File.Copy(templatePath, Path.Combine(templateDirectory, AppPaths.TemplateFileName), overwrite: true);
                var store = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    "Fonts.TemplateDefaultsReachRuntime",
                    store.Current.Fonts.Ui == "Microsoft YaHei UI" &&
                    store.Current.Fonts.Prompt == "YaHei Monaco Hybird,Consolas",
                    $"ui={store.Current.Fonts.Ui}, prompt={store.Current.Fonts.Prompt}");

                // 空串必须落成 ""（OmitNull 不省略空串）：清空字体不能把 fonts 段塔成 {}
                store.Current.Fonts = new FontConfig { Ui = string.Empty, Prompt = "Consolas" };
                store.AtomicWrite(store.Current);
                var reloaded = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    "Fonts.EmptyStringRoundTrips",
                    reloaded.Current.Fonts.Ui.Length == 0 && reloaded.Current.Fonts.Prompt == "Consolas",
                    $"ui={reloaded.Current.Fonts.Ui}, prompt={reloaded.Current.Fonts.Prompt}");
            });
        }

        // 2. Apply 是唯一写入口：两个键都进应用资源
        FontResources.Apply(new FontConfig { Ui = "Consolas", Prompt = "Consolas" });
        Check(
            "Fonts.ApplyWritesResourceKeys",
            app.Resources.TryGetValue(FontResources.UiKey, out var uiResource) &&
            uiResource is FontFamily { Name: "Consolas" } &&
            app.Resources.TryGetValue(FontResources.PromptKey, out var promptResource) &&
            promptResource is FontFamily { Name: "Consolas" });

        // 3. 提示词编辑框跟随 Font.Prompt（DynamicResource 需要挂到资源宿主上才能解析）
        var translationPage = new TranslationPage();
        translationPage.Load(new AppConfig());
        _ = new Window { Content = translationPage.View, Width = 760, Height = 600 };
        translationPage.View.Measure(new Avalonia.Size(760, 900));
        var promptBox = Find<TextBox>(translationPage, "PromptBox");
        var visionPromptBox = Find<TextBox>(translationPage, "VisionPromptBox");
        Check(
            "Fonts.PromptEditorsFollowResource",
            promptBox?.FontFamily?.Name == "Consolas" && visionPromptBox?.FontFamily?.Name == "Consolas",
            $"prompt={promptBox?.FontFamily?.Name}, vision={visionPromptBox?.FontFamily?.Name}");

        // 4. 选区浮层的尺寸 chip 不再用 Typeface.Default
        Check(
            "Fonts.OverlayChipUsesUiFamily",
            SelectionOverlayWindow.ResolveChipTypeface().FontFamily.Name == "Consolas");

        // 5. 空串合法：删除资源键 = 平台默认，不回落任何内置链
        FontResources.Apply(new FontConfig());
        Check(
            "Fonts.EmptyWritesNoResource",
            !app.Resources.ContainsKey(FontResources.UiKey) && !app.Resources.ContainsKey(FontResources.PromptKey));

        // 6. 未知族名：点名警告但不阻止保存
        FontResources.WarmUpAsync().GetAwaiter().GetResult();
        var generalPage = new GeneralPage();
        generalPage.Load(new AppConfig { Fonts = new FontConfig { Ui = "NaniKore Missing Font 123", Prompt = "" } });
        var fontStatus = Find<InfoBar>(generalPage, "FontStatus");
        Check(
            "Fonts.UnknownFamilyWarnsButSaves",
            fontStatus is { IsOpen: true, Severity: InfoSeverity.Warning } &&
            fontStatus.Message.Contains("回退为系统字体", StringComparison.Ordinal) &&
            generalPage.Validate() is null,
            fontStatus?.Message ?? "missing");
        var savedConfig = new AppConfig();
        generalPage.Apply(savedConfig);
        Check("Fonts.UnknownFamilyIsPersisted", savedConfig.Fonts.Ui == "NaniKore Missing Font 123");

        // 7. 候选只来自本机字体表：非空、无重复、已排序、逐项可命中
        var families = FontResources.InstalledFamilies;
        Check(
            "Fonts.SuggestionsAreInstalled",
            families.Count > 0 &&
            families.Distinct(StringComparer.OrdinalIgnoreCase).Count() == families.Count &&
            families.SequenceEqual(families.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) &&
            families.All(FontResources.IsInstalled),
            $"count={families.Count}");

        // 8. 输入框即搜索框：Contains + 空前缀显示全部
        var filterBox = new AutoCompleteBox { FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0 };
        Check(
            "Fonts.FilterIsCaseInsensitiveSubstring",
            filterBox.MinimumPrefixLength == 0 &&
            filterBox.TextFilter?.Invoke("yahei", "Microsoft YaHei UI") == true &&
            filterBox.TextFilter?.Invoke("YAHEI", "Microsoft YaHei UI") == true &&
            filterBox.TextFilter?.Invoke("mono", "Microsoft YaHei UI") == false);

        // 9. 自渲染预览落在模板内 TextBox 上（不是 AutoCompleteBox 本身），空值回平台默认
        var uiPage = new GeneralPage();
        uiPage.Load(new AppConfig());
        _ = new Window { Content = uiPage.View, Width = 760, Height = 900 };
        uiPage.View.Measure(new Avalonia.Size(760, 900));
        var uiBox = Find<AutoCompleteBox>(uiPage, "UiFontBox");
        uiBox!.Text = "Consolas";
        var innerInput = FindDescendants<TextBox>(uiBox).FirstOrDefault();
        Check(
            "Fonts.InputSelfPreviews",
            innerInput is not null && innerInput.FontFamily?.Name == "Consolas",
            $"inner={(innerInput is null ? "null" : innerInput.FontFamily?.Name)}");
        uiBox.Text = string.Empty;
        Check(
            "Fonts.InputSelfPreviewFallsBackToPlatformDefault",
            innerInput is not null && innerInput.FontFamily?.Name == FontFamily.Default.Name);

        // 10. 候选列表钉回 Font.Ui：填了符号字体也不影响候选项可读
        FontResources.Apply(new FontConfig { Ui = "Consolas", Prompt = "Consolas" });
        var itemTemplate = uiBox.ItemTemplate;
        if (itemTemplate?.Build("Microsoft YaHei UI") is Control itemView)
        {
            _ = new Window { Content = itemView, Width = 320, Height = 40 };
            itemView.Measure(new Avalonia.Size(320, 40));
            var itemText = itemView as TextBlock ?? FindDescendants<TextBlock>(itemView).FirstOrDefault();
            Check(
                "Fonts.SuggestionItemsIgnorePreviewFont",
                itemText?.FontFamily?.Name == "Consolas",
                $"item={itemText?.FontFamily?.Name}");
        }
        else
        {
            Check("Fonts.SuggestionItemsIgnorePreviewFont", false, "itemTemplate is missing");
        }

        // 收尾：把资源清回空，后续用例不继承本次注入的字体
        FontResources.Apply(new FontConfig());
    }

    /// <summary>
    /// 关闭设置窗口必须「有变化才写盘」。
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

    /// <summary>保存按钮必须提交独立快照，后续 Apply 不能修改宿主持有的旧提交。</summary>
    private static void TestSettingsSaveSnapshots()
    {
        var initial = new AppConfig();
        var current = initial;
        var submissions = new List<AppConfig>();
        var settings = new SettingsWindow(() => current, next =>
        {
            submissions.Add(next);
            current = next;
        }, (_, _) => Task.FromResult("ok"));
        try
        {
            var page = (GeneralPage)Find<ContentControl>(settings, "PageHost")!.Content!;
            var idleBox = Find<AutoCompleteBox>(page, "LocalResourceIdleTimeoutBox")!;
            var rareOnly = Find<ToggleSwitch>(page, "PhoneticsRareOnlyToggle")!;
            var opacityBox = Find<NumericUpDown>(page, "PanelBackgroundOpacityBox")!;
            var save = Find<Button>(settings, "SaveButton")!;
            var status = Find<InfoBar>(settings, "FooterStatus")!;

            idleBox.Text = "30s";
            rareOnly.IsChecked = false;
            opacityBox.Value = 45;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("settings save button successfully submits the edited configuration",
                submissions.Count == 1 && status.Severity == InfoSeverity.Success &&
                current.General.LocalResourceIdleTimeout == "30s" &&
                !current.Phonetics.RareOnly && current.Windows.PanelBackgroundOpacity == 0.45);
            Check("settings save leaves the configuration supplied at open unchanged",
                !ReferenceEquals(current, initial) &&
                initial.General.LocalResourceIdleTimeout == "0" && initial.Phonetics.RareOnly);

            var first = current;
            idleBox.Text = "10m";
            rareOnly.IsChecked = true;
            opacityBox.Value = 75;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("second settings save submits a separate snapshot with the new edits",
                submissions.Count == 2 && status.Severity == InfoSeverity.Success &&
                !ReferenceEquals(first, current) &&
                !ReferenceEquals(first.General, current.General) &&
                !ReferenceEquals(first.Phonetics, current.Phonetics) &&
                !ReferenceEquals(first.Windows, current.Windows) &&
                current.General.LocalResourceIdleTimeout == "10m" &&
                current.Phonetics.RareOnly && current.Windows.PanelBackgroundOpacity == 0.75);
            // 第二次保存会再次 Apply 到工作副本；首份提交保持原值才能证明提交不暴露工作对象。
            Check("second settings save does not mutate the previously committed snapshot",
                first.General.LocalResourceIdleTimeout == "30s" &&
                !first.Phonetics.RareOnly && first.Windows.PanelBackgroundOpacity == 0.45);
        }
        finally
        {
            settings.Close();
        }
    }

    /// <summary>真实关闭事件仅回写尺寸，始终保留最新提交及外部更新，不夹带被拒绝的编辑。</summary>
    private static void TestSettingsCloseSizeWriteBackKeepsExternalUpdates()
    {
        foreach (var scenario in new[] { "unsaved", "saved", "validation rejected", "host rejected" })
        {
            var directory = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            SettingsWindow? settings = null;
            try
            {
                var store = new ConfigStore(directory);
                var initial = new AppConfig();
                initial.Windows.SettingsWidth = 800;
                initial.Windows.SettingsHeight = 640;
                store.AtomicWrite(initial);
                var rejectSave = false;
                var saveAttempts = 0;
                settings = new SettingsWindow(() => store.Current, next =>
                {
                    saveAttempts++;
                    if (rejectSave)
                    {
                        throw new InvalidOperationException("self-test host rejected settings");
                    }
                    store.AtomicWrite(next);
                }, (_, _) => Task.FromResult("ok"));
                var page = (GeneralPage)Find<ContentControl>(settings, "PageHost")!.Content!;
                var idleBox = Find<AutoCompleteBox>(page, "LocalResourceIdleTimeoutBox")!;
                var save = Find<Button>(settings, "SaveButton")!;
                var status = Find<InfoBar>(settings, "FooterStatus")!;

                if (scenario != "unsaved")
                {
                    idleBox.Text = "30s";
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check($"{scenario}: initial settings save succeeds",
                        saveAttempts == 1 && status.Severity == InfoSeverity.Success &&
                        store.Current.General.LocalResourceIdleTimeout == "30s");
                }

                idleBox.Text = scenario == "validation rejected" ? "invalid duration" : "10m";
                if (scenario == "validation rejected")
                {
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("invalid settings edits are rejected before reaching the host",
                        saveAttempts == 1 && status.Severity == InfoSeverity.Warning);
                }
                else if (scenario == "host rejected")
                {
                    rejectSave = true;
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    rejectSave = false;
                    Check("host rejection leaves the last committed settings unchanged",
                        saveAttempts == 2 && status.Severity == InfoSeverity.Error &&
                        store.Current.General.LocalResourceIdleTimeout == "30s");
                }

                var external = AppConfigCloner.Clone(store.Current);
                external.General.AutoExplain = true;
                external.General.TargetLanguage = "日本語";
                store.AtomicWrite(external);
                var baseline = store.Current;
                var attemptsBeforeClose = saveAttempts;
                // 无头窗口通过真实布局产生 Bounds，再走取消按钮触发 Closed 的尺寸回写。
                settings.Width = 1024;
                settings.Height = 700;
                settings.Measure(new Size(1024, 700));
                settings.Arrange(new Rect(0, 0, 1024, 700));
                // 平台可能限制客户区尺寸，验证实际 Bounds 的写回，不假设请求尺寸必被原样接纳。
                var resized = settings.Bounds.Size;
                Check($"{scenario}: settings close test lays out the resized window",
                    SettingsWindow.ResolvePersistedSize(baseline.Windows, resized.Width, resized.Height,
                        settings.MinWidth, settings.MinHeight) is not null,
                    $"actual={resized}");
                Find<Button>(settings, "CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var reloaded = new ConfigStore(directory).Current;
                Check($"{scenario}: resized settings close writes exactly once",
                    saveAttempts == attemptsBeforeClose + 1);
                Check($"{scenario}: external updates survive settings close",
                    reloaded.General.AutoExplain && reloaded.General.TargetLanguage == "日本語");
                Check($"{scenario}: settings close persists the resized dimensions",
                    reloaded.Windows.SettingsWidth == resized.Width && reloaded.Windows.SettingsHeight == resized.Height);
                Check($"{scenario}: settings close discards uncommitted page edits",
                    reloaded.General.LocalResourceIdleTimeout ==
                    (scenario == "unsaved" ? initial.General.LocalResourceIdleTimeout : "30s"));
                Check($"{scenario}: size write-back clones the latest committed baseline",
                    !ReferenceEquals(store.Current, baseline) &&
                    baseline.Windows.SettingsWidth == 800 && baseline.Windows.SettingsHeight == 640);
            }
            finally
            {
                settings?.Close();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 收藏管理页：元数据、筛选/搜索、事件回流刷新与配置契约（Validate 恒空、Apply 无副作用）。
    /// </summary>
    private static void TestFavoritesSettingsPageFilteringAndEvents()
    {
        EnsureThemedApplication();

        var databasePath = Path.Combine(Path.GetTempPath(), $"nanikore-ui-selftest-{Guid.NewGuid():N}.db");
        try
        {
            var service = new SqliteFavoriteService(databasePath);
            var page = new FavoritesPage(service);
            var hostWindow = new Window { Content = page, Width = 800, Height = 600 };

            Check("favorites page exposes title and star glyph", page.Title == "收藏" && page.Glyph == "\uE734");
            Check("favorites page shows the live database path", page.DatabasePathText.Text == databasePath);
            Check("favorites page exposes database path copy button", page.CopyPathButton is not null);
            Check("favorites page validate never blocks saving", page.Validate() is null);

            Check(
                "favorites page uses flat nav bar without settings groups",
                page.LanguageBox.SelectedItem as string == "全部" &&
                FindDescendants<SettingsGroup>(page).Distinct().Count() == 0);

            page.Load(new AppConfig());

            service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "通る", Note = "とおる" }).Wait();
            service.AddFavoriteAsync(new FavoriteItem { Category = "语法", Content = "～ない" }).Wait();
            service.AddFavoriteAsync(new FavoriteItem { Category = "短语", Content = "過ぎる" }).Wait();

            var itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page refreshes on favorite added event", itemsList.Count == 3);
            Check("favorites page count text lists total", page.CountText.Text == "共 3 条收藏");

            page.CategoryBox.SelectedItem = "语法";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page filters by category", itemsList.Count == 1);
            Check("favorites page count text reports filtered hits", page.CountText.Text == "筛选命中 1 / 3 条");

            page.CategoryBox.SelectedItem = "全部";
            page.SearchBox.Text = "とおる";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page searches note content", itemsList.Count == 1);

            // 备注不再存字段前缀：搜「读音」「释义」这类提示词不得再命中任何条目。
            page.SearchBox.Text = "读音";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page ignores removed note prefixes", itemsList.Count == 0);

            page.SearchBox.Text = "不存在的词";
            Check("favorites page shows empty hint when nothing matches", page.EmptyHint.IsVisible);

            page.SearchBox.Text = string.Empty;
            service.RemoveFavoriteAsync("～ない").Wait();
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page refreshes on favorite removed event", itemsList.Count == 2);

            service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "run", Language = "English" }).Wait();
            page.LanguageBox.SelectedItem = "English";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page filters by language", itemsList.Count == 1);
            page.LanguageBox.SelectedItem = "日本語";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page language filter keeps japanese rows", itemsList.Count == 2);
            page.LanguageBox.SelectedItem = "全部";
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check("favorites page language filter restores all rows", itemsList.Count == 3);

            var editTarget = service.GetAllFavorites().First(x => x.Content == "通る");
            page.BeginEdit(editTarget);
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check(
                "favorites page enters inline edit mode",
                itemsList.OfType<FavoriteEditViewModel>().Count() == 1);

            var saved = page.SaveEditAsync(editTarget, "通[とお]る", "とおる\n修订", "短语", "日本語")
                .GetAwaiter().GetResult();
            Check("favorites page inline edit saves successfully", saved);
            var stored = service.GetAllFavorites().Single(x => x.Id == editTarget.Id);
            Check(
                "favorites page inline edit persists normalized fields",
                stored.Content == "通る" && stored.Category == "短语" && stored.Note!.Contains("修订"));
            itemsList = page.FavoriteList.ItemsSource?.Cast<object>().ToList() ?? [];
            Check(
                "favorites page inline edit restores display mode",
                itemsList.OfType<FavoriteDisplayViewModel>()
                    .Any(vm => vm.Item.Content == "通る" && vm.Item.Category == "短语"));

            // 编辑态下拉框：展开期间必须被判定为“仍在编辑”，否则失焦提交会刷新列表、拆掉展开的下拉。
            page.BeginEdit(service.GetAllFavorites().First(x => x.Id == editTarget.Id));
            page.Measure(new Size(900, 900));
            page.Arrange(new Rect(0, 0, 900, 900));
            var editCard = FindDescendants<Border>(page)
                .FirstOrDefault(border => border.DataContext is FavoriteEditViewModel);
            var editBoxes = editCard is null
                ? []
                : FindDescendants<ComboBox>(editCard).Distinct().ToList();
            Check("favorites page inline edit hosts category and language drop-downs", editBoxes.Count == 2);
            if (editCard is not null && editBoxes.Count == 2)
            {
                Check("favorites page inline edit is not treated as dropdown-open by default",
                    !FavoritesPage.IsEditDropDownOpen(editCard));
                editBoxes[0].IsDropDownOpen = true;
                Check("favorites page inline edit detects an open drop-down",
                    FavoritesPage.IsEditDropDownOpen(editCard));

                // 下拉展开期间模拟失焦：不得提交/刷新，否则会拆掉正在展开的下拉。
                var settle = page.CommitEditWhenFocusLeavesAsync(editCard);
                PumpUntil(settle, TimeSpan.FromMilliseconds(200));
                Check("favorites page keeps inline edit alive while a drop-down is open",
                    page.FavoriteList.ItemsSource?.Cast<object>()
                        .OfType<FavoriteEditViewModel>().Count() == 1);

                editBoxes[0].IsDropDownOpen = false;
                Check("favorites page inline edit clears the drop-down state after closing",
                    !FavoritesPage.IsEditDropDownOpen(editCard));

                // 下拉收起且焦点已离开：失焦提交才落地。
                PumpUntil(settle, TimeSpan.FromSeconds(2));
                Check("favorites page commits inline edit once the drop-down is closed and focus left",
                    page.FavoriteList.ItemsSource?.Cast<object>()
                        .OfType<FavoriteEditViewModel>().Count() == 0);
            }

            // 点击编辑卡片以外的区域必须立即收起：仅靠失焦判断会漏掉点其他收藏条目（非可聚焦控件）的情况。
            page.BeginEdit(service.GetAllFavorites().First(x => x.Id == editTarget.Id));
            page.Measure(new Size(900, 900));
            page.Arrange(new Rect(0, 0, 900, 900));
            Dispatcher.UIThread.RunJobs();
            var pressCard = RealizeEditCard(page);
            Check("favorites page pointer test has an active edit card", pressCard is not null);
            if (pressCard is not null)
            {
                var pointer = new Pointer(0, PointerType.Mouse, true);
                page.OnPagePointerPressedForTest(NewPointerArgs(pressCard, pressCard, pointer));
                Dispatcher.UIThread.RunJobs();
                Check("favorites page keeps inline edit when pressing the card itself",
                    page.FavoriteList.ItemsSource?.Cast<object>()
                        .OfType<FavoriteEditViewModel>().Count() == 1);

                page.OnPagePointerPressedForTest(NewPointerArgs(page, page, pointer));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (page.FavoriteList.ItemsSource?.Cast<object>()
                           .OfType<FavoriteEditViewModel>().Any() == true && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    System.Threading.Thread.Sleep(5);
                }

                Check("favorites page commits inline edit when pressing outside the card",
                    page.FavoriteList.ItemsSource?.Cast<object>()
                        .OfType<FavoriteEditViewModel>().Count() == 0);
            }

            var collision = service.GetAllFavorites().First(x => x.Content == "過ぎる");
            var rejected = page.SaveEditAsync(collision, "通る (とおる)", null, "单词", "日本語")
                .GetAwaiter().GetResult();
            Check("favorites page inline edit rejects normalized conflict", !rejected);
            Check(
                "favorites page inline edit keeps original on conflict",
                service.GetAllFavorites().Any(x => x.Id == collision.Id && x.Content == "過ぎる"));

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
            }
        }
    }

    /// <summary>
    /// 窗口行为与层级管理：设置窗口显示在任务栏且默认不置顶。
    /// </summary>
    private static void TestWindowProperties()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        Check("result panel window is not topmost", !panel.Topmost);

        SettingsTab? requestedTab = null;
        panel.SettingsRequested += (_, tab) => requestedTab = tab;
        var settingsButton = FindDescendants<Button>(panel)
            .FirstOrDefault(b => b.Content as string == "设置");
        if (settingsButton is not null)
        {
            RaiseClick(settingsButton);
        }
        Check("result panel settings button requests opening translation tab", requestedTab == SettingsTab.Translation);

        var settings = NewSettingsWindow();
        Check("settings window shows in taskbar", settings.ShowInTaskbar);
        Check("settings window is not topmost", !settings.Topmost);

        var settingsTranslation = NewSettingsWindow(SettingsTab.Translation);
        var navList = Find<ListBox>(settingsTranslation, "NavigationList");
        var pageTitle = Find<TextBlock>(settingsTranslation, "PageTitle");
        Check(
            "settings window opens directly to translation tab when requested",
            navList?.SelectedIndex == (int)SettingsTab.Translation && pageTitle?.Text == "翻译");
        settingsTranslation.SelectTab(SettingsTab.General);
        Check(
            "settings window selects general tab on SelectTab",
            navList?.SelectedIndex == (int)SettingsTab.General && pageTitle?.Text == "通用");
    }

    private static SettingsWindow NewSettingsWindow(SettingsTab? initialTab = null) =>
        initialTab is null
            ? new SettingsWindow(() => new AppConfig(), _ => { }, (_, _) => Task.FromResult("ok"))
            : new SettingsWindow(() => new AppConfig(), _ => { }, (_, _) => Task.FromResult("ok"), initialTab: initialTab.Value);

    /// <summary>
    /// 取（并强制实例化）列表里当前编辑态的卡片 Border：
    /// 无头环境不跑完整布局流程，ItemsControl 容器可能尚未套用模板，直接查可视树会落空。
    /// </summary>
    private static Border? RealizeEditCard(FavoritesPage page)
    {
        var editVm = page.FavoriteList.ItemsSource?.Cast<object>().OfType<FavoriteEditViewModel>().FirstOrDefault();
        if (editVm is null || page.FavoriteList.ContainerFromItem(editVm) is not Control container)
        {
            return null;
        }

        container.ApplyTemplate();
        return container.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.DataContext is FavoriteEditViewModel);
    }

    /// <summary>构造一个左键按下的指针事件，来源由测试指定。</summary>
    private static PointerPressedEventArgs NewPointerArgs(Visual source, Visual root, Pointer pointer) =>
        new(
            source,
            pointer,
            root,
            new Point(5, 5),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        };

    /// <summary>无头环境下抽干 Dispatcher 队列，直到任务完成或超时（异步失焦提交测试用）。</summary>
    private static void PumpUntil(Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> FindDescendants<T>(Control root) where T : class
    {
        if (root is T self)
        {
            yield return self;
        }

        foreach (var child in root.GetVisualDescendants().OfType<T>())
        {
            yield return child;
        }

        foreach (var child in root.GetLogicalDescendants().OfType<T>())
        {
            yield return child;
        }

        if (root is ContentControl cc && cc.Content is Control ccChild)
        {
            foreach (var item in FindDescendants<T>(ccChild))
            {
                yield return item;
            }
        }
        else if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                foreach (var item in FindDescendants<T>(child))
                {
                    yield return item;
                }
            }
        }
        else if (root is HeaderedContentControl hcc && hcc.Content is Control hccChild)
        {
            foreach (var item in FindDescendants<T>(hccChild))
            {
                yield return item;
            }
        }
        else if (root is Border border && border.Child is Control borderChild)
        {
            foreach (var item in FindDescendants<T>(borderChild))
            {
                yield return item;
            }
        }
    }
}
