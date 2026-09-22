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

    private void Normalize(AppConfig config, bool reportWarnings)
    {
        var normalized = false;
        if (string.IsNullOrWhiteSpace(config.Version))
        {
            config.Version = AppConfig.CurrentProductVersion;
            normalized = true;
        }

        config.General ??= new GeneralConfig();
        config.Pipeline ??= new PipelineConfig();
        config.Hotkeys ??= new HotkeyConfig();
        config.Windows ??= new WindowConfig();
        config.Prompts ??= new PromptConfig();
        config.Profiles ??= [];

        // YAML 的 ~ 是显式空值，反序列化得到的是 null 而不是空串。提示词与预留热键
        // 必须在此收敛为空串：序列化器配置了 OmitNull，null 属性会被整体省略，
        // 回写时把 prompts 段塌缩成 {}，全部子键丢失。表现为：模板用 ~ 置空后，
        // 进入设置窗口不做任何修改、仅关闭时的尺寸记忆也会清空 prompts。
        // 收敛后空串仍表示“回落内置默认模板”，语义不变。
        config.Prompts.Base ??= string.Empty;
        config.Prompts.JapanesePlugin ??= string.Empty;
        config.Prompts.EnglishPlugin ??= string.Empty;
        config.Prompts.VisionBase ??= string.Empty;
        config.Prompts.Explain ??= string.Empty;
        config.Hotkeys.TranslateClipboard ??= string.Empty;

        if (string.IsNullOrWhiteSpace(config.General.TargetLanguage))
        {
            config.General.TargetLanguage = "中文（简体）";
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

        // 防止手工配置、损坏文件或异常 DPI 数据把设置窗口恢复成不可用的尺寸。
        if (double.IsNaN(config.Windows.SettingsWidth) || double.IsInfinity(config.Windows.SettingsWidth) ||
            config.Windows.SettingsWidth is < 620 or > 3840)
        {
            config.Windows.SettingsWidth = 760;
            normalized = true;
        }

        if (double.IsNaN(config.Windows.SettingsHeight) || double.IsInfinity(config.Windows.SettingsHeight) ||
            config.Windows.SettingsHeight is < 460 or > 2160)
        {
            config.Windows.SettingsHeight = 620;
            normalized = true;
        }

        if (reportWarnings && normalized)
        {
            LoadWarning ??= "config.yaml 中的非法配置已回落到默认值。";
        }
    }
}
