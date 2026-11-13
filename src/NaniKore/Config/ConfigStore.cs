using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NaniKore.Config;

/// <summary>
/// 配置加载、默认值生成和原子写盘。运行期间内存配置是唯一真相，只有保存操作会写回磁盘。
/// 面向数据目录（issue 017）：读 <c>.data/config.yaml</c>，缺失时从程序目录 <c>config.template.yaml</c>
/// 按字节原样复制生成一份再读；已有时原样不动。模板目录可注入，未注入时用程序自己所在的文件夹。
/// </summary>
internal sealed class ConfigStore
{
    // YamlDotNet 静态上下文（裁决 2 + epic 002 穿刺 2）：编译期生成的访问器取代运行期反射，
    // NativeAOT 下无 IL3050。三个选项与迁移前的反射 builder 逐项一致；「读→写→读」产出
    // 经穿刺 2 在 10 份样本（含真实 .data/config.yaml）上验证与反射路径逐字节等价。
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new ConfigYamlStaticContext())
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();
    private static readonly ISerializer Serializer = new StaticSerializerBuilder(new ConfigYamlStaticContext())
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();
    private readonly string _configPath;
    private readonly string _templatePath;

    // 读取失败后置位：内存虽退回默认值让程序仍能启动，但拒绝写盘，防止覆盖无法解析的原文件（R09）。
    private bool _readFailed;

    public ConfigStore(string dataDirectory, string? templateDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        // 程序目录/.data 不可创建或不可写时明确失败（DataDirectoryUnavailableException），不回落别处。
        AppPaths.EnsureWritableDataDirectory(dataDirectory);
        _configPath = Path.Combine(dataDirectory, AppPaths.ConfigFileName);
        _templatePath = Path.Combine(templateDirectory ?? AppContext.BaseDirectory, AppPaths.TemplateFileName);
        Current = Load();
    }

    public AppConfig Current { get; private set; }

    public string ConfigPath => _configPath;

    /// <summary>供设置面板显示的非致命读取/规范化提醒。</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>
    /// 配置读取失败（格式错误/无权限/IO）。此时 <see cref="AtomicWrite"/> 会拒绝写盘，
    /// 避免把用户无法解析的原文件静默覆盖成默认配置（R09）。
    /// </summary>
    public bool ReadFailed => _readFailed;

    public ProviderProfile GetActiveProfile() => GetProfile(Current.Pipeline.ActiveProfile);

    /// <summary>按档名查找；档名是配置档唯一身份（issue 017）。</summary>
    public ProviderProfile GetProfile(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Current.Profiles.FirstOrDefault(profile =>
                   string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"找不到配置档“{name}”。");
    }

    /// <summary>临时文件写完后再替换，避免崩溃留下半份 YAML。</summary>
    public void AtomicWrite(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 读取失败时拒绝覆盖原文件（R09）：宁可不保存，也不把无法解析的用户配置静默抹成默认值。
        if (_readFailed)
        {
            throw new ConfigWriteRefusedException(LoadWarning ?? "config.yaml 读取失败，已拒绝写入。");
        }

        Normalize(config, reportWarnings: false);
        // 写盘只走磁盘契约：缺省字段在此折叠成 null 并被序列器省略（4 行形态的唯一来源）。
        var yaml = Serializer.Serialize(AppConfigDocument.ToDocument(config));
        var temporaryPath = _configPath + ".tmp";
        File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, _configPath, overwrite: true);
        Current = config;
    }

    /// <summary>
    /// 单独读模板版本号（「关于」页的 A 段 = 最新版本）；模板缺失或不可解析返回 null，调用方按「不判定」处理。
    /// </summary>
    public static string? ReadTemplateVersion(string? templateDirectory = null)
    {
        var templatePath = Path.Combine(templateDirectory ?? AppContext.BaseDirectory, AppPaths.TemplateFileName);
        try
        {
            if (!File.Exists(templatePath))
            {
                return null;
            }

            return Deserializer.Deserialize<AppConfigDocument>(File.ReadAllText(templatePath))?.Version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            // 模板不可读只影响版本差提示这类展示性功能，绝不阻断启动。
            return null;
        }
    }

    private AppConfig Load()
    {
        if (File.Exists(_configPath))
        {
            return ReadConfig();
        }

        // 首启：模板按字节原样复制（File.Copy 不做文本编解码，注释与排版原样保留），之后再读这份副本。
        if (TrySeedFromTemplate())
        {
            return ReadConfig();
        }

        // 模板也缺失（开发期异常）：写一份内置默认配置。
        var defaults = new AppConfig();
        AtomicWrite(defaults);
        return defaults;
    }

    private AppConfig ReadConfig()
    {
        try
        {
            var yaml = File.ReadAllText(_configPath);
            var document = Deserializer.Deserialize<AppConfigDocument>(yaml) ?? new AppConfigDocument();
            var config = document.ToRuntime();
            Normalize(config, reportWarnings: true);
            _readFailed = false;
            return config;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            // 读取失败不等于首次使用（R09）：内存退回默认值让程序仍能启动，但记录失败并锁定写盘，
            // 避免后续保存把无法解析的原文件覆盖成默认配置。
            _readFailed = true;
            LoadWarning = DescribeReadFailure(exception);
            return new AppConfig();
        }
    }

    /// <summary>
    /// 把读取失败分类成可行动的中文说明：区分格式错误、无权限与 IO 失败；格式错误额外读出
    /// 出错行原文（即「解析有问题的一项」），便于用户直接定位修改。
    /// </summary>
    private string DescribeReadFailure(Exception exception)
    {
        var reason = exception switch
        {
            YamlDotNet.Core.YamlException yaml => $"config.yaml 格式错误：{DescribeYamlError(yaml)}",
            UnauthorizedAccessException => $"config.yaml 无权限读取：{exception.Message}",
            _ => $"config.yaml 读取失败：{exception.Message}",
        };

        return $"{reason}。为避免覆盖原文件，本次不会写入 config.yaml；请修正或删除 {_configPath} 后重启。";
    }

    /// <summary>从 YamlException 的标记位置读出出错行原文，拼进说明里点名「有问题的一项」。</summary>
    private string DescribeYamlError(YamlDotNet.Core.YamlException exception)
    {
        var line = exception.Start.Line; // YamlDotNet 行号从 1 开始
        if (line <= 0)
        {
            return exception.Message;
        }

        try
        {
            var lines = File.ReadAllLines(_configPath);
            if (line <= lines.Length)
            {
                return $"{exception.Message}（第 {line} 行：{lines[line - 1].Trim()}）";
            }
        }
        catch (Exception readException) when (readException is IOException or UnauthorizedAccessException)
        {
            // 二次读取失败时只保留原始异常文本，不影响主流程。
        }

        return exception.Message;
    }

    /// <returns>true 表示已从模板生成 config.yaml；模板缺失或复制失败返回 false。</returns>
    private bool TrySeedFromTemplate()
    {
        try
        {
            if (!File.Exists(_templatePath))
            {
                return false;
            }

            File.Copy(_templatePath, _configPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"从配置模板生成 config.yaml 失败，已使用默认配置：{exception.Message}";
            return false;
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
        config.Fonts ??= new FontConfig();
        config.Profiles ??= [];

        // YAML 的 ~ 是显式空值，反序列化得到的是 null 而不是空串。提示词与选取热键
        // 必须在此收敛为空串：序列化器配置了 OmitNull，null 属性会被整体省略，
        // 回写时把 prompts 段塌缩成 {}，全部子键丢失。表现为：模板用 ~ 置空后，
        // 进入设置窗口不做任何修改、仅关闭时的尺寸记忆也会清空 prompts。
        // 收敛后空串仍表示“回落内置默认模板”，语义不变。
        config.Prompts.Base ??= string.Empty;
        // 三个语言注音插件键已退役（issue 008 Phase 3）：仍收敛空值，保证旧文件回写不丢段、不塌缩
        config.Prompts.JapanesePlugin ??= string.Empty;
        config.Prompts.EnglishPlugin ??= string.Empty;
        config.Prompts.ChinesePlugin ??= string.Empty;
        config.Prompts.VisionBase ??= string.Empty;
        config.Prompts.Explain ??= string.Empty;
        // 选取热键的 ~ 同样收敛为空串：空串＝关闭该热键，归一化绝不替用户改成默认组合。
        config.Hotkeys.Paste ??= string.Empty;
        // 字体键（issue 033）的 ~ 收敛为空串：空串＝平台默认字体，这里不做任何字体名兜底，
        // 否则 OmitNull 会把 fonts 段塌成 {}，与 prompts 段同一范式。
        config.Fonts.Ui ??= string.Empty;
        config.Fonts.Prompt ??= string.Empty;

        // 手工配置无效时沿用读取提醒；设置页则在保存前显式校验，不能静默吞掉输入错误。
        if (!ResourceIdleDuration.TryParse(config.General.LocalResourceIdleTimeout, out _))
        {
            config.General.LocalResourceIdleTimeout = "0";
            normalized = true;
        }
        else
        {
            config.General.LocalResourceIdleTimeout = config.General.LocalResourceIdleTimeout.Trim().ToLowerInvariant();
        }

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

        // 档名是唯一身份：缺名按位置生成占位名，重名追加 " (2)"（issue 017 起不再有 id 索引）。
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < config.Profiles.Count; index++)
        {
            var profile = config.Profiles[index];
            var baseName = string.IsNullOrWhiteSpace(profile.Name) ? $"配置档 {index + 1}" : profile.Name.Trim();
            var normalizedName = baseName;
            var suffix = 2;
            while (!usedNames.Add(normalizedName))
            {
                normalizedName = $"{baseName} ({suffix++})";
            }

            if (!string.Equals(profile.Name, normalizedName, StringComparison.Ordinal))
            {
                profile.Name = normalizedName;
                normalized = true;
            }

            profile.Kind = profile.Kind?.Trim().ToLowerInvariant() ?? ProviderProfileDefaults.Kind;
            if (profile.Kind is not ("openai" or "claude" or "gemini"))
            {
                profile.Kind = ProviderProfileDefaults.Kind;
                normalized = true;
            }

            profile.ApiType = profile.ApiType?.Trim().ToLowerInvariant() ?? ProviderProfileDefaults.ApiType;
            if (profile.ApiType is not ("completions" or "responses"))
            {
                profile.ApiType = ProviderProfileDefaults.ApiType;
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
                profile.TimeoutSeconds = ProviderProfileDefaults.TimeoutSeconds;
                normalized = true;
            }
        }

        // active_profile 只按 name 匹配，不中则回落第一个档（既有不变量，不为旧 id 开特例）。
        if (!config.Profiles.Any(profile => string.Equals(profile.Name, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase)))
        {
            config.Pipeline.ActiveProfile = config.Profiles[0].Name;
            normalized = true;
        }

        if (string.IsNullOrWhiteSpace(config.Hotkeys.Capture))
        {
            config.Hotkeys.Capture = "Ctrl+Shift+A";
            normalized = true;
        }

        // Paste 刻意不做空值兜底：空串就是「关闭该热键」的显式表达；配置段缺失时
        // 反序列化会保留 HotkeyConfig 的代码默认（Ctrl+Shift+S），无需在这里补写。

        // 背景路径只收敛空值与首尾空白，不依赖本机文件是否存在。
        config.Windows.BackgroundImage = config.Windows.BackgroundImage?.Trim() ?? string.Empty;
        // 非有限数与越界值统一恢复默认，避免把非法参数带入背景渲染。
        if (!double.IsFinite(config.Windows.BackgroundBlur) || config.Windows.BackgroundBlur is < 0 or > 100)
        {
            config.Windows.BackgroundBlur = 0;
            normalized = true;
        }

        if (!double.IsFinite(config.Windows.BackgroundOpacity) || config.Windows.BackgroundOpacity is < 0 or > 1)
        {
            config.Windows.BackgroundOpacity = 1;
            normalized = true;
        }

        if (!double.IsFinite(config.Windows.PanelBackgroundOpacity) || config.Windows.PanelBackgroundOpacity is < 0 or > 1)
        {
            config.Windows.PanelBackgroundOpacity = 1;
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

        // 结果面板尺寸记忆：越界或非法数据回退到面板 XAML 默认（960×660），
        // 下限对齐面板 MinWidth/MinHeight，防止异常 DPI 数据把面板缩到不可操作。
        if (double.IsNaN(config.Windows.ResultPanelWidth) || double.IsInfinity(config.Windows.ResultPanelWidth) ||
            config.Windows.ResultPanelWidth is < 440 or > 3840)
        {
            config.Windows.ResultPanelWidth = 960;
            normalized = true;
        }

        if (double.IsNaN(config.Windows.ResultPanelHeight) || double.IsInfinity(config.Windows.ResultPanelHeight) ||
            config.Windows.ResultPanelHeight is < 300 or > 2160)
        {
            config.Windows.ResultPanelHeight = 660;
            normalized = true;
        }

        if (reportWarnings && normalized)
        {
            LoadWarning ??= "config.yaml 中的非法配置已回落到默认值。";
        }
    }
}

/// <summary>
/// config.yaml 读取失败后仍尝试写盘时抛出（R09）：拒绝用默认值覆盖无法解析的原文件。
/// 提示信息本身已是可行动中文，调用方通常直接展示。
/// </summary>
internal sealed class ConfigWriteRefusedException(string message) : Exception(message);
