using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NaniKore.Config;

/// <summary>
/// 配置加载、默认值生成和原子写盘。运行期间内存配置是唯一真相，只有保存操作会写回磁盘。
/// </summary>
internal sealed class ConfigStore
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private static readonly Regex ProfileIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    public ConfigStore(string configDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "config.yaml");
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        Current = Load();
    }

    public AppConfig Current { get; private set; }

    public string ConfigPath => _configPath;

    /// <summary>供设置面板显示的非致命读取/规范化提醒。</summary>
    public string? LoadWarning { get; private set; }

    public ProviderProfile GetActiveProfile() => GetProfile(Current.Pipeline.ActiveProfile);

    public ProviderProfile GetProfile(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Current.Profiles.FirstOrDefault(profile =>
                   string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"找不到配置档“{id}”。");
    }

    /// <summary>临时文件写完后再替换，避免崩溃留下半份 YAML。</summary>
    public void AtomicWrite(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Normalize(config, reportWarnings: false);
        var yaml = _serializer.Serialize(config);
        var temporaryPath = _configPath + ".tmp";
        File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, _configPath, overwrite: true);
        Current = config;
    }

    private AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = new AppConfig();
            AtomicWrite(defaults);
            return defaults;
        }

        try
        {
            var yaml = File.ReadAllText(_configPath);
            if (ContainsLegacyTranslationSection(yaml))
            {
                // 旧版 Step 1 配置把翻译选项嵌套在 translation 下；迁移仅发生在内存中，
                // 仍遵守“用户点击保存后才写盘”的约定，避免启动时意外覆盖用户文件。
                var legacyConfig = _deserializer.Deserialize<LegacyAppConfig>(yaml) ?? new LegacyAppConfig();
                var migratedConfig = MigrateLegacyConfig(legacyConfig);
                LoadWarning = "检测到旧版 config.yaml，已在内存中迁移为当前格式；请在设置中确认后保存。";
                Normalize(migratedConfig, reportWarnings: false);
                return migratedConfig;
            }

            var config = _deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
            Normalize(config, reportWarnings: true);
            return config;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            LoadWarning = $"读取 config.yaml 失败，已使用默认配置：{exception.Message}";
            return new AppConfig();
        }
    }

    /// <summary>未知字段由反序列化器忽略；已知字段的非法值在这里回退至安全默认值。</summary>
    private static bool ContainsLegacyTranslationSection(string yaml) =>
        Regex.IsMatch(yaml, @"(?m)^translation\s*:");

    private static AppConfig MigrateLegacyConfig(LegacyAppConfig legacyConfig)
    {
        var legacyProfiles = legacyConfig.Translation?.Profiles ?? [];
        var activeProfile = legacyProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, legacyConfig.Translation?.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            ?? legacyProfiles.FirstOrDefault();

        return new AppConfig
        {
            Version = legacyConfig.Version is > 0 ? legacyConfig.Version : 1,
            General = new GeneralConfig
            {
                AutoTranslate = activeProfile?.AutoTranslate ?? false,
                TargetLanguage = activeProfile?.TargetLanguage ?? "zh-CN",
            },
            Pipeline = new PipelineConfig
            {
                Mode = "local_ocr",
                ActiveProfile = legacyConfig.Translation?.ActiveProfile ?? string.Empty,
            },
            Profiles = legacyProfiles.Select(profile => new ProviderProfile
            {
                Id = profile.Id ?? string.Empty,
                Kind = profile.Provider ?? "openai",
                ApiType = profile.OpenAiForm ?? "completions",
                BaseUrl = profile.BaseUrl ?? string.Empty,
                Model = profile.Model ?? string.Empty,
            }).ToList(),
        };
    }

    private void Normalize(AppConfig config, bool reportWarnings)
    {
        var normalized = false;
        if (config.Version > 1)
        {
            LoadWarning = $"config.yaml 的版本 {config.Version} 高于当前支持的版本 1；未知内容可能不会生效。";
        }

        config.General ??= new GeneralConfig();
        config.Pipeline ??= new PipelineConfig();
        config.Hotkeys ??= new HotkeyConfig();
        config.Prompts ??= new PromptConfig();
        config.Profiles ??= [];

        if (string.IsNullOrWhiteSpace(config.General.TargetLanguage))
        {
            config.General.TargetLanguage = "zh-CN";
            normalized = true;
        }

        if (!string.Equals(config.Pipeline.Mode, "local_ocr", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase))
        {
            config.Pipeline.Mode = "local_ocr";
            normalized = true;
        }

        if (config.Profiles.Count == 0)
        {
            config.Profiles.Add(ProviderProfile.CreateOpenAiDefault());
            normalized = true;
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Profiles)
        {
            profile.Id = profile.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profile.Id) || !ProfileIdPattern.IsMatch(profile.Id) || !usedIds.Add(profile.Id))
            {
                profile.Id = $"profile-{usedIds.Count + 1}";
                usedIds.Add(profile.Id);
                normalized = true;
            }

            // 旧配置缺少 name 时以原有 ID 作为显示名；重命名只改 Name，DPAPI 索引不受影响。
            var originalName = profile.Name;
            var baseName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name.Trim();
            var normalizedName = baseName;
            var suffix = 2;
            while (!usedNames.Add(normalizedName))
            {
                normalizedName = $"{baseName} ({suffix++})";
            }

            if (!string.Equals(originalName, normalizedName, StringComparison.Ordinal))
            {
                profile.Name = normalizedName;
                normalized = true;
            }

            profile.Kind = profile.Kind?.Trim().ToLowerInvariant() ?? "openai";
            if (profile.Kind is not ("openai" or "claude" or "gemini"))
            {
                profile.Kind = "openai";
                normalized = true;
            }

            profile.ApiType = profile.ApiType?.Trim().ToLowerInvariant() ?? "completions";
            if (profile.ApiType is not ("completions" or "responses"))
            {
                profile.ApiType = "completions";
                normalized = true;
            }

            if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out _))
            {
                profile.BaseUrl = profile.Kind switch
                {
                    "claude" => "https://api.anthropic.com",
                    "gemini" => "https://generativelanguage.googleapis.com/v1beta",
                    _ => "https://api.openai.com/v1",
                };
                normalized = true;
            }

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                profile.Model = "gpt-4o-mini";
                normalized = true;
            }

            if (profile.TimeoutSeconds is < 1 or > 600)
            {
                profile.TimeoutSeconds = 60;
                normalized = true;
            }
        }

        if (!config.Profiles.Any(profile => string.Equals(profile.Id, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase)))
        {
            config.Pipeline.ActiveProfile = config.Profiles[0].Id;
            normalized = true;
        }

        if (string.IsNullOrWhiteSpace(config.Hotkeys.Capture))
        {
            config.Hotkeys.Capture = "Ctrl+Shift+A";
            normalized = true;
        }

        if (reportWarnings && normalized)
        {
            LoadWarning ??= "config.yaml 中的非法配置已回落到默认值。";
        }
    }
}

/// <summary>
/// 仅用于读取阶段 3 之前的配置格式。保留在此处可避免旧字段重新渗入运行期配置模型。
/// </summary>
internal sealed class LegacyAppConfig
{
    public int Version { get; set; }

    public LegacyTranslationConfig? Translation { get; set; }
}

internal sealed class LegacyTranslationConfig
{
    public string? ActiveProfile { get; set; }

    public List<LegacyProviderProfile>? Profiles { get; set; }
}

internal sealed class LegacyProviderProfile
{
    public string? Id { get; set; }

    public string? Provider { get; set; }

    public string? BaseUrl { get; set; }

    public string? Model { get; set; }

    public string? OpenAiForm { get; set; }

    public string? TargetLanguage { get; set; }

    public bool AutoTranslate { get; set; }
}
