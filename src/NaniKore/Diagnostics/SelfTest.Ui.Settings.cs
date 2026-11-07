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
using Avalonia.Threading;
using Avalonia.VisualTree;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Ocr.Models;
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
        TestGeneralPagePipelineDependencyVisibility();
        TestGeneralPagePhoneticsCards();
        TestSharedLanguageChoicesAndDetectionMapping();
        TestTranslationPagePromptEditing();
        TestAboutPageRepositoryEntry();
        TestSettingsCloseSizeMemory();
        TestSettingsCloseSizeWriteBackKeepsExternalUpdates();
        TestFavoritesSettingsPageFilteringAndEvents();
        TestWindowProperties();
    }

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
    }

    /// <summary>
    /// 通用页流水线依赖可见性：本地 OCR 依赖组只在 local_ocr 模式露出；
    /// 依赖按钮文案直接映射本地清单状态，无下载入口时禁用；AutoExplain 读写闭环。
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

        var autoExplainPage = new GeneralPage();
        var autoExplainConfig = new AppConfig { General = new GeneralConfig { AutoExplain = true } };
        autoExplainPage.Load(autoExplainConfig);
        var autoExplainToggle = Find<ToggleSwitch>(autoExplainPage, "AutoExplainToggle");
        Check(
            "general page loads and applies AutoExplain setting accurately",
            autoExplainToggle?.IsChecked == true);

        autoExplainToggle!.IsChecked = false;
        var appliedConfig = new AppConfig();
        autoExplainPage.Apply(appliedConfig);
        Check(
            "general page applies AutoExplain toggle change to configuration",
            !appliedConfig.General.AutoExplain);

        var pasteAutoTranslatePage = new GeneralPage();
        pasteAutoTranslatePage.Load(new AppConfig { General = new GeneralConfig { PasteAutoTranslate = false } });
        var pasteAutoTranslateToggle = Find<ToggleSwitch>(pasteAutoTranslatePage, "PasteAutoTranslateToggle");
        Check(
            "general page loads the paste selection auto-translate switch accurately",
            pasteAutoTranslateToggle?.IsChecked == false);

        pasteAutoTranslateToggle!.IsChecked = true;
        var pasteAppliedConfig = new AppConfig();
        pasteAutoTranslatePage.Apply(pasteAppliedConfig);
        Check(
            "general page applies the paste selection auto-translate switch to configuration",
            pasteAppliedConfig.General.PasteAutoTranslate);

        var targetBox = Find<ComboBox>(page, "TargetLanguageBox");
        var targetItems = targetBox?.ItemsSource?.Cast<LanguageChoice>().ToList() ?? [];
        Check(
            "general page target languages reference the shared native-name list",
            targetBox is not null &&
            targetItems.Count == LanguageChoices.All.Count &&
            ReferenceEquals(targetItems[3], LanguageChoices.All[3]) &&
            LanguageChoices.All[3].Name == "日本語");

        page.Load(new AppConfig { General = new GeneralConfig { TargetLanguage = "日语" } });
        Check(
            "general page resolves legacy chinese alias to native display name",
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

    /// <summary>
    /// 设置窗口未保存、仅拖过尺寸就关闭时，尺寸记忆必须叠加在「实时当前配置」上。
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

            AppConfig LatestConfig() => store.Current;
            var openTimeSnapshot = AppConfigCloner.Clone(LatestConfig());
            var working = AppConfigCloner.Clone(LatestConfig());

            working.General.TargetLanguage = "未保存的页面编辑";

            var external = LatestConfig();
            external.General.AutoExplain = true;
            store.AtomicWrite(external);

            var baseline = SettingsWindow.ResolveCloseWriteBackBaseline(settingsSaved: false, working, LatestConfig());
            Check(
                "unsaved settings close picks the live config instead of the stale open-time snapshot",
                !ReferenceEquals(baseline, openTimeSnapshot) &&
                ReferenceEquals(baseline, store.Current) &&
                baseline.General.AutoExplain);

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

            Check(
                "saved settings close keeps using the working copy as write-back baseline",
                ReferenceEquals(SettingsWindow.ResolveCloseWriteBackBaseline(settingsSaved: true, working, store.Current), working));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
