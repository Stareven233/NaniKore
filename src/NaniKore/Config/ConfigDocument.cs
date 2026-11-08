namespace NaniKore.Config;

/// <summary>
/// 配置档的磁盘契约（issue 017）：可缺省字段全部可空，落盘时缺省折叠成 null 由序列器的 OmitNull 省略。
/// <see cref="ToRuntime"/> 与 <see cref="ToDocument"/> 是「哪些字段落盘、缺省如何省略」的唯一映射处，
/// 改配置档 schema 只改这两个函数。DTO 不设 Id 字段：旧配置里的 id 键由反序列化器的
/// <c>IgnoreUnmatchedProperties()</c> 静默吞掉，不写任何专门兼容代码。
/// </summary>
internal sealed class ProviderProfileDocument
{
    /// <summary>档名 = 唯一身份：活动档、下拉、报错与密钥索引全部用它。</summary>
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public string? BaseUrl { get; set; }

    public string? Model { get; set; }

    /// <summary>缺省 completions；仅 OpenAI 使用。</summary>
    public string? ApiType { get; set; }

    /// <summary>缺省 true；偏离缺省时才落行。</summary>
    public bool? SupportsVision { get; set; }

    /// <summary>缺省 30 秒；偏离缺省时才落行。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>缺省 false；偏离缺省时才落行。</summary>
    public bool? EnableThinking { get; set; }

    public ProviderProfile ToRuntime() => new()
    {
        // 空 name 交给 ConfigStore.Normalize 生成占位档名；BaseUrl/Model 空串由 Normalize 按 kind 兜底。
        Name = Name ?? string.Empty,
        Kind = Kind ?? ProviderProfileDefaults.Kind,
        ApiType = ApiType ?? ProviderProfileDefaults.ApiType,
        BaseUrl = BaseUrl ?? string.Empty,
        Model = Model ?? string.Empty,
        SupportsVision = SupportsVision ?? ProviderProfileDefaults.SupportsVision,
        EnableThinking = EnableThinking ?? ProviderProfileDefaults.EnableThinking,
        TimeoutSeconds = TimeoutSeconds ?? ProviderProfileDefaults.TimeoutSeconds,
    };

    /// <summary>运行期配置档 → 磁盘契约：name/kind/base_url/model 恒落行，其余字段等于缺省即省略。</summary>
    public static ProviderProfileDocument ToDocument(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProviderProfileDocument
        {
            Name = profile.Name,
            Kind = profile.Kind,
            BaseUrl = profile.BaseUrl,
            Model = profile.Model,
            ApiType = string.Equals(profile.ApiType, ProviderProfileDefaults.ApiType, StringComparison.OrdinalIgnoreCase)
                ? null
                : profile.ApiType,
            SupportsVision = profile.SupportsVision == ProviderProfileDefaults.SupportsVision ? null : profile.SupportsVision,
            TimeoutSeconds = profile.TimeoutSeconds == ProviderProfileDefaults.TimeoutSeconds ? null : profile.TimeoutSeconds,
            EnableThinking = profile.EnableThinking == ProviderProfileDefaults.EnableThinking ? null : profile.EnableThinking,
        };
    }
}

/// <summary>
/// config.yaml 根级磁盘契约：读盘反序列化到本类型再 <see cref="ToRuntime"/>，写盘由 <see cref="ToDocument"/> 折叠而来。
/// 非配置档小节沿用运行期类型（缺省省略只针对配置档字段，issue 017 目标 3）。
/// </summary>
internal sealed class AppConfigDocument
{
    public string? Version { get; set; }

    public GeneralConfig? General { get; set; }

    public PipelineConfig? Pipeline { get; set; }

    public List<ProviderProfileDocument>? Profiles { get; set; }

    public HotkeyConfig? Hotkeys { get; set; }

    public WindowConfig? Windows { get; set; }

    public PromptConfig? Prompts { get; set; }

    public PhoneticsConfig? Phonetics { get; set; }

    public FontConfig? Fonts { get; set; }

    public AppConfig ToRuntime()
    {
        var config = new AppConfig
        {
            Version = Version ?? AppConfig.CurrentProductVersion,
            General = General ?? new GeneralConfig(),
            Pipeline = Pipeline ?? new PipelineConfig(),
            Hotkeys = Hotkeys ?? new HotkeyConfig(),
            Windows = Windows ?? new WindowConfig(),
            Prompts = Prompts ?? new PromptConfig(),
            Phonetics = Phonetics ?? new PhoneticsConfig(),
            Fonts = Fonts ?? new FontConfig(),
        };
        // 磁盘上没有 profiles 段时保留运行期缺省（三个官方默认档）；有段才逐档映射
        // （含显式空列表，交由 Normalize 兜底），与旧反序列化器「缺段保留初值」的语义一致。
        if (Profiles is not null)
        {
            config.Profiles = Profiles.Select(profile => profile.ToRuntime()).ToList();
        }

        return config;
    }

    /// <summary>运行期配置 → 磁盘契约；只在写盘时走。</summary>
    public static AppConfigDocument ToDocument(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new AppConfigDocument
        {
            Version = config.Version,
            General = config.General,
            Pipeline = config.Pipeline,
            Profiles = config.Profiles.Select(ProviderProfileDocument.ToDocument).ToList(),
            Hotkeys = config.Hotkeys,
            Windows = config.Windows,
            Prompts = config.Prompts,
            Phonetics = config.Phonetics,
            Fonts = config.Fonts,
        };
    }
}

/// <summary>
/// 配置版本差判定（issue 017 C 期）：模板与配置的 version 在 major 或 minor 上不一致即视为落后；
/// 仅 build 段不同不算（dev 构建每次只递增 build），任一侧解析不出版本号不判定（不误报）。
/// </summary>
internal static class ConfigVersionComparator
{
    public static bool IsBehind(string? templateVersion, string? configVersion)
    {
        var template = Parse(templateVersion);
        var config = Parse(configVersion);
        return template is not null && config is not null &&
            (template.Value.Major != config.Value.Major || template.Value.Minor != config.Value.Minor);
    }

    /// <summary>取 +build 之前的两段；解析失败返回 null 表示「不判定」。</summary>
    private static (int Major, int Minor)? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var baseVersion = version.Split('+', 2)[0];
        var parts = baseVersion.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? (major, minor)
            : null;
    }
}
