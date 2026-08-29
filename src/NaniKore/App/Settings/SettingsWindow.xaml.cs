using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Config;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace NaniKore.App.Settings;

/// <summary>非模态设置宿主；所有页面在配置副本上编辑，保存后一次性热应用。</summary>
internal sealed partial class SettingsWindow : Window
{
    /// <summary>打开窗口时的原始配置快照；未保存就关闭时，尺寸记录以它为准，避免把页面编辑污染进配置。</summary>
    private readonly AppConfig _initialConfig;
    private readonly AppConfig _working;
    private readonly Action<AppConfig> _save;
    private readonly List<ISettingsPage> _pages;
    private readonly TranslationPage _translationPage;

    /// <summary>业务配置是否已成功落盘；决定关闭时尺寸记录克隆哪份副本。</summary>
    private bool _settingsSaved;

    public SettingsWindow(
        AppConfig current,
        Action<AppConfig> save,
        Func<ProviderProfile, string?, Task<string>> testConnection,
        Action<bool>? suspendGlobalHotkey = null,
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
        // 结果卡本身是置顶窗口；设置从结果卡打开时也必须保持同等层级，避免被已有窗口遮挡。
        Topmost = true;

        // 页面编辑直接改动 _working，因此必须额外保留一份打开时的原始快照，
        // 供未保存就关闭时记录窗口尺寸使用。
        _initialConfig = AppConfigCloner.Clone(current);
        _working = AppConfigCloner.Clone(current);
        _save = save;
        Width = _working.Windows.SettingsWidth;
        Height = _working.Windows.SettingsHeight;

        var general = new GeneralPage(downloadDependencies);
        _translationPage = new TranslationPage { TestConnection = testConnection };
        var hotkeys = new HotkeyPage();
        // 录制期间必须临时注销已生效的全局热键，否则系统会把按键交给 NaniKore 自己的截图流程。
        hotkeys.RecordingChanged += (_, recording) => suspendGlobalHotkey?.Invoke(recording);
        var about = new AboutPage();
        _pages = [general, _translationPage, hotkeys, about];

        foreach (var page in _pages)
        {
            NavigationList.Items.Add(CreateNavigationItem(page));
            page.Load(_working);
        }

        NavigationList.SelectedIndex = 0;
        // 无论保存还是取消，关闭时都记录窗口尺寸；尺寸走独立副本，不携带尚未保存的页面编辑。
        Closed += (_, _) => SaveWindowSize();
    }

    /// <summary>导航项：图标 + 标题，图标用 Segoe Fluent Icons 字形，不引入图片资源。</summary>
    private static FrameworkElement CreateNavigationItem(ISettingsPage page)
    {
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Tag = page };
        var glyph = new TextBlock
        {
            Text = page.Glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        layout.Children.Add(glyph);
        layout.Children.Add(new TextBlock { Text = page.Title, VerticalAlignment = VerticalAlignment.Center });
        return layout;
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not FrameworkElement { Tag: ISettingsPage page })
        {
            return;
        }

        PageTitle.Text = page.Title;
        PageHost.Content = page.View;
        FooterStatus.Clear();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void Save()
    {
        foreach (var page in _pages)
        {
            if (page.Validate() is not { } error)
            {
                continue;
            }

            // 校验失败时切到出错的页面并就地提示，用户不必猜是哪一页的字段有问题。
            SelectPage(page);
            FooterStatus.Show(error, InfoSeverity.Warning);
            return;
        }

        foreach (var page in _pages)
        {
            page.Apply(_working);
        }

        var active = _working.Profiles.First(profile => string.Equals(profile.Id, _working.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_working.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase) && !active.SupportsVision)
        {
            SelectPage(_translationPage);
            FooterStatus.Show("vision_oneshot 需要当前配置档声明支持图像输入。", InfoSeverity.Warning);
            return;
        }

        try
        {
            _save(_working);
            // 仅在业务配置落盘成功后置位：校验失败或写盘抛错时保持 false，
            // 关闭流程的尺寸记录才会退回初始快照而不是污染后的 _working。
            _settingsSaved = true;
            _translationPage.SaveSecrets();
            Close();
        }
        catch (Exception exception)
        {
            FooterStatus.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void SelectPage(ISettingsPage page)
    {
        var index = _pages.IndexOf(page);
        if (index >= 0)
        {
            NavigationList.SelectedIndex = index;
        }
    }

    /// <summary>关闭时把实际窗口尺寸写回配置，下次打开按上次大小恢复。</summary>
    private void SaveWindowSize()
    {
        // 最小化或布局尚未完成时 Actual* 不是可信尺寸，跳过本次记录。
        if (WindowState == WindowState.Minimized ||
            !double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight))
        {
            return;
        }

        // 页面在输入时会直接改动 _working，不能无条件克隆它：
        // 保存成功过才以 _working 为基础（业务配置已落盘，只叠加尺寸）；
        // 否则退回打开时的初始快照，未保存的页面编辑不会跟着写盘。
        var next = AppConfigCloner.Clone(_settingsSaved ? _working : _initialConfig);
        // 以 XAML 声明的最小尺寸为下限，防止异常 DPI 数据把窗口缩到不可操作。
        next.Windows.SettingsWidth = Math.Max(MinWidth, ActualWidth);
        next.Windows.SettingsHeight = Math.Max(MinHeight, ActualHeight);
        try
        {
            _save(next);
        }
        catch
        {
            // 尺寸记忆是尽力而为的：关闭流程不该因写盘失败而中断。
        }
    }
}
