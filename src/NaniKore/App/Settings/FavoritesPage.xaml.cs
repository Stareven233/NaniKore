using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Favorites;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

/// <summary>
/// 收藏管理页：类别筛选、关键词搜索、卡片列表、单条复制与删除、一键复制全部。
/// 数据由长期持有的 <see cref="IFavoriteService"/> 提供，不写入 <see cref="AppConfig"/>；
/// 因此 <see cref="Validate"/> 恒为空、<see cref="Apply"/> 为空操作，宿主“保存”不触及收藏。
/// </summary>
internal sealed partial class FavoritesPage : UserControl, ISettingsPage
{
    /// <summary>类别筛选项：全部 + 四个收藏类别（与 favorites.category 取值一致）。</summary>
    private static readonly string[] CategoryFilters = ["全部", "单词", "短语", "语法", "备注"];

    private readonly IFavoriteService? _favoriteService;
    private List<FavoriteItem> _items = [];

    public FavoritesPage(IFavoriteService? favoriteService = null)
    {
        InitializeComponent();
        _favoriteService = favoriteService;

        foreach (var category in CategoryFilters)
        {
            CategoryBox.Items.Add(category);
        }

        CategoryBox.SelectedIndex = 0;
        DatabasePathText.Text = _favoriteService is SqliteFavoriteService sqlite
            ? sqlite.DatabasePath
            : "收藏服务不可用。";

        if (_favoriteService is null)
        {
            EmptyHint.Text = "收藏服务不可用，请在应用中重新打开设置。";
        }

        // 设置窗可反复开关，而收藏服务是应用级长期实例：必须在页面卸载时退订，
        // 否则每次开关设置都会往服务上堆叠一个已废弃页面的事件处理器。
        Unloaded += (_, _) => DetachService();
    }

    public string Title => "收藏";

    /// <summary>Segoe Fluent Icons 星形轮廓，与结果卡未收藏态图标一致。</summary>
    public string Glyph => "\uE734";

    public FrameworkElement View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AttachService();
        Refresh();
    }

    public string? Validate() => null;

    public void Apply(AppConfig config)
    {
        // 收藏数据不经配置系统持久化，本页对宿主编辑副本没有任何可回写字段。
        ArgumentNullException.ThrowIfNull(config);
    }

    private void AttachService()
    {
        if (_favoriteService is null)
        {
            return;
        }

        _favoriteService.FavoriteAdded -= OnFavoriteAdded;
        _favoriteService.FavoriteAdded += OnFavoriteAdded;
        _favoriteService.FavoriteRemoved -= OnFavoriteRemoved;
        _favoriteService.FavoriteRemoved += OnFavoriteRemoved;
    }

    private void DetachService()
    {
        if (_favoriteService is null)
        {
            return;
        }

        _favoriteService.FavoriteAdded -= OnFavoriteAdded;
        _favoriteService.FavoriteRemoved -= OnFavoriteRemoved;
    }

    /// <summary>结果卡/划词气泡的收藏变更经事件回流，与本页刷新按钮最终都汇聚到同一路径。</summary>
    private void OnFavoriteAdded(object? sender, FavoriteItem e) => PostRefresh();

    private void OnFavoriteRemoved(object? sender, string e) => PostRefresh();

    /// <summary>跨线程（或未来异步持久化）变更时切回 UI 线程再刷新。</summary>
    private void PostRefresh()
    {
        if (!CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }

        Refresh();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Refresh();
        Status.Show("已刷新收藏列表。", InfoSeverity.Neutral);
    }

    /// <summary>从服务拉取全量快照，再按当前筛选条件呈现。</summary>
    private void Refresh()
    {
        _items = _favoriteService?.GetAllFavorites().ToList() ?? [];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var category = CategoryBox.SelectedItem as string ?? "全部";
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;

        var filtered = _items.Where(item =>
            (category == "全部" || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (keyword.Length == 0 ||
             item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
             (item.Note?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();

        CountText.Text = filtered.Count == _items.Count
            ? $"共 {_items.Count} 条收藏"
            : $"筛选命中 {filtered.Count} / {_items.Count} 条";
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoriteList.ItemsSource = filtered;
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteItem item })
        {
            return;
        }

        CopyToClipboard(FormatEntry(item), 1);
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var entries = FavoriteList.Items.Cast<FavoriteItem>().ToList();
        if (entries.Count == 0)
        {
            Status.Show("当前没有可复制的收藏。", InfoSeverity.Warning);
            return;
        }

        // 每条之间空行分隔，粘贴到记事本/聊天窗后仍然保持条目边界。
        var text = string.Join("\r\n\r\n", entries.Select(FormatEntry));
        CopyToClipboard(text, entries.Count);
    }

    private async void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteItem item } || _favoriteService is null)
        {
            return;
        }

        var removed = await _favoriteService.RemoveFavoriteAsync(item.Content);
        if (removed)
        {
            Status.Show($"已删除「{item.Content}」。", InfoSeverity.Success);
        }
        else
        {
            Status.Show("删除失败：条目可能已被移除。", InfoSeverity.Warning);
        }

        // 事件回调通常已触发刷新；兜底再刷一次保证列表与服务状态一致。
        Refresh();
    }

    /// <summary>单条纯文本格式：核心文本一行，note 分层原样跟行，复制出去免去二次整理。</summary>
    private static string FormatEntry(FavoriteItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{item.Category}] {item.Content}");
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            builder.AppendLine(item.Note);
        }

        return builder.ToString().TrimEnd();
    }

    private void CopyToClipboard(string text, int count)
    {
        try
        {
            Clipboard.SetText(text);
            Status.Show(count == 1 ? "已复制该条收藏。" : $"已复制 {count} 条收藏。", InfoSeverity.Success);
        }
        catch (Exception exception)
        {
            // 极少数宿主环境会拒绝剪贴板访问；原地提示而不是弹窗打断管理流程。
            Status.Show($"复制失败：{exception.Message}", InfoSeverity.Error);
        }
    }
}
