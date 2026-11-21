using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using NaniKore.App.Controls;
using NaniKore.Config;

namespace NaniKore.App.Settings;

internal sealed partial class AboutPage : UserControl, ISettingsPage
{
    public AboutPage()
    {
        InitializeComponent();
        AppIconBitmap = ApplicationIcon.LoadBitmap();
        ToolTip.SetTip(RepositoryButton, $"在浏览器打开 GitHub 仓库\n{RepositoryUrl}");
        DataContext = this;
    }

    /// <summary>关于页展示应用图标。</summary>
    public Avalonia.Media.Imaging.Bitmap AppIconBitmap { get; }

    /// <summary>项目仓库地址，GitHub 按钮跳转目标；自测直接断言此常量。</summary>
    public const string RepositoryUrl = "https://github.com/Stareven233/NaniKore";

    public string Title => "关于";

    public string Glyph => "\uE946";

    public Control View => this;

    /// <summary>供自测断言的完整描述文本，避免依赖控件树结构。</summary>
    public string DisplayText => $"{VersionText.Text}\r\n{DescriptionText.Text}";

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        VersionText.Text = $"v{ProductVersion.Current}";
        DescriptionText.Text = "如果你觉得 NaniKore 还不错，请给 GitHub 仓库点个 Star喵，这很重要喵\n本地 PP-OCRv6 检测与识别，配合可配置的云端流式翻译。截图、选区、OCR 全程在本机完成";
    }

    public string? Validate() => null;

    /// <summary>用默认浏览器打开仓库；失败时弹窗告知，不静默吞掉。</summary>
    private void OnRepositoryButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MessageDialog.Show(TopLevel.GetTopLevel(this) as Window, "打开 GitHub 仓库失败", exception.Message, InfoSeverity.Error);
        }
    }

    public void Apply(AppConfig config) { }
}
