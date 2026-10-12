namespace NaniKore.Config;

/// <summary>config.yaml 的根配置。</summary>
internal sealed class AppConfig
{
    /// <summary>手工维护的产品发布版本；不能再使用 int，否则合法的 x.y.z+b 会导致整份配置读取失败。</summary>
    public string Version { get; set; } = CurrentProductVersion;

    /// <summary>仅在 config.yaml 缺失版本时使用的默认产品版本。</summary>
    public const string CurrentProductVersion = "0.1.3";

    public GeneralConfig General { get; set; } = new();

    public PipelineConfig Pipeline { get; set; } = new();

    public List<ProviderProfile> Profiles { get; set; } =
    [
        ProviderProfile.CreateOpenAiDefault(),
        ProviderProfile.CreateClaudeDefault(),
        ProviderProfile.CreateGeminiDefault(),
    ];

    public HotkeyConfig Hotkeys { get; set; } = new();

    public WindowConfig Windows { get; set; } = new();

    public PromptConfig Prompts { get; set; } = new();

    /// <summary>本地注音（issue 008）：开关与过滤口径；词典本体走按语言下载，不随配置分发。</summary>
    public PhoneticsConfig Phonetics { get; set; } = new();
}

internal sealed class GeneralConfig
{
    /// <summary>选区 OCR 完成后是否立即启动翻译；关闭时由面板按钮显式触发。</summary>
    public bool AutoTranslate { get; set; }

    /// <summary>阶段 1 翻译完成后是否自动开始并展开语法深度精讲。</summary>
    public bool AutoExplain { get; set; }

    public string TargetLanguage { get; set; } = "中文（简体）";
}

internal sealed class PipelineConfig
{
    /// <summary>local_ocr 或 vision_oneshot。</summary>
    public string Mode { get; set; } = "local_ocr";

    /// <summary>激活配置档的 name（档名是唯一身份，issue 017：旧 id 索引已废除）。</summary>
    public string ActiveProfile { get; set; } = ProviderProfileDefaults.DefaultActiveProfileName;
}

internal sealed class PhoneticsConfig
{
    /// <summary>原文注音总开关；关闭时原文区直接呈现纯文本（翻译不受影响）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>仅注难词：开启时叠加常用字符表过滤；关闭时只走停用词表（全文注音）。</summary>
    public bool RareOnly { get; set; } = true;
}

/// <summary>
/// 配置档缺省口径的唯一真相（issue 017）：运行期 <see cref="ProviderProfile"/> 的属性初值、
/// 落盘省略判定与读盘兜底都引用这里，杜绝三处默认值各自漂移。
/// </summary>
internal static class ProviderProfileDefaults
{
    public const string Kind = "openai";
    public const string ApiType = "completions";
    public const bool SupportsVision = true;
    public const bool EnableThinking = false;

    /// <summary>没人愿意为一次翻译等 10 秒：统一 30 秒，官方默认档也不再显式写 60。</summary>
    public const int TimeoutSeconds = 30;

    /// <summary>三个官方默认档中第一个档的档名，同时是新配置激活档的缺省。</summary>
    public const string DefaultActiveProfileName = "OpenAI 默认";
}

internal sealed class ProviderProfile
{
    /// <summary>面向用户的显示名称，同时是配置档的唯一身份：活动档、下拉、报错与密钥索引全部用它。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>openai、claude 或 gemini。</summary>
    public string Kind { get; set; } = ProviderProfileDefaults.Kind;

    /// <summary>仅 OpenAI 使用：completions 或 responses。</summary>
    public string ApiType { get; set; } = ProviderProfileDefaults.ApiType;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>由用户声明模型是否接受图像，不用内置模型名单猜测。</summary>
    public bool SupportsVision { get; set; } = ProviderProfileDefaults.SupportsVision;

    /// <summary>是否启用模型思考链（Reasoning / Thinking）；默认关闭以追求快速响应与节省 Token。</summary>
    public bool EnableThinking { get; set; } = ProviderProfileDefaults.EnableThinking;

    public int TimeoutSeconds { get; set; } = ProviderProfileDefaults.TimeoutSeconds;

    public static ProviderProfile CreateOpenAiDefault() => new() { Name = ProviderProfileDefaults.DefaultActiveProfileName };

    public static ProviderProfile CreateClaudeDefault() => new()
    {
        Name = "Claude 默认",
        Kind = "claude",
        BaseUrl = "https://api.anthropic.com",
        Model = "claude-haiku-4-5",
    };

    public static ProviderProfile CreateGeminiDefault() => new()
    {
        Name = "Gemini 默认",
        Kind = "gemini",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Model = "gemini-2.0-flash",
    };

    public ProviderProfile Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        ApiType = ApiType,
        BaseUrl = BaseUrl,
        Model = Model,
        SupportsVision = SupportsVision,
        EnableThinking = EnableThinking,
        TimeoutSeconds = TimeoutSeconds,
    };
}

internal sealed class HotkeyConfig
{
    /// <summary>截图翻译热键；空串由 <see cref="ConfigStore"/> 归一化兜底成 Ctrl+Shift+A。</summary>
    public string Capture { get; set; } = "Ctrl+Shift+A";

    /// <summary>
    /// 选取/剪贴板翻译热键（issue 018）：按下后按优先级取选区文本 → 剪贴板文本 → 剪贴板图像。
    /// 空串表示关闭该热键，不做空值兜底；配置缺失时由这里的代码默认 Ctrl+Shift+S 提供。
    /// </summary>
    public string Paste { get; set; } = "Ctrl+Shift+S";
}

internal sealed class WindowConfig
{
    /// <summary>设置窗口上次关闭时的客户区宽度与高度；无效值由配置加载阶段回退。</summary>
    public double SettingsWidth { get; set; } = 760;

    public double SettingsHeight { get; set; } = 620;
}

internal sealed class PromptConfig
{
    /// <summary>Base 基础提示词（翻译纪律与目标语言约束）；空字符串时使用内置 Base 模板。</summary>
    public string Base { get; set; } = string.Empty;

    /// <summary>
    /// 已退役的日语注音插件（issue 008 Phase 3）：注音职责移交本地注音引擎，本键不再参与 prompt 组装；
    /// 旧配置中的自定义值原样保留，仅做数据留存，设置页亦不再展示。
    /// </summary>
    public string JapanesePlugin { get; set; } = string.Empty;

    /// <summary>已退役的英语 IPA 注音插件；旧值保留、不再生效，详见 <see cref="JapanesePlugin"/>。</summary>
    public string EnglishPlugin { get; set; } = string.Empty;

    /// <summary>已退役的中文拼音注音插件；旧值保留、不再生效，详见 <see cref="JapanesePlugin"/>。</summary>
    public string ChinesePlugin { get; set; } = string.Empty;

    /// <summary>视觉链路 Base 提示词（纯转录 + 译文两段式协议）；空字符串时使用内置视觉 Base 模板。</summary>
    public string VisionBase { get; set; } = string.Empty;

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
            AutoExplain = source.General.AutoExplain,
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
            Paste = source.Hotkeys.Paste,
        },
        Windows = new WindowConfig
        {
            SettingsWidth = source.Windows.SettingsWidth,
            SettingsHeight = source.Windows.SettingsHeight,
        },
        Prompts = new PromptConfig
        {
            Base = source.Prompts.Base,
            // 三个语言注音插件键已退役（issue 008 Phase 3）：旧值随克隆保留，保证取消设置时不丢用户数据，但不参与组装
            JapanesePlugin = source.Prompts.JapanesePlugin,
            EnglishPlugin = source.Prompts.EnglishPlugin,
            ChinesePlugin = source.Prompts.ChinesePlugin,
            VisionBase = source.Prompts.VisionBase,
            Explain = source.Prompts.Explain,
        },
        Phonetics = new PhoneticsConfig
        {
            Enabled = source.Phonetics.Enabled,
            RareOnly = source.Phonetics.RareOnly,
        },
    };
}
