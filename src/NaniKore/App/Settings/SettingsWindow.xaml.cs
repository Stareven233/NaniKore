using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Config;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace NaniKore.App.Settings;

/// <summary>非模态设置宿主；所有页面在配置副本上编辑，保存后一次性热应用。</summary>
internal sealed partial class SettingsWindow : Window
{
    /// <summary>
    /// 实时配置提供者（宿主传入 <c>() =&gt; _configStore.Current</c>）。
    /// 未保存就关闭时的尺寸回写以它取到的最新配置为基础，而不是打开时的快照：
    /// 快照可能已被其他入口（结果卡 AutoExplainChanged → ApplySettings → AtomicWrite）
    /// 改写，拿旧快照整份写盘会把这些更新静默回滚。
    /// </summary>
    private readonly Func<AppConfig> _currentConfig;
    private readonly AppConfig _working;
    private readonly Action<AppConfig> _save;
    private readonly List<ISettingsPage> _pages;
    private readonly TranslationPage _translationPage;
    private readonly DispatcherTimer _successStatusTimer;

    /// <summary>业务配置是否已成功落盘；决定关闭时尺寸记录克隆哪份副本。</summary>
    private bool _settingsSaved;

    /// <summary>尺寸比较容差：吸收不同 DPI 下 Actual* 的取整漂移，偏差小于该值视为未变化。</summary>
    private const double SizeEpsilon = 1.0;

    public SettingsWindow(
        Func<AppConfig> current,
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

        // 打开时取一份快照作为编辑副本；实时提供者保留下来，关闭时的尺寸回写
        // 必须以当前最新配置为基础，不能用打开时的快照（见 _currentConfig 注释）。
        var snapshot = current();
        ArgumentNullException.ThrowIfNull(snapshot);
        _currentConfig = current;
        _working = AppConfigCloner.Clone(snapshot);
        _save = save;
        _successStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _successStatusTimer.Tick += OnSuccessStatusTimerTick;
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
        // 关闭时记录窗口尺寸（仅在实际改变过尺寸时才写盘）；尺寸走独立副本，不携带尚未保存的页面编辑。
        Closed += (_, _) =>
        {
            _successStatusTimer.Stop();
            SaveWindowSize();
        };
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
            ShowSavedStatus();
        }
        catch (Exception exception)
        {
            FooterStatus.Show(exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>保存成功提示只短暂显示，避免长期占用页脚状态区域。</summary>
    private void ShowSavedStatus()
    {
        FooterStatus.Show("设置已保存。", InfoSeverity.Success);
        _successStatusTimer.Stop();
        _successStatusTimer.Start();
    }

    private void OnSuccessStatusTimerTick(object? sender, EventArgs e)
    {
        _successStatusTimer.Stop();
        // 只有当前仍是本次保存产生的成功提示时才清除，避免覆盖后续的校验或错误信息。
        if (FooterStatus.Severity == InfoSeverity.Success && FooterStatus.Message == "设置已保存。")
        {
            FooterStatus.Clear();
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
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        // 尺寸回写的基线必须是“当前最新的配置”：保存过业务配置时以 _working 为准
        // （其内容已随保存落盘，只叠加尺寸）；没保存过时 _working 里只有未保存的页面编辑，
        // 必须改用实时配置——打开时的快照可能已被其他入口改写，整份写回会静默回滚。
        var baseline = ResolveCloseWriteBackBaseline(_settingsSaved, _working, _currentConfig());
        if (ResolvePersistedSize(baseline.Windows, ActualWidth, ActualHeight, MinWidth, MinHeight) is not { } size)
        {
            // 尺寸与基线一致（或取不到可信尺寸）时完全不写盘：保存过说明业务配置和
            // 当前尺寸已经落盘，没保存过说明用户什么都没改——“进设置窗口逛一圈直接
            // 关掉”不该重写 config.yaml（丢注释、~ 被规范化成 '' 等副作用）。
            return;
        }

        var next = AppConfigCloner.Clone(baseline);
        next.Windows.SettingsWidth = size.Width;
        next.Windows.SettingsHeight = size.Height;
        try
        {
            _save(next);
        }
        catch
        {
            // 尺寸记忆是尽力而为的：关闭流程不该因写盘失败而中断。
        }
    }

    /// <summary>
    /// 关闭时尺寸回写的基线决策：已保存过业务配置时以编辑副本 <paramref name="working"/> 为准
    /// （其内容已随保存落盘，只叠加尺寸）；未保存时必须取 <paramref name="latest"/> 实时配置，
    /// 避免打开时的快照覆盖其他入口（结果卡 AutoExplainChanged → NaniKoreApp.ApplySettings
    /// → AtomicWrite）期间的写盘。未保存的页面编辑只存在于 <paramref name="working"/>，
    /// 选用 <paramref name="latest"/> 即可天然丢弃，不夹带任何业务字段。
    /// </summary>
    internal static AppConfig ResolveCloseWriteBackBaseline(bool settingsSaved, AppConfig working, AppConfig latest)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(latest);
        return settingsSaved ? working : latest;
    }

    /// <summary>
    /// 尺寸记忆的纯决策：返回需要写回的窗口尺寸，没有任何变化时返回 null 表示不必写盘。
    /// 尺寸不可信（布局尚未完成、窗口从未显示）或与基线一致时跳过；
    /// WPF 的 ActualWidth 与 Width 严格相等（已实测多轮开关零漂移），故 1px 容差足够。
    /// </summary>
    internal static (double Width, double Height)? ResolvePersistedSize(
        WindowConfig baseline, double actualWidth, double actualHeight, double minWidth, double minHeight)
    {
        if (!double.IsFinite(actualWidth) || !double.IsFinite(actualHeight) ||
            actualWidth <= 0 || actualHeight <= 0)
        {
            return null;
        }

        // 以 XAML 声明的最小尺寸为下限，防止异常 DPI 数据把窗口缩到不可操作。
        var width = Math.Max(minWidth, actualWidth);
        var height = Math.Max(minHeight, actualHeight);
        if (Math.Abs(baseline.SettingsWidth - width) < SizeEpsilon &&
            Math.Abs(baseline.SettingsHeight - height) < SizeEpsilon)
        {
            return null;
        }

        return (width, height);
    }
}
