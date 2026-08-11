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
    private readonly AppConfig _working;
    private readonly Action<AppConfig> _save;
    private readonly List<ISettingsPage> _pages;
    private readonly TranslationPage _translationPage;

    public SettingsWindow(
        AppConfig current,
        Action<AppConfig> save,
        Func<ProviderProfile, string?, Task<string>> testConnection,
        Action<bool>? suspendGlobalHotkey = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
        // 结果卡本身是置顶窗口；设置从结果卡打开时也必须保持同等层级，避免被已有窗口遮挡。
        Topmost = true;

        _working = AppConfigCloner.Clone(current);
        _save = save;

        var general = new GeneralPage();
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
}
