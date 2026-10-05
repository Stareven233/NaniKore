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
    /// <summary>主题资源与控件模板必须可用；顺带验证两份色板与两份控件字典都能解析。</summary>
    private static void TestThemeResourcesResolve()
    {
        EnsureThemedApplication();
        var application = Wpf.Application.Current!;
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
            application.TryFindResource(typeof(SettingsRow)) is Wpf.Style &&
            application.TryFindResource(typeof(SettingsGroup)) is Wpf.Style &&
            application.TryFindResource(typeof(NumberBox)) is Wpf.Style &&
            application.TryFindResource(typeof(InfoBar)) is Wpf.Style &&
            application.TryFindResource(typeof(HotkeyRecorderBox)) is Wpf.Style);
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
            view.Measure(new Wpf.Size(760, double.PositiveInfinity));
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
            page.FindName("LocalOcrDependenciesGroup") is WpfControls.Control { Visibility: Wpf.Visibility.Collapsed });

        page.Load(new AppConfig { Pipeline = new PipelineConfig { Mode = "local_ocr" } });
        Check(
            "general page shows local OCR dependencies for local pipeline",
            page.FindName("LocalOcrDependenciesGroup") is WpfControls.Control { Visibility: Wpf.Visibility.Visible });

        // OCR 依赖卡片：状态直接映射为按钮文案（下载 / 已就绪），初始不单独显示提示行
        var ocrButton = Find<WpfControls.Button>(page, "DownloadDependenciesButton");
        var ocrStatus = Find<InfoBar>(page, "DependencyStatus");
        var isOcrComplete = OcrDependencyManifest.IsComplete();
        Check(
            "ocr dependency button mirrors local manifest state and stays disabled without a downloader",
            ocrButton?.Content as string == GeneralPage.OcrButtonLabel(isOcrComplete) &&
            ocrButton?.IsEnabled == false);
        Check(
            "ocr dependency status bar does not display separate readiness hint on open",
            ocrStatus?.IsOpen == false);

        var pageWithOcrDownloader = new GeneralPage(downloadDependencies: (_, _) => Task.CompletedTask);
        var enabledOcrButton = Find<WpfControls.Button>(pageWithOcrDownloader, "DownloadDependenciesButton");
        Check(
            "ocr dependency button enables once a downloader is injected",
            enabledOcrButton?.IsEnabled == true);

        var autoExplainPage = new GeneralPage();
        var autoExplainConfig = new AppConfig { General = new GeneralConfig { AutoExplain = true } };
        autoExplainPage.Load(autoExplainConfig);
        var autoExplainToggle = Find<WpfPrimitives.ToggleButton>(autoExplainPage, "AutoExplainToggle");
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
        var targetBox = Find<WpfControls.ComboBox>(page, "TargetLanguageBox");
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
            page.FindName("PhoneticsEnabledToggle") is WpfPrimitives.ToggleButton &&
            page.FindName("PhoneticsRareOnlyToggle") is WpfPrimitives.ToggleButton);

        foreach (var language in PhoneticLanguages.All)
        {
            var buttonName = $"PhoneticsDownload{char.ToUpperInvariant(language.Code[0])}{language.Code[1..]}";
            var button = Find<WpfControls.Button>(page, buttonName);
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
        var jaButton = Find<WpfControls.Button>(withDownloader, "PhoneticsDownloadJa");
        Check(
            "dictionary card button enables once a downloader is injected",
            jaButton?.IsEnabled == true);

        // 注音开关读写闭环
        var config = new AppConfig { Phonetics = new PhoneticsConfig { Enabled = false, RareOnly = false } };
        page.Load(config);
        var enabledToggle = Find<WpfPrimitives.ToggleButton>(page, "PhoneticsEnabledToggle");
        var rareOnlyToggle = Find<WpfPrimitives.ToggleButton>(page, "PhoneticsRareOnlyToggle");
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

    /// <summary>
    /// 翻译设置页提示词编辑：语言注音插件编辑器已随插件键退役（issue 008 Phase 3），
    /// 页面只剩 Base 与视觉 Base 两行；恢复默认按钮与标题行共享；
    /// 旧配置里退役插件键的自定义值不得被清空（数据留存，仅不参与组装）。
    /// </summary>
    private static void TestTranslationPagePromptEditing()
    {
        var config = new AppConfig();
        var page = new TranslationPage();
        page.Load(config);

        var localPromptBox = Find<WpfControls.TextBox>(page, "PromptBox");
        var visionPromptBox = Find<WpfControls.TextBox>(page, "VisionPromptBox");
        var localPromptRow = Find<SettingsRow>(page, "LocalPromptRow");
        var visionPromptRow = Find<SettingsRow>(page, "VisionPromptRow");
        var restoreLocalButton = Find<WpfControls.Button>(page, "RestorePromptButton");
        var restoreVisionButton = Find<WpfControls.Button>(page, "RestoreVisionPromptButton");

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
            restoreLocalButton!.Foreground is WpfMedia.SolidColorBrush { Color: var localColor } &&
            localColor == WpfMedia.Colors.Black &&
            restoreVisionButton!.Foreground is WpfMedia.SolidColorBrush { Color: var visionColor } &&
            visionColor == WpfMedia.Colors.Black);

        // 视觉 Base 一键恢复默认
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

        // 旧配置里退役插件键的自定义值不得被设置页清空（数据留存，仅不参与组装）
        page.Load(legacyPluginConfig);
        page.Apply(legacyPluginConfig);
        Check(
            "retired plugin key values survive the settings page untouched",
            legacyPluginConfig.Prompts.JapanesePlugin == "Legacy ja-JP" &&
            legacyPluginConfig.Prompts.EnglishPlugin == "Legacy en-US" &&
            legacyPluginConfig.Prompts.ChinesePlugin == "Legacy zh-CN");
    }

    /// <summary>
    /// 回归测试：关闭设置窗口必须「有变化才写盘」。
    /// 曾经无条件回写，导致「什么都不改、直接关闭」也会重写 config.yaml。
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
    /// 回归测试：设置窗口未保存、仅拖过尺寸就关闭时，尺寸记忆必须叠加在「实时当前配置」上。
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
                FindDescendants<SettingsGroup>(page).Count() == 0);

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
            Check("favorites page shows empty hint when nothing matches", page.EmptyHint.Visibility == Wpf.Visibility.Visible);

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

    /// <summary>
    /// 窗口行为与层级管理：设置窗口显示在任务栏且默认不置顶，最小化时进入任务栏；
    /// 结果面板恢复为非置顶窗口，避免强抢层级遮挡其他程序。
    /// </summary>
    private static void TestWindowProperties()
    {
        EnsureThemedApplication();
        var panel = new ResultPanelWindow();
        Check("result panel window is not topmost", !panel.Topmost);

        SettingsTab? requestedTab = null;
        panel.SettingsRequested += (_, tab) => requestedTab = tab;
        var settingsButton = FindDescendants<WpfControls.Button>(panel)
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
        var navList = Find<WpfControls.ListBox>(settingsTranslation, "NavigationList");
        var pageTitle = Find<WpfControls.TextBlock>(settingsTranslation, "PageTitle");
        Check(
            "settings window opens directly to translation tab when requested",
            navList?.SelectedIndex == (int)SettingsTab.Translation && pageTitle?.Text == "翻译");
        settingsTranslation.SelectTab(SettingsTab.General);
        Check(
            "settings window selects general tab on SelectTab",
            navList?.SelectedIndex == (int)SettingsTab.General && pageTitle?.Text == "通用");
    }

    /// <summary>构造一个不落盘的设置窗口（自测注入空保存器与恒通探活，可选直达页签）。</summary>
    private static SettingsWindow NewSettingsWindow(SettingsTab? initialTab = null) =>
        initialTab is null
            ? new SettingsWindow(() => new AppConfig(), _ => { }, (_, _) => Task.FromResult("ok"))
            : new SettingsWindow(() => new AppConfig(), _ => { }, (_, _) => Task.FromResult("ok"), initialTab: initialTab.Value);
}
