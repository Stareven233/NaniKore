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
/// 导航列表项视图模型，完全兼容编译绑定。
/// </summary>
internal sealed class NavigationItem(ISettingsPage page)
{
    public ISettingsPage Page { get; } = page;
    public string Title => Page.Title;
    public string Glyph => Page.Glyph;
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
    private readonly List<ISettingsPage> _pages;
    private readonly List<NavigationItem> _navItems;
    private readonly TranslationPage _translationPage;
    private readonly DispatcherTimer _successStatusTimer;

    /// <summary>业务配置是否已成功落盘；决定关闭时尺寸记录克隆哪份副本。</summary>
    private bool _settingsSaved;

    /// <summary>尺寸比较容差：吸收不同 DPI 下 Bounds 的取整漂移，偏差小于该值视为未变化。</summary>
    private const double SizeEpsilon = 1.0;

    public SettingsWindow(
        Func<AppConfig> current,
        Action<AppConfig> save,
        Func<ProviderProfile, string?, Task<string>> testConnection,
        Action<HotkeyRecordingState>? suspendGlobalHotkey = null,
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null,
        Func<string, IProgress<string>, CancellationToken, Task>? downloadPhoneticDictionary = null,
        IFavoriteService? favoriteService = null,
        Func<string?>? templateVersion = null,
        SettingsTab initialTab = SettingsTab.General)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        Icon = ApplicationIcon.Load();
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

        var general = new GeneralPage(downloadDependencies, downloadPhoneticDictionary);
        _translationPage = new TranslationPage { TestConnection = testConnection };
        var hotkeys = new HotkeyPage();
        // 录制期间必须临时注销正在录制的那个全局热键，否则系统会把按键交给 NaniKore 自己的翻译流程。
        hotkeys.RecordingChanged += (_, state) => suspendGlobalHotkey?.Invoke(state);
        var about = new AboutPage { TemplateVersionProvider = templateVersion };
        // 收藏页读应用级收藏服务（SQLite 持久化），不参与 AppConfig 编辑副本。
        var favorites = new FavoritesPage(favoriteService);
        _pages = [general, _translationPage, hotkeys, favorites, about];
        _navItems = _pages.Select(page => new NavigationItem(page)).ToList();

        NavigationList.ItemsSource = _navItems;
        NavigationList.SelectionChanged += OnNavigationChanged;

        foreach (var page in _pages)
        {
            page.Load(_working);
        }

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

        PageTitle.Text = navItem.Title;
        PageHost.Content = navItem.Page.View;
        FooterStatus.Clear();
    }

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

        var active = _working.Profiles.First(profile => string.Equals(profile.Name, _working.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
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
    /// 关闭时尺寸回写的基线决策：已保存过业务配置时以编辑副本 <paramref name="working"/> 为准
    /// （其内容已随保存落盘，只叠加尺寸）；未保存时必须取 <paramref name="latest"/> 实时配置，
    /// 避免打开时的快照覆盖其他入口期间的写盘。未保存的页面编辑只存在于 <paramref name="working"/>，
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
