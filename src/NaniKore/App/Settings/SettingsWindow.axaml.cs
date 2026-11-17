using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Input;

namespace NaniKore.App.Settings;

/// <summary>
/// 导航列表项视图模型，完全兼容编译绑定与按需延迟加载。
/// </summary>
internal sealed class NavigationItem(string title, string glyph, Func<ISettingsPage> pageFactory)
{
    private ISettingsPage? _instance;

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    public bool IsCreated => _instance is not null;

    public ISettingsPage GetOrCreatePage(AppConfig working)
    {
        if (_instance is null)
        {
            _instance = pageFactory();
            _instance.Load(working);
        }

        return _instance;
    }
}

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
    private readonly List<NavigationItem> _navItems;
    private readonly NavigationItem _translationNav;
    private readonly DispatcherTimer _successStatusTimer;

    /// <summary>尺寸比较容差：吸收不同 DPI 下 Bounds 的取整漂移，偏差小于该值视为未变化。</summary>
    private const double SizeEpsilon = 1.0;

    public SettingsWindow(
        Func<AppConfig> current,
        Action<AppConfig> save,
        Func<ProviderProfile, string?, Task<string>> testConnection,
        Action<HotkeyRecordingState>? suspendGlobalHotkey = null,
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null,
        Func<string, IProgress<string>, CancellationToken, Task>? downloadPhoneticPack = null,
        IFavoriteService? favoriteService = null,
        Func<string?>? templateVersion = null,
        SettingsTab initialTab = SettingsTab.General)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        Icon = ApplicationIcon.Load();
        TitleBarIcon.Source = ApplicationIcon.LoadBitmap();
        ThemeManager.ApplyWindowChrome(this);

        var snapshot = current();
        ArgumentNullException.ThrowIfNull(snapshot);
        _currentConfig = current;
        _working = AppConfigCloner.Clone(snapshot);
        _save = save;
        _successStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _successStatusTimer.Tick += OnSuccessStatusTimerTick;
        Width = _working.Windows.SettingsWidth;
        Height = _working.Windows.SettingsHeight;

        // 核心性能重构：5 个 Tab 页面按需延迟加载，绝不在构造函数中全量同步初始化
        _translationNav = new NavigationItem("翻译", "\uE8C1", () => new TranslationPage { TestConnection = testConnection });
        _navItems =
        [
            new NavigationItem("通用", "\uE713", () => new GeneralPage(downloadDependencies, downloadPhoneticPack)),
            _translationNav,
            new NavigationItem("热键", "\uE765", () =>
            {
                var hotkeys = new HotkeyPage();
                hotkeys.RecordingChanged += (_, state) => suspendGlobalHotkey?.Invoke(state);
                return hotkeys;
            }),
            new NavigationItem("生词本", "\uE734", () => new FavoritesPage(favoriteService)),
            new NavigationItem("关于", "\uE946", () => new AboutPage { TemplateVersionProvider = templateVersion })
        ];

        NavigationList.ItemsSource = _navItems;
        NavigationList.SelectionChanged += OnNavigationChanged;

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();

        SelectTab(initialTab);

        // 关闭时记录窗口尺寸（仅在实际改变过尺寸时才写盘）；尺寸走独立副本，不携带尚未保存的页面编辑。
        Closed += (_, _) =>
        {
            _successStatusTimer.Stop();
            SaveWindowSize();
        };
    }

    private void OnTitleBarPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeButtonClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>按枚举切换当前选中的设置标签页。</summary>
    public void SelectTab(SettingsTab tab)
    {
        var targetIndex = (int)tab;
        if (targetIndex >= 0 && targetIndex < _navItems.Count)
        {
            NavigationList.SelectedIndex = targetIndex;
        }
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not NavigationItem navItem)
        {
            return;
        }

        var page = navItem.GetOrCreatePage(_working);
        // 页面延迟创建；返回通用页时同步尚未保存的流程选择，不重载其它编辑项。
        if (page is GeneralPage general && _translationNav.IsCreated)
        {
            var translation = (TranslationPage)_translationNav.GetOrCreatePage(_working);
            general.SetPipelineMode(translation.PipelineMode);
        }

        PageTitle.Text = navItem.Title;
        PageHost.Content = page.View;
        FooterStatus.Clear();
    }

    private void Save()
    {
        foreach (var navItem in _navItems)
        {
            if (!navItem.IsCreated)
            {
                continue;
            }

            var page = navItem.GetOrCreatePage(_working);
            if (page.Validate() is not { } error)
            {
                continue;
            }

            // 校验失败时切到出错的页面并就地提示，用户不必猜是哪一页的字段有问题。
            SelectPage(page);
            FooterStatus.Show(error, InfoSeverity.Warning);
            return;
        }

        foreach (var navItem in _navItems)
        {
            if (navItem.IsCreated)
            {
                navItem.GetOrCreatePage(_working).Apply(_working);
            }
        }

        var active = _working.Profiles.First(profile => string.Equals(profile.Name, _working.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_working.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase) && !active.SupportsVision)
        {
            SelectTab(SettingsTab.Translation);
            FooterStatus.Show("vision_oneshot 需要当前配置档声明支持图像输入", InfoSeverity.Warning);
            return;
        }

        try
        {
            // 提交独立快照，避免后续页面编辑直接修改宿主持有的已提交配置。
            _save(AppConfigCloner.Clone(_working));
            if (_translationNav.IsCreated && _translationNav.GetOrCreatePage(_working) is TranslationPage translationPage)
            {
                translationPage.SaveSecrets();
            }
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
        FooterStatus.Show("设置已保存", InfoSeverity.Success);
        _successStatusTimer.Stop();
        _successStatusTimer.Start();
    }

    private void OnSuccessStatusTimerTick(object? sender, EventArgs e)
    {
        _successStatusTimer.Stop();
        // 只有当前仍是本次保存产生的成功提示时才清除，避免覆盖后续的校验或错误信息。
        if (FooterStatus.Severity == InfoSeverity.Success && FooterStatus.Message == "设置已保存")
        {
            FooterStatus.Clear();
        }
    }

    private void SelectPage(ISettingsPage page)
    {
        for (var i = 0; i < _navItems.Count; i++)
        {
            if (_navItems[i].IsCreated && ReferenceEquals(_navItems[i].GetOrCreatePage(_working), page))
            {
                NavigationList.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>关闭时把实际窗口尺寸写回配置，下次打开按上次大小恢复。</summary>
    private void SaveWindowSize()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        // 始终以最新已提交配置为基线，避免后续被拒绝的编辑或旧快照随尺寸写回。
        var baseline = _currentConfig();
        if (ResolvePersistedSize(baseline.Windows, Bounds.Width, Bounds.Height, MinWidth, MinHeight) is not { } size)
        {
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
    /// 尺寸记忆的纯决策：返回需要写回的窗口尺寸，没有任何变化时返回 null 表示不必写盘。
    /// 尺寸不可信（布局尚未完成、窗口从未显示）或与基线一致时跳过。
    /// </summary>
    internal static (double Width, double Height)? ResolvePersistedSize(
        WindowConfig baseline, double actualWidth, double actualHeight, double minWidth, double minHeight)
    {
        if (!double.IsFinite(actualWidth) || !double.IsFinite(actualHeight) ||
            actualWidth <= 0 || actualHeight <= 0)
        {
            return null;
        }

        // 以声明的最小尺寸为下限，防止异常数据把窗口缩到不可操作。
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
