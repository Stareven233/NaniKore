using NaniKore.App;
using NaniKore.App.Settings;
using NaniKore.Config;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunConfigTests()
    {
        TestConfigRoundTrip();
        TestAboutPageDisplaysProductVersion();
        TestApplicationIconResource();
        TestProfileRenameKeepsSecret();
    }

    private static void TestConfigRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NaniKore-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.yaml"), """
                version: 0.1.1
                general:
                  auto_translate: true
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
                  translate: 'Translate into {{target_language}} only.'
                  vision_oneshot: "Transcribe\n\n---\n\nTranslate into {{target_language}}."
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
                store.Current.General.TargetLanguage == "ja" &&
                store.Current.Pipeline.Mode == "local_ocr" &&
                store.Current.Profiles.Single().TimeoutSeconds == 20 &&
                store.Current.Profiles.Single().Name == "test" &&
                store.Current.Prompts.Translate == "Translate into {{target_language}} only." &&
                store.Current.Prompts.VisionOneshot == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");
            store.AtomicWrite(store.Current);
            var reloaded = new ConfigStore(directory);
            Check(
                "YAML reload preserves generated display names",
                reloaded.Current.Profiles.Single().Model == "model-a" &&
                reloaded.Current.Profiles.Single().Name == "test" &&
                reloaded.Current.Prompts.Translate == "Translate into {{target_language}} only." &&
                reloaded.Current.Prompts.VisionOneshot == "Transcribe\n\n---\n\nTranslate into {{target_language}}." &&
                reloaded.Current.Windows.SettingsWidth == 800 &&
                reloaded.Current.Windows.SettingsHeight == 640 &&
                AppConfigCloner.Clone(reloaded.Current).Profiles.Single().Name == "test" &&
                AppConfigCloner.Clone(reloaded.Current).Prompts.VisionOneshot == "Transcribe\n\n---\n\nTranslate into {{target_language}}.");

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
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

