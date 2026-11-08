using System.Text;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Input;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    /// <summary>
    /// 结果面板尺寸记忆（issue：缩放不记忆）：result_panel_width/height 与设置窗尺寸同范式落盘、跨重启恢复；
    /// 越界或非法值由归一化回退面板默认（700×520），下限对齐面板 MinWidth/MinHeight，克隆不丢字段。
    /// </summary>
    private static void TestResultPanelSizeConfigRoundTrip()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            // 合法值：读盘恢复 + AtomicWrite 往返不丢、克隆保留
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.1.1
                windows:
                  result_panel_width: 960
                  result_panel_height: 640
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "result panel size loads from config",
                store.Current.Windows.ResultPanelWidth == 960 &&
                store.Current.Windows.ResultPanelHeight == 640);
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "result panel size survives an atomic write round-trip and clone",
                reloaded.Current.Windows.ResultPanelWidth == 960 &&
                reloaded.Current.Windows.ResultPanelHeight == 640 &&
                AppConfigCloner.Clone(reloaded.Current).Windows.ResultPanelWidth == 960 &&
                AppConfigCloner.Clone(reloaded.Current).Windows.ResultPanelHeight == 640);

            // 越界值：宽低于面板 MinWidth(440)、高超过上限(2160) → 归一化回退默认 700×520
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.1.1
                windows:
                  result_panel_width: 100
                  result_panel_height: 99999
                """);
            var clamped = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "out-of-range result panel size normalizes back to the panel defaults",
                clamped.Current.Windows.ResultPanelWidth == 960 &&
                clamped.Current.Windows.ResultPanelHeight == 660);
        });
    }

    private static void RunConfigTests()
    {
        TestConfigRoundTrip();
        TestResultPanelSizeConfigRoundTrip();
        TestSharedBackgroundConfigRoundTrip();
        TestSharedBackgroundConfigNormalization();
        TestEmptyPromptKeysSurviveRewrite();
        TestPasteHotkeyNormalization();
        TestLegacyVisionOneshotKeyIsIgnored();
        TestPhoneticsConfigDefaultsAndRoundTrip();
        TestProfileDocumentOmitsDefaults();
        TestProfileWithOnlyNameGetsDefaults();
        TestConfigSeedsFromTemplateByteForByte();
        TestDataDirectoryWritabilityRefusal();
        TestSecretStoreKeysBinEnvelope();
        TestSecretStoreIgnoresLegacyLayout();
        TestSecretRenameMovesProfileKey();
        TestConfigVersionComparator();
        // TestAboutPageDisplaysTemplateAndConfigVersions(); // 待 P4 关于页迁移至 Avalonia 后接回
        TestFavoritesDefaultDatabasePath();
        // TestApplicationIconResource(); // 待 P2 平台服务迁移至 Avalonia 后接回
    }

    /// <summary>共用背景配置：缺省、路径修整、独立克隆与写盘往返均保留约定。</summary>
    private static void TestSharedBackgroundConfigRoundTrip()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            var configPath = Path.Combine(dataDirectory, "config.yaml");
            File.WriteAllText(configPath, "windows: {}\n");
            var defaults = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "missing background fields use empty image, zero blur and full image/panel opacity",
                defaults.Current.Windows.BackgroundImage == string.Empty &&
                defaults.Current.Windows.BackgroundBlur == 0 &&
                defaults.Current.Windows.BackgroundOpacity == 1 &&
                defaults.Current.Windows.PanelBackgroundOpacity == 1 &&
                defaults.LoadWarning is null);

            File.WriteAllText(configPath, """
                windows:
                  background_image: '  images/背景 image.png  '
                  background_blur: 24.5
                  background_opacity: 0.35
                  panel_background_opacity: 0.45
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            var windows = store.Current.Windows;
            Check(
                "background fields load with a trimmed path and fractional values",
                windows.BackgroundImage == "images/背景 image.png" &&
                windows.BackgroundBlur == 24.5 && windows.BackgroundOpacity == 0.35 &&
                windows.PanelBackgroundOpacity == 0.45 &&
                store.LoadWarning is null);

            var clone = AppConfigCloner.Clone(store.Current);
            Check(
                "background clone preserves all fields in a separate window config",
                !ReferenceEquals(clone.Windows, windows) &&
                clone.Windows.BackgroundImage == windows.BackgroundImage &&
                clone.Windows.BackgroundBlur == windows.BackgroundBlur &&
                clone.Windows.BackgroundOpacity == windows.BackgroundOpacity &&
                clone.Windows.PanelBackgroundOpacity == windows.PanelBackgroundOpacity);
            clone.Windows.BackgroundImage = "  other/image.png  ";
            clone.Windows.BackgroundBlur = 75.25;
            clone.Windows.BackgroundOpacity = 0.625;
            clone.Windows.PanelBackgroundOpacity = 0.75;
            Check(
                "editing cloned background fields leaves the original config intact",
                windows.BackgroundImage == "images/背景 image.png" &&
                windows.BackgroundBlur == 24.5 && windows.BackgroundOpacity == 0.35 &&
                windows.PanelBackgroundOpacity == 0.45);
            store.AtomicWrite(clone);
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            var yaml = File.ReadAllText(configPath);
            Check(
                "background fields persist under snake-case keys and normalize on write",
                reloaded.Current.Windows.BackgroundImage == "other/image.png" &&
                reloaded.Current.Windows.BackgroundBlur == 75.25 &&
                reloaded.Current.Windows.BackgroundOpacity == 0.625 &&
                reloaded.Current.Windows.PanelBackgroundOpacity == 0.75 &&
                reloaded.LoadWarning is null &&
                yaml.Contains("background_image:", StringComparison.Ordinal) &&
                yaml.Contains("background_blur:", StringComparison.Ordinal) &&
                yaml.Contains("background_opacity:", StringComparison.Ordinal) &&
                yaml.Contains("panel_background_opacity:", StringComparison.Ordinal));
        });
    }

    /// <summary>空路径与数值边界可往返；读写两端的越界、NaN 和无穷值都恢复默认。</summary>
    private static void TestSharedBackgroundConfigNormalization()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            var configPath = Path.Combine(dataDirectory, "config.yaml");
            foreach (var image in new[] { "~", "''", "'   '" })
            {
                File.WriteAllText(configPath, $"windows:\n  background_image: {image}\n");
                var store = new ConfigStore(dataDirectory, templateDirectory);
                store.AtomicWrite(store.Current);
                var reloaded = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    $"background image {image} normalizes to a persisted empty string",
                    store.Current.Windows.BackgroundImage == string.Empty &&
                    reloaded.Current.Windows.BackgroundImage == string.Empty &&
                    File.ReadAllText(configPath).Contains("background_image: ''", StringComparison.Ordinal));
            }

            var writable = new ConfigStore(dataDirectory, templateDirectory);
            foreach (var (blur, opacity) in new[] { (0d, 0d), (100d, 1d) })
            {
                writable.Current.Windows.BackgroundBlur = blur;
                writable.Current.Windows.BackgroundOpacity = opacity;
                writable.Current.Windows.PanelBackgroundOpacity = opacity;
                writable.AtomicWrite(writable.Current);
                var reloaded = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    $"background range endpoints {blur}/{opacity} survive persistence",
                    reloaded.Current.Windows.BackgroundBlur == blur &&
                    reloaded.Current.Windows.BackgroundOpacity == opacity &&
                    reloaded.Current.Windows.PanelBackgroundOpacity == opacity &&
                    reloaded.LoadWarning is null);
            }

            // YAML 与内存写入分别覆盖，避免只验证读盘或只验证保存的一侧。
            foreach (var (yamlBlur, yamlOpacity, blur, opacity) in new[]
            {
                ("-0.1", "-0.1", -0.1, -0.1),
                ("100.1", "1.1", 100.1, 1.1),
                (".nan", ".nan", double.NaN, double.NaN),
                (".inf", ".inf", double.PositiveInfinity, double.PositiveInfinity),
                ("-.inf", "-.inf", double.NegativeInfinity, double.NegativeInfinity),
            })
            {
                File.WriteAllText(configPath, $"""
                    windows:
                      background_image: keep.png
                      background_blur: {yamlBlur}
                      background_opacity: {yamlOpacity}
                      panel_background_opacity: {yamlOpacity}
                    """);
                var loaded = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    $"invalid YAML background values {yamlBlur}/{yamlOpacity} reset without losing other fields",
                    loaded.Current.Windows.BackgroundImage == "keep.png" &&
                    loaded.Current.Windows.BackgroundBlur == 0 &&
                    loaded.Current.Windows.BackgroundOpacity == 1 &&
                    loaded.Current.Windows.PanelBackgroundOpacity == 1 &&
                    loaded.LoadWarning == "config.yaml 中的非法配置已回落到默认值。");

                loaded.Current.Windows.BackgroundImage = null!;
                loaded.Current.Windows.BackgroundBlur = blur;
                loaded.Current.Windows.BackgroundOpacity = opacity;
                loaded.Current.Windows.PanelBackgroundOpacity = opacity;
                loaded.AtomicWrite(loaded.Current);
                var reloaded = new ConfigStore(dataDirectory, templateDirectory);
                Check(
                    $"invalid in-memory background values {yamlBlur}/{yamlOpacity} persist as defaults",
                    loaded.Current.Windows.BackgroundImage == string.Empty &&
                    loaded.Current.Windows.BackgroundBlur == 0 &&
                    loaded.Current.Windows.BackgroundOpacity == 1 &&
                    loaded.Current.Windows.PanelBackgroundOpacity == 1 &&
                    reloaded.Current.Windows.BackgroundImage == string.Empty &&
                    reloaded.Current.Windows.BackgroundBlur == 0 &&
                    reloaded.Current.Windows.BackgroundOpacity == 1 &&
                    reloaded.Current.Windows.PanelBackgroundOpacity == 1 &&
                    reloaded.LoadWarning is null);
            }
        });
    }

    /// <summary>
    /// 临时「数据目录 + 模板目录」开箱即用（issue 017）：配置用例必须能指定模板从哪个文件夹来，
    /// 否则会顺手读到真程序目录里的 config.template.yaml，测出错误结果。
    /// </summary>
    private static void WithTempConfigDirectory(Action<string, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        var templateDirectory = Path.Combine(root, "template");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(templateDirectory);
        try
        {
            body(dataDirectory, templateDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>临时密钥目录：自测一律注入临时目录，不再触碰真实 .data 与 %APPDATA%。</summary>
    private static void WithTempSecretDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            body(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 注音配置（issue 008 Phase 2）：缺省 phonetics 段的旧配置必须回落「开 + 仅难词」双 true
    /// （与 AnnotationProfile.Load 默认口径一致）；显示关闭的配置要原样读写，克隆不丢字段。
    /// </summary>
    private static void TestPhoneticsConfigDefaultsAndRoundTrip()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                """);
            var defaults = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "legacy config without phonetics section falls back to enabled + rare-only defaults",
                defaults.Current.Phonetics.Enabled &&
                defaults.Current.Phonetics.RareOnly &&
                defaults.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);

            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                phonetics:
                  enabled: false
                  rare_only: false
                """);
            var disabled = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "phonetics switches load as written",
                !disabled.Current.Phonetics.Enabled && !disabled.Current.Phonetics.RareOnly);
            disabled.Current.Phonetics.Enabled = true;
            disabled.AtomicWrite(AppConfigCloner.Clone(disabled.Current));
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "phonetics switches survive cloner round-trip",
                reloaded.Current.Phonetics.Enabled &&
                !reloaded.Current.Phonetics.RareOnly);
        });
    }

    /// <summary>
    /// 回归测试：模板用 ~ 置空 prompts 后，关闭设置窗口（仅尺寸记忆回写、未保存任何修改）
    /// 曾把 prompts 段序列化成 {}，导致全部子键丢失。
    /// </summary>
    private static void TestEmptyPromptKeysSurviveRewrite()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                hotkeys:
                  capture: Ctrl+Shift+A
                  paste: ~
                prompts:
                  base: ~
                  japanese_plugin: ~
                  english_plugin: ~
                  chinese_plugin: ~
                  vision_base: ~
                  explain: ~
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "YAML null (~) prompt keys normalize to empty strings instead of staying null",
                store.Current.Prompts.Base is { Length: 0 } &&
                store.Current.Prompts.JapanesePlugin is { Length: 0 } &&
                store.Current.Prompts.EnglishPlugin is { Length: 0 } &&
                store.Current.Prompts.ChinesePlugin is { Length: 0 } &&
                store.Current.Prompts.VisionBase is { Length: 0 } &&
                store.Current.Prompts.Explain is { Length: 0 } &&
                store.Current.Hotkeys.Paste is { Length: 0 });

            // 模拟设置窗口未保存直接关闭：克隆打开时的快照后原样写盘（尺寸记忆路径）。
            store.AtomicWrite(AppConfigCloner.Clone(store.Current));
            var rewritten = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            Check(
                "settings-close rewrite keeps every prompts sub-key instead of collapsing to {}",
                rewritten.Contains("  base: ", StringComparison.Ordinal) &&
                rewritten.Contains("  japanese_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  english_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  chinese_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  vision_base: ", StringComparison.Ordinal) &&
                rewritten.Contains("  explain: ", StringComparison.Ordinal) &&
                rewritten.Contains("  paste: ", StringComparison.Ordinal) &&
                !rewritten.Contains("prompts: {}", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// 选取热键（issue 018）三段口径：<c>paste: ~</c> 归一化成空串（不弹默认值），
    /// 配置段整个缺失时取代码默认 <c>Ctrl+Shift+S</c>，显式空串表示关闭且不触达注册路径。
    /// </summary>
    private static void TestPasteHotkeyNormalization()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            // 显式 ~：收敛为空串，绝不替用户改回默认组合。
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                hotkeys:
                  capture: Ctrl+Shift+A
                  paste: ~
                """);
            var explicitNull = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "explicit paste null normalizes to an empty string instead of the default",
                explicitNull.Current.Hotkeys.Paste is { Length: 0 });

            // 段内缺失 paste 键：反序列化保留 HotkeyConfig 的代码默认。
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                hotkeys:
                  capture: Ctrl+Shift+A
                """);
            var missingKey = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "a missing paste key falls back to the code default",
                missingKey.Current.Hotkeys.Paste == "Ctrl+Shift+S");

            // 显式空串：原样保留，且注册路径据此整体跳过。
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                hotkeys:
                  capture: Ctrl+Shift+A
                  paste: ''
                """);
            var disabled = new ConfigStore(dataDirectory, templateDirectory);
            disabled.AtomicWrite(AppConfigCloner.Clone(disabled.Current));
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "an explicitly empty paste gesture stays empty across a rewrite",
                reloaded.Current.Hotkeys.Paste is { Length: 0 } &&
                File.ReadAllText(Path.Combine(dataDirectory, "config.yaml")).Contains("  paste: ''", StringComparison.Ordinal));
            Check(
                "an empty paste gesture never reaches hotkey registration",
                !HotkeyGesture.IsEnabled(reloaded.Current.Hotkeys.Paste) &&
                HotkeyGesture.IsEnabled(reloaded.Current.Hotkeys.Capture));
        });
    }

    /// <summary>
    /// 配置键迁移回归：prompts.vision_oneshot 已更名为 prompts.vision_base（009）。
    /// 用户旧配置里的旧键反序列化后无对应属性，必须被静默忽略并回落空串（= 用内置视觉 Base），
    /// 不得抛异常、不得改动配置版本号；重写时只落新键，不把旧键以新名复活。
    /// </summary>
    private static void TestLegacyVisionOneshotKeyIsIgnored()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                prompts:
                  vision_oneshot: '旧键：整段覆盖视觉提示词'
                  explain: ~
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "legacy vision_oneshot prompt key is ignored and falls back to the built-in vision base",
                store.Current.Prompts.VisionBase is { Length: 0 } &&
                store.Current.Version == "0.2.11" &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);

            store.AtomicWrite(store.Current);
            var rewritten = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            Check(
                "legacy vision_oneshot key is dropped on rewrite instead of being resurrected as vision_base",
                !rewritten.Contains("vision_oneshot", StringComparison.Ordinal) &&
                rewritten.Contains("vision_base", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// 配置档 4 行形态（issue 017）：name / kind / base_url / model 恒落行，api_type /
    /// supports_vision / timeout_seconds / enable_thinking 等于缺省即省略；显式非缺省值原样保留；
    /// 保存一次后不会反弹成 8 行，写盘形态也不含已废弃的 id 键。
    /// </summary>
    private static void TestProfileDocumentOmitsDefaults()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            var store = new ConfigStore(dataDirectory, templateDirectory);
            store.AtomicWrite(store.Current);
            var yaml = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            var profilesSection = ExtractSection(yaml, "profiles");
            var profileLines = profilesSection
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => !line.StartsWith("profiles:", StringComparison.Ordinal));
            Check(
                "default profiles persist as exactly four lines each",
                profileLines == 12 &&
                !profilesSection.Contains("api_type", StringComparison.Ordinal) &&
                !profilesSection.Contains("supports_vision", StringComparison.Ordinal) &&
                !profilesSection.Contains("timeout_seconds", StringComparison.Ordinal) &&
                !profilesSection.Contains("enable_thinking", StringComparison.Ordinal));
            Check(
                "persisted profiles carry no id key",
                !yaml.Contains("id:", StringComparison.Ordinal));

            // 显式非缺省值必须原样落行
            var customized = store.Current;
            customized.Profiles[0].ApiType = "responses";
            customized.Profiles[0].SupportsVision = false;
            customized.Profiles[0].TimeoutSeconds = 60;
            customized.Profiles[0].EnableThinking = true;
            store.AtomicWrite(customized);
            var rewritten = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            Check(
                "explicit non-default profile values are persisted verbatim",
                rewritten.Contains("api_type: responses", StringComparison.Ordinal) &&
                rewritten.Contains("supports_vision: false", StringComparison.Ordinal) &&
                rewritten.Contains("timeout_seconds: 60", StringComparison.Ordinal) &&
                rewritten.Contains("enable_thinking: true", StringComparison.Ordinal));

            // 读回后再写一次：其余两个档的缺省字段重新省略，不会反弹成 8 行
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            reloaded.AtomicWrite(reloaded.Current);
            var secondPass = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            var secondProfiles = ExtractSection(secondPass, "profiles");
            Check(
                "resaving a loaded config does not bounce back to eight lines per profile",
                secondProfiles
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Count(line => !line.StartsWith("profiles:", StringComparison.Ordinal)) == 16 &&
                !secondProfiles.Contains("api_type: completions", StringComparison.Ordinal) &&
                !secondProfiles.Contains("supports_vision: true", StringComparison.Ordinal) &&
                !secondProfiles.Contains("timeout_seconds: 30", StringComparison.Ordinal) &&
                !secondProfiles.Contains("enable_thinking: false", StringComparison.Ordinal) &&
                secondProfiles.Contains("api_type: responses", StringComparison.Ordinal) &&
                secondProfiles.Contains("timeout_seconds: 60", StringComparison.Ordinal));
        });
    }

    /// <summary>只有 name 的配置档：读盘时按缺省口径补齐 kind/api_type/supports_vision/timeout。</summary>
    private static void TestProfileWithOnlyNameGetsDefaults()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.2.11
                profiles:
                - name: 只有名字
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            var profile = store.Current.Profiles.Single();
            Check(
                "a profile with only a name resolves the documented defaults",
                profile.Name == "只有名字" &&
                profile.Kind == "openai" &&
                profile.ApiType == "completions" &&
                profile.SupportsVision &&
                !profile.EnableThinking &&
                profile.TimeoutSeconds == 30 &&
                profile.BaseUrl == "https://api.openai.com/v1" &&
                profile.Model == "gpt-4o-mini");
        });
    }

    /// <summary>
    /// 首启从模板字节级复制（issue 017）：注释与排版原样保留；已存在 config.yaml 时绝不被覆盖；
    /// 模板缺失时回落内置默认配置。
    /// </summary>
    private static void TestConfigSeedsFromTemplateByteForByte()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            const string template = """
                # 模板注释：首启复制后必须原样保留
                version: 9.9.9
                profiles:
                - name: 模板档
                  kind: claude
                  base_url: https://template.example/v1
                  model: template-model
                """;
            File.WriteAllText(Path.Combine(templateDirectory, "config.template.yaml"), template);

            var store = new ConfigStore(dataDirectory, templateDirectory);
            var seeded = File.ReadAllText(Path.Combine(dataDirectory, "config.yaml"));
            Check(
                "first launch copies the template byte for byte and keeps its comments",
                seeded == template);
            Check(
                "the seeded config parses the template profiles",
                store.Current.Profiles.Single().Name == "模板档" &&
                store.Current.Profiles.Single().Kind == "claude" &&
                store.Current.Version == "9.9.9");

            // 已有配置：原样不动
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), "version: 1.2.3\n");
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "an existing config is never overwritten by the template",
                reloaded.Current.Version == "1.2.3" &&
                File.ReadAllText(Path.Combine(dataDirectory, "config.yaml")).Contains("1.2.3", StringComparison.Ordinal));
        });

        // 模板也缺失：写内置默认
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            var store = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "a missing template falls back to the built-in default config",
                store.Current.Profiles.Count == 3 &&
                store.Current.Pipeline.ActiveProfile == "OpenAI 默认" &&
                File.Exists(Path.Combine(dataDirectory, "config.yaml")));
        });
    }

    /// <summary>
    /// 可写性拒绝路径（issue 017 决策 8）：数据目录不可创建/不可写时明确失败，不回落别处；
    /// 探测文件不残留。
    /// </summary>
    private static void TestDataDirectoryWritabilityRefusal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 用同名文件占住数据目录的位置：既建不了目录，也写不进文件。
            var blocked = Path.Combine(root, AppPaths.DataDirectoryName);
            File.WriteAllText(blocked, "not a directory");
            Check(
                "an unusable data directory refuses instead of falling back",
                ThrowsDataDirectoryUnavailable(() => AppPaths.EnsureWritableDataDirectory(blocked)) &&
                ThrowsDataDirectoryUnavailable(() => new ConfigStore(blocked)));

            var usable = Path.Combine(root, "data");
            AppPaths.EnsureWritableDataDirectory(usable);
            Check(
                "the write probe leaves nothing behind",
                Directory.GetFiles(usable).Length == 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool ThrowsDataDirectoryUnavailable(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (DataDirectoryUnavailableException)
        {
            return true;
        }
    }

    /// <summary>
    /// keys.bin 信封（issue 017）：整块 DPAPI 密文、无明文；按档名 Save/Load 往返一致；
    /// 整文件损坏时按「未配置」处理不抛；删除只掉对应键。
    /// </summary>
    private static void TestSecretStoreKeysBinEnvelope()
    {
        WithTempSecretDirectory(directory =>
        {
            var secrets = new SecretStore(directory);
            secrets.Save("档 A", "key-alpha");
            secrets.Save("档 B", "key-beta");
            var bytes = File.ReadAllBytes(Path.Combine(directory, "keys.bin"));
            var text = Encoding.UTF8.GetString(bytes);
            Check(
                "keys.bin stores only ciphertext without plaintext keys or profile names",
                !text.Contains("key-alpha", StringComparison.Ordinal) &&
                !text.Contains("key-beta", StringComparison.Ordinal) &&
                !text.Contains("档 A", StringComparison.Ordinal) &&
                !text.Contains("档 B", StringComparison.Ordinal));
            Check(
                "secrets round-trip per profile name",
                secrets.Load("档 A") == "key-alpha" &&
                secrets.Load("档 B") == "key-beta" &&
                secrets.Exists("档 A") &&
                !secrets.Exists("不存在的档"));

            // 整文件损坏：整体按未配置处理，绝不泄露密文内容
            File.WriteAllBytes(Path.Combine(directory, "keys.bin"), [1, 2, 3]);
            Check(
                "a corrupted keys.bin degrades to no secrets without throwing",
                secrets.Load("档 A") is null && !secrets.Exists("档 A"));

            secrets.Save("档 A", "key-alpha");
            secrets.Save("档 B", "key-beta");
            secrets.Delete("档 A");
            Check(
                "delete removes only the named entry",
                secrets.Load("档 A") is null && secrets.Load("档 B") == "key-beta");
        });
    }

    /// <summary>反向断言（零兼容）：旧布局 secrets\{id}.bin 一律不读，应用只认注入目录下的 keys.bin。</summary>
    private static void TestSecretStoreIgnoresLegacyLayout()
    {
        WithTempSecretDirectory(directory =>
        {
            var legacyDirectory = Path.Combine(directory, "secrets");
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllBytes(Path.Combine(legacyDirectory, "openai-default.bin"), [9, 9, 9]);
            var secrets = new SecretStore(directory);
            Check(
                "legacy per-profile secret files are never read",
                secrets.Load("openai-default") is null && !secrets.Exists("openai-default"));
        });
    }

    /// <summary>重命名配置档必须把密钥键名搬到新档名下，否则一改名 Key 就丢。</summary>
    private static void TestSecretRenameMovesProfileKey()
    {
        WithTempSecretDirectory(directory =>
        {
            var secrets = new SecretStore(directory);
            secrets.Save("旧档名", "secret-value");
            secrets.Rename("旧档名", "新档名");
            Check(
                "rename moves the secret to the new profile name",
                secrets.Load("新档名") == "secret-value" && secrets.Load("旧档名") is null);

            secrets.Rename("不存在的档", "另一个档");
            Check(
                "renaming an unknown profile is a no-op",
                secrets.Load("新档名") == "secret-value");
        });
    }

    /// <summary>版本差判定（issue 017 C 期）：major/minor 不同才判落后；仅 build 段不同不判；任一侧不可解析不判。</summary>
    private static void TestConfigVersionComparator()
    {
        Check(
            "major or minor drift marks the config as behind the template",
            ConfigVersionComparator.IsBehind("0.4.0", "0.3.9") &&
            ConfigVersionComparator.IsBehind("1.0.0", "0.9.9") &&
            ConfigVersionComparator.IsBehind("0.6.1", "0.5.2"));
        Check(
            "build-only drift never marks the config as behind",
            !ConfigVersionComparator.IsBehind("0.4.0+7", "0.4.0+3") &&
            !ConfigVersionComparator.IsBehind("0.4.0", "0.4.0") &&
            !ConfigVersionComparator.IsBehind("0.4.0+build.12", "0.4.0"));
        Check(
            "unparseable versions are not judged",
            !ConfigVersionComparator.IsBehind(null, "0.4.0") &&
            !ConfigVersionComparator.IsBehind("0.4.0", null) &&
            !ConfigVersionComparator.IsBehind("dev", "0.4.0") &&
            !ConfigVersionComparator.IsBehind("0.4.0", "nightly"));
    }

    /// <summary>
    /// 「关于」页版本号：宿主注入模板版本时显示 v{A}({B})（A=模板/最新，B=配置），
    /// 缺注入只显示 B；版本差给常驻一行提示，版本一致或任一侧不可解析时不显示。
    /// </summary>
    private static void TestAboutPageDisplaysTemplateAndConfigVersions()
    {
        // 待 P4 关于页迁移至 Avalonia 后重写并启用
    }

    /// <summary>收藏库改名搬家（issue 017）：nanikore.db → 数据目录下的 collections.db。</summary>
    private static void TestFavoritesDefaultDatabasePath()
    {
        Check(
            "collections database lives under the data directory",
            SqliteFavoriteService.DatabaseFileName == "collections.db" &&
            SqliteFavoriteService.DefaultDatabasePath ==
            Path.Combine(AppContext.BaseDirectory, AppPaths.DataDirectoryName, "collections.db"));
    }

    private static void TestConfigRoundTrip()
    {
        WithTempConfigDirectory((dataDirectory, templateDirectory) =>
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.1.1
                general:
                  auto_translate: true
                  paste_auto_translate: false
                  auto_explain: true
                  target_language: ja
                pipeline:
                  mode: invalid-mode
                  active_profile: test
                profiles:
                  - name: test
                    kind: openai
                    api_type: completions
                    base_url: https://example.test/v1
                    model: model-a
                    timeout_seconds: 20
                windows:
                  settings_width: 800
                  settings_height: 640
                prompts:
                  base: 'Custom Base {{target_language}}'
                  japanese_plugin: 'Custom ja-JP'
                  english_plugin: 'Custom en-US'
                  chinese_plugin: 'Custom zh-CN'
                  translate: 'Translate into {{target_language}} only.'
                  vision_base: "Transcribe\n\n---\n\nTranslate into {{target_language}}."
                unknown_field: ignored
                """);
            var store = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "YAML round-trip retains the manually maintained product version",
                store.Current.Version == "0.1.1" &&
                store.Current.Windows.SettingsWidth == 800 &&
                store.Current.Windows.SettingsHeight == 640 &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);
            Check(
                "YAML round-trip retains known fields and normalizes invalid values",
                store.Current.General.AutoTranslate &&
                !store.Current.General.PasteAutoTranslate &&
                store.Current.General.AutoExplain &&
                store.Current.General.TargetLanguage == "ja" &&
                store.Current.Pipeline.Mode == "local_ocr" &&
                store.Current.Pipeline.ActiveProfile == "test" &&
                store.Current.Profiles.Single().TimeoutSeconds == 20 &&
                store.Current.Profiles.Single().Name == "test" &&
                store.Current.Prompts.Base == "Custom Base {{target_language}}" &&
                store.Current.Prompts.JapanesePlugin == "Custom ja-JP" &&
                store.Current.Prompts.EnglishPlugin == "Custom en-US" &&
                store.Current.Prompts.ChinesePlugin == "Custom zh-CN" &&
                store.Current.Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "YAML reload preserves display names and modular prompt fields",
                reloaded.Current.Profiles.Single().Model == "model-a" &&
                reloaded.Current.Profiles.Single().Name == "test" &&
                reloaded.Current.Prompts.Base == "Custom Base {{target_language}}" &&
                reloaded.Current.Prompts.JapanesePlugin == "Custom ja-JP" &&
                reloaded.Current.Prompts.EnglishPlugin == "Custom en-US" &&
                reloaded.Current.Prompts.ChinesePlugin == "Custom zh-CN" &&
                reloaded.Current.Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}." &&
                reloaded.Current.Windows.SettingsWidth == 800 &&
                reloaded.Current.Windows.SettingsHeight == 640 &&
                !reloaded.Current.General.PasteAutoTranslate &&
                AppConfigCloner.Clone(reloaded.Current).General.AutoExplain &&
                !AppConfigCloner.Clone(reloaded.Current).General.PasteAutoTranslate &&
                !AppConfigCloner.Clone(new AppConfig { General = new GeneralConfig { AutoAnnotate = false } }).General.AutoAnnotate &&
                AppConfigCloner.Clone(new AppConfig { General = new GeneralConfig { OcrListen = "127.0.0.1:23335" } }).General.OcrListen == "127.0.0.1:23335" &&
                AppConfigCloner.Clone(reloaded.Current).Profiles.Single().Name == "test" &&
                AppConfigCloner.Clone(reloaded.Current).Prompts.JapanesePlugin == "Custom ja-JP" &&
                AppConfigCloner.Clone(reloaded.Current).Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");

            // 档名唯一化：重名追加 " (2)"，活动档按 name 匹配
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 1
                pipeline:
                  active_profile: 工作
                profiles:
                  - name: 工作
                    kind: openai
                    base_url: https://work.example/v1
                    model: work-model
                  - name: 工作
                    kind: gemini
                    base_url: https://personal.example/v1
                    model: personal-model
                """);
            var named = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "duplicate profile names normalize with a numeric suffix while the active profile stays by name",
                named.Current.Pipeline.ActiveProfile == "工作" &&
                named.Current.Profiles.Select(profile => profile.Name).SequenceEqual(["工作", "工作 (2)"]));

            // 活动档不中任何档名：回落第一个档
            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 1
                pipeline:
                  active_profile: 旧 id 索引
                profiles:
                - name: 第一个
                  base_url: https://first.example/v1
                  model: first-model
                """);
            var missingActive = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "an unmatched active profile falls back to the first profile by name",
                missingActive.Current.Pipeline.ActiveProfile == "第一个");

            File.WriteAllText(Path.Combine(dataDirectory, "config.yaml"), """
                version: 0.1.3
                translation:
                  active_profile: legacy
                  profiles:
                    - id: legacy
                      provider: openai
                      base_url: https://legacy.example/v1
                      model: legacy-model
                """);
            var unsupported = new ConfigStore(dataDirectory, templateDirectory);
            Check(
                "unsupported early config format falls back without migration",
                unsupported.Current.Pipeline.ActiveProfile == "OpenAI 默认" &&
                unsupported.Current.Profiles.Count == 3 &&
                !unsupported.Current.General.AutoTranslate &&
                unsupported.Current.General.PasteAutoTranslate &&
                unsupported.Current.General.TargetLanguage == "中文（简体）" &&
                File.ReadAllText(Path.Combine(dataDirectory, "config.yaml")).Contains("translation:", StringComparison.Ordinal));
        });
    }

    /// <summary>截取 YAML 顶层小节文本（从 「key:」 到下一个顶层键），供形态断言使用。</summary>
    private static string ExtractSection(string yaml, string sectionName)
    {
        var lines = yaml.Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith($"{sectionName}:", StringComparison.Ordinal));
        if (start < 0)
        {
            return string.Empty;
        }

        // 顶层键顶格且以字母开头；序列项（- name:）与缩进内容都属于当前小节。
        var end = start + 1;
        while (end < lines.Length && !IsTopLevelKey(lines[end]))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
    }

    private static bool IsTopLevelKey(string line) => line.Length > 0 && char.IsLetter(line[0]);

    private static void TestApplicationIconResource()
    {
        // 待 P2 平台服务迁移至 Avalonia WindowIcon 后重写并启用
    }
}
