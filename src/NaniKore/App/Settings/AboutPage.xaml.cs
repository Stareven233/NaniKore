using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // version 是产品发布版本，由 config.yaml 手工维护；不为尚未使用的 schema 版本引入额外字段。
        VersionText.Text = $"v{config.Version}";
        DescriptionText.Text = "本地 PP-OCRv6 检测与识别，配合可配置的云端流式翻译。截图、选区、OCR 全程在本机完成。";
    }

    public string? Validate() => null;

    public void Apply(AppConfig config) { }
}
