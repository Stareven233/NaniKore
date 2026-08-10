namespace NaniKore.Config;

/// <summary>config.yaml 的根配置。</summary>
internal sealed class AppConfig
{
    /// <summary>手工维护的产品版本；不能再使用 int，否则合法的 x.y.z+b 会导致整份配置读取失败。</summary>
    public string Version { get; set; } = CurrentProductVersion;

    public const string CurrentProductVersion = "0.1.1";

    public GeneralConfig General { get; set; } = new();

    public PipelineConfig Pipeline { get; set; } = new();

    public List<ProviderProfile> Profiles { get; set; } =
    [
        ProviderProfile.CreateOpenAiDefault(),
        ProviderProfile.CreateClaudeDefault(),
        ProviderProfile.CreateGeminiDefault(),
    ];

    public HotkeyConfig Hotkeys { get; set; } = new();

    public PromptConfig Prompts { get; set; } = new();
}

internal sealed class GeneralConfig
{
    /// <summary>选区 OCR 完成后是否立即启动翻译；关闭时由面板按钮显式触发。</summary>
    public bool AutoTranslate { get; set; }

    public string TargetLanguage { get; set; } = "中文（简体）";
}

internal sealed class PipelineConfig
{
    /// <summary>local_ocr 或 vision_oneshot。</summary>
    public string Mode { get; set; } = "local_ocr";

    public string ActiveProfile { get; set; } = "openai-default";
}

internal sealed class ProviderProfile
{
    /// <summary>稳定内部索引；用于活动配置档和 DPAPI 密钥文件名，用户不可编辑。</summary>
    public string Id { get; set; } = "openai-default";

    /// <summary>面向用户的可编辑显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>openai、claude 或 gemini。</summary>
    public string Kind { get; set; } = "openai";

    /// <summary>仅 OpenAI 使用：completions 或 responses。</summary>
    public string ApiType { get; set; } = "completions";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>由用户声明模型是否接受图像，不用内置模型名单猜测。</summary>
    public bool SupportsVision { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    public static ProviderProfile CreateOpenAiDefault() => new() { Name = "OpenAI 默认" };

    public static ProviderProfile CreateClaudeDefault() => new()
    {
        Id = "claude-default",
        Name = "Claude 默认",
        Kind = "claude",
        BaseUrl = "https://api.anthropic.com",
        Model = "claude-haiku-4-5",
        SupportsVision = true,
    };

    public static ProviderProfile CreateGeminiDefault() => new()
    {
        Id = "gemini-default",
        Name = "Gemini 默认",
        Kind = "gemini",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Model = "gemini-2.0-flash",
        SupportsVision = true,
    };

    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        ApiType = ApiType,
        BaseUrl = BaseUrl,
        Model = Model,
        SupportsVision = SupportsVision,
        TimeoutSeconds = TimeoutSeconds,
    };
}

internal sealed class HotkeyConfig
{
    public string Capture { get; set; } = "Ctrl+Shift+A";

    /// <summary>为后续剪贴板翻译预留，当前不注册。</summary>
    public string TranslateClipboard { get; set; } = string.Empty;
}

internal sealed class PromptConfig
{
    /// <summary>空字符串时使用内置翻译模板。</summary>
    public string Translate { get; set; } = string.Empty;

    /// <summary>空字符串时使用内置精讲模板。</summary>
    public string Explain { get; set; } = string.Empty;
}

internal static class AppConfigCloner
{
    /// <summary>设置面板在副本上编辑，取消时绝不污染正在运行的配置。</summary>
    public static AppConfig Clone(AppConfig source) => new()
    {
        Version = source.Version,
        General = new GeneralConfig
        {
            AutoTranslate = source.General.AutoTranslate,
            TargetLanguage = source.General.TargetLanguage,
        },
        Pipeline = new PipelineConfig
        {
            Mode = source.Pipeline.Mode,
            ActiveProfile = source.Pipeline.ActiveProfile,
        },
        Profiles = source.Profiles.Select(profile => profile.Clone()).ToList(),
        Hotkeys = new HotkeyConfig
        {
            Capture = source.Hotkeys.Capture,
            TranslateClipboard = source.Hotkeys.TranslateClipboard,
        },
        Prompts = new PromptConfig
        {
            Translate = source.Prompts.Translate,
            Explain = source.Prompts.Explain,
        },
    };
}
