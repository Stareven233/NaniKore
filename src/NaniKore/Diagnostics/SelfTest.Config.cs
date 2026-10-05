using NaniKore.App;
using NaniKore.App.Settings;
using NaniKore.Config;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunConfigTests()
    {
        TestConfigRoundTrip();
        TestEmptyPromptKeysSurviveRewrite();
        TestLegacyVisionOneshotKeyIsIgnored();
        TestPhoneticsConfigDefaultsAndRoundTrip();
        TestAboutPageDisplaysProductVersion();
        TestApplicationIconResource();
        TestProfileRenameKeepsSecret();
    }

    /// <summary>临时配置目录开箱即用：4 个配置回写用例反复手写 Path.Combine + try/finally，收敛成一个执行体。</summary>
    private static void WithTempConfigDirectory(Action<string> body)
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
        WithTempConfigDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.2.11
                """);
            var defaults = new ConfigStore(directory);
            Check(
                "legacy config without phonetics section falls back to enabled + rare-only defaults",
                defaults.Current.Phonetics.Enabled &&
                defaults.Current.Phonetics.RareOnly &&
                defaults.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);

            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.2.11
                phonetics:
                  enabled: false
                  rare_only: false
                """);
            var disabled = new ConfigStore(directory);
            Check(
                "phonetics switches load as written",
                !disabled.Current.Phonetics.Enabled && !disabled.Current.Phonetics.RareOnly);
            disabled.Current.Phonetics.Enabled = true;
            disabled.AtomicWrite(AppConfigCloner.Clone(disabled.Current));
            var reloaded = new ConfigStore(directory);
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
        WithTempConfigDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.2.11
                hotkeys:
                  capture: Ctrl+Shift+A
                  translate_clipboard: ~
                prompts:
                  base: ~
                  japanese_plugin: ~
                  english_plugin: ~
                  chinese_plugin: ~
                  vision_base: ~
                  explain: ~
                """);
            var store = new ConfigStore(directory);
            Check(
                "YAML null (~) prompt keys normalize to empty strings instead of staying null",
                store.Current.Prompts.Base is { Length: 0 } &&
                store.Current.Prompts.JapanesePlugin is { Length: 0 } &&
                store.Current.Prompts.EnglishPlugin is { Length: 0 } &&
                store.Current.Prompts.ChinesePlugin is { Length: 0 } &&
                store.Current.Prompts.VisionBase is { Length: 0 } &&
                store.Current.Prompts.Explain is { Length: 0 } &&
                store.Current.Hotkeys.TranslateClipboard is { Length: 0 });

            // 模拟设置窗口未保存直接关闭：克隆打开时的快照后原样写盘（尺寸记忆路径）。
            store.AtomicWrite(AppConfigCloner.Clone(store.Current));
            var rewritten = File.ReadAllText(Path.Combine(directory, "config.yaml"));
            Check(
                "settings-close rewrite keeps every prompts sub-key instead of collapsing to {}",
                rewritten.Contains("  base: ", StringComparison.Ordinal) &&
                rewritten.Contains("  japanese_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  english_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  chinese_plugin: ", StringComparison.Ordinal) &&
                rewritten.Contains("  vision_base: ", StringComparison.Ordinal) &&
                rewritten.Contains("  explain: ", StringComparison.Ordinal) &&
                rewritten.Contains("  translate_clipboard: ", StringComparison.Ordinal) &&
                !rewritten.Contains("prompts: {}", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// 配置键迁移回归：prompts.vision_oneshot 已更名为 prompts.vision_base（009）。
    /// 用户旧配置里的旧键反序列化后无对应属性，必须被静默忽略并回落空串（= 用内置视觉 Base），
    /// 不得抛异常、不得改动配置版本号；重写时只落新键，不把旧键以新名复活。
    /// </summary>
    private static void TestLegacyVisionOneshotKeyIsIgnored()
    {
        WithTempConfigDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.2.11
                prompts:
                  vision_oneshot: '旧键：整段覆盖视觉提示词'
                  explain: ~
                """);
            var store = new ConfigStore(directory);
            Check(
                "legacy vision_oneshot prompt key is ignored and falls back to the built-in vision base",
                store.Current.Prompts.VisionBase is { Length: 0 } &&
                store.Current.Version == "0.2.11" &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);

            store.AtomicWrite(store.Current);
            var rewritten = File.ReadAllText(Path.Combine(directory, "config.yaml"));
            Check(
                "legacy vision_oneshot key is dropped on rewrite instead of being resurrected as vision_base",
                !rewritten.Contains("vision_oneshot", StringComparison.Ordinal) &&
                rewritten.Contains("vision_base", StringComparison.Ordinal));
        });
    }

    private static void TestConfigRoundTrip()
    {
        WithTempConfigDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.1.1
                general:
                  auto_translate: true
                  auto_explain: true
                  target_language: ja
                pipeline:
                  mode: invalid-mode
                  active_profile: test
                profiles:
                  - id: test
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
            var store = new ConfigStore(directory);
            Check(
                "YAML round-trip retains the manually maintained product version",
                store.Current.Version == "0.1.1" &&
                store.Current.Windows.SettingsWidth == 800 &&
                store.Current.Windows.SettingsHeight == 640 &&
                store.LoadWarning?.Contains("读取 config.yaml 失败", StringComparison.Ordinal) != true);
            Check(
                "YAML round-trip retains known fields and normalizes invalid values",
                store.Current.General.AutoTranslate &&
                store.Current.General.AutoExplain &&
                store.Current.General.TargetLanguage == "ja" &&
                store.Current.Pipeline.Mode == "local_ocr" &&
                store.Current.Profiles.Single().TimeoutSeconds == 20 &&
                store.Current.Profiles.Single().Name == "test" &&
                store.Current.Prompts.Base == "Custom Base {{target_language}}" &&
                store.Current.Prompts.JapanesePlugin == "Custom ja-JP" &&
                store.Current.Prompts.EnglishPlugin == "Custom en-US" &&
                store.Current.Prompts.ChinesePlugin == "Custom zh-CN" &&
                store.Current.Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(directory);
            Check(
                "YAML reload preserves generated display names and modular prompt fields",
                reloaded.Current.Profiles.Single().Model == "model-a" &&
                reloaded.Current.Profiles.Single().Name == "test" &&
                reloaded.Current.Prompts.Base == "Custom Base {{target_language}}" &&
                reloaded.Current.Prompts.JapanesePlugin == "Custom ja-JP" &&
                reloaded.Current.Prompts.EnglishPlugin == "Custom en-US" &&
                reloaded.Current.Prompts.ChinesePlugin == "Custom zh-CN" &&
                reloaded.Current.Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}." &&
                reloaded.Current.Windows.SettingsWidth == 800 &&
                reloaded.Current.Windows.SettingsHeight == 640 &&
                AppConfigCloner.Clone(reloaded.Current).General.AutoExplain &&
                AppConfigCloner.Clone(reloaded.Current).Profiles.Single().Name == "test" &&
                AppConfigCloner.Clone(reloaded.Current).Prompts.JapanesePlugin == "Custom ja-JP" &&
                AppConfigCloner.Clone(reloaded.Current).Prompts.VisionBase == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");

            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 1
                pipeline:
                  active_profile: work
                profiles:
                  - id: work
                    name: 工作
                    kind: openai
                    base_url: https://work.example/v1
                    model: work-model
                  - id: personal
                    name: 工作
                    kind: gemini
                    base_url: https://personal.example/v1
                    model: personal-model
                """);
            var named = new ConfigStore(directory);
            Check(
                "duplicate profile names normalize without changing stable IDs or active profile",
                named.Current.Pipeline.ActiveProfile == "work" &&
                named.Current.Profiles.Select(profile => profile.Id).SequenceEqual(["work", "personal"]) &&
                named.Current.Profiles.Select(profile => profile.Name).SequenceEqual(["工作", "工作 (2)"]));

            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.1.3
                translation:
                  active_profile: legacy
                  profiles:
                    - id: legacy
                      provider: openai
                      base_url: https://legacy.example/v1
                      model: legacy-model
                """);
            var unsupported = new ConfigStore(directory);
            Check(
                "unsupported early config format falls back without migration",
                unsupported.Current.Pipeline.ActiveProfile == "openai-default" &&
                unsupported.Current.Profiles.Count == 3 &&
                unsupported.Current.Profiles.All(profile => profile.Id != "legacy") &&
                !unsupported.Current.General.AutoTranslate &&
                unsupported.Current.General.TargetLanguage == "中文（简体）" &&
                File.ReadAllText(Path.Combine(directory, "config.yaml")).Contains("translation:", StringComparison.Ordinal));
        });
    }

    private static void TestAboutPageDisplaysProductVersion()
    {
        const string productVersion = "0.1.3+test.1";
        var page = new AboutPage();
        page.Load(new AppConfig { Version = productVersion });

        var description = page.DisplayText;
        Check(
            "about page displays the configured product version without a schema version",
            description.Contains($"v{productVersion}", StringComparison.Ordinal) &&
            !description.Contains("Schema", StringComparison.OrdinalIgnoreCase));
    }

    private static void TestApplicationIconResource()
    {
        using var icon = ApplicationIcon.LoadWinForms();
        Check(
            "application icon is loaded from the bundled NaniKore asset",
            icon.Width > 0 && icon.Height > 0);
    }

    private static void TestProfileRenameKeepsSecret()
    {
        var profileId = $"self-test-{Guid.NewGuid():N}";
        const string secret = "self-test-secret";
        try
        {
            SecretStore.Save(profileId, secret);
            var profile = new ProviderProfile { Id = profileId, Name = "原名称" };
            profile.Name = "重命名后";
            Check(
                "profile rename keeps the stable DPAPI secret index",
                profile.Id == profileId && SecretStore.Load(profile.Id) == secret);
        }
        finally
        {
            SecretStore.Delete(profileId);
        }
    }
}

