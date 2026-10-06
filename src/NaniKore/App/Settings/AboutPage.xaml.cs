using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NaniKore.App.Controls;
using NaniKore.Config;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class AboutPage : UserControl, ISettingsPage
{
    public AboutPage()
    {
        InitializeComponent();
        AppIcon = ApplicationIcon.LoadWpf();
    }

    /// <summary>关于页展示应用图标；与托盘、窗口图标共用同一嵌入资源。</summary>
    public ImageSource AppIcon { get; }

    public string Title => "关于";

    public string Glyph => "\uE946";

    public FrameworkElement View => this;

    /// <summary>供自测断言的完整描述文本，避免依赖控件树结构。</summary>
    public string DisplayText => $"{VersionText.Text}\r\n{DescriptionText.Text}";

    /// <summary>
    /// 宿主注入的模板版本提供者：「关于」页版本号的 A 段（最新）取程序目录 config.template.yaml 的 version；
    /// 缺注入（自测、--settings-preview）时只显示配置版本 B，即现有行为。
    /// </summary>
    public Func<string?>? TemplateVersionProvider { get; set; }

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // version 是产品发布版本：A 段取模板（本次发布版本），B 段取当前配置版本，一眼看出配置落后多少。
        var templateVersion = TemplateVersionProvider?.Invoke();
        VersionText.Text = string.IsNullOrWhiteSpace(templateVersion)
            ? $"v{config.Version}"
            : $"v{templateVersion}({config.Version})";
        DescriptionText.Text = "本地 PP-OCRv6 检测与识别，配合可配置的云端流式翻译。截图、选区、OCR 全程在本机完成。";
        // 版本差只提示不动作：major/minor 不一致时常驻一行；任一侧解析不出版本号则不误报。
        if (ConfigVersionComparator.IsBehind(templateVersion, config.Version))
        {
            VersionMismatchBar.Show("当前配置版本落后于模板，可对照程序目录 config.template.yaml 手动核对。", InfoSeverity.Warning);
        }
        else
        {
            VersionMismatchBar.Clear();
        }
    }

    public string? Validate() => null;

    public void Apply(AppConfig config) { }
}
