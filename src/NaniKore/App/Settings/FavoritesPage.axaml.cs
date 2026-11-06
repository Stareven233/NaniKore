using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Diagnostics;
using NaniKore.Favorites;

namespace NaniKore.App.Settings;

internal sealed partial class FavoritesPage : UserControl, ISettingsPage
{
    internal static readonly string[] CategoryFilters = ["全部", "单词", "短语", "语法", "备注"];
    private static readonly string[] LanguageFilters = ["全部", .. LanguageChoices.All.Select(choice => choice.Name)];

    private readonly IFavoriteService? _favoriteService;
    private List<FavoriteItem> _items = [];
    private readonly ObservableCollection<object> _entries = [];

    public FavoritesPage(IFavoriteService? favoriteService = null)
    {
        InitializeComponent();
        _favoriteService = favoriteService;

        CategoryBox.ItemsSource = CategoryFilters;
        LanguageBox.ItemsSource = LanguageFilters;

        CategoryBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex = 0;

        CategoryBox.SelectionChanged += (_, _) => ApplyFilter();
        LanguageBox.SelectionChanged += (_, _) => ApplyFilter();
        SearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ApplyFilter();
            }
        };
        RefreshButton.Click += OnRefreshClick;
        CopyAllButton.Click += OnCopyAllClick;
        CopyPathButton.Click += OnCopyPathClick;

        DatabasePathText.Text = _favoriteService is SqliteFavoriteService sqlite
            ? sqlite.DatabasePath
            : "收藏服务不可用。";

        if (_favoriteService is null)
        {
            EmptyHint.Text = "收藏服务不可用，请在应用中重新打开设置。";
        }

        FavoriteList.ItemsSource = _entries;

        // 事件路由的隧道阶段会先于任何控件抢焦点执行：点卡片外立即提交，
        // 不依赖后续焦点到底落到哪个控件（点列表空白、非可聚焦区域也能收起）。
        AddHandler(PointerPressedEvent, OnPagePointerPressed, RoutingStrategies.Tunnel);

        Unloaded += (_, _) => DetachService();
    }

    public string Title => "收藏";

    public string Glyph => "\uE734";

    public Control View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AttachService();
        Refresh();
    }

    public string? Validate() => null;

    public void Apply(AppConfig config)
    {
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
        _favoriteService.FavoriteUpdated -= OnFavoriteUpdated;
        _favoriteService.FavoriteUpdated += OnFavoriteUpdated;
    }

    private void DetachService()
    {
        if (_favoriteService is null)
        {
            return;
        }

        _favoriteService.FavoriteAdded -= OnFavoriteAdded;
        _favoriteService.FavoriteRemoved -= OnFavoriteRemoved;
        _favoriteService.FavoriteUpdated -= OnFavoriteUpdated;
    }

    private void OnFavoriteAdded(object? sender, FavoriteItem e) => PostRefresh();

    private void OnFavoriteRemoved(object? sender, string e) => PostRefresh();

    private void OnFavoriteUpdated(object? sender, FavoriteItem e) => PostRefresh();

    private void PostRefresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        Refresh();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        Refresh();
        Status.Show("已刷新收藏列表。", InfoSeverity.Neutral);
    }

    private void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        var path = _favoriteService is SqliteFavoriteService sqlite
            ? sqlite.DatabasePath
            : DatabasePathText.Text;
        if (string.IsNullOrWhiteSpace(path) || path == "收藏服务不可用。")
        {
            Status.Show("数据库路径不可用。", InfoSeverity.Warning);
            return;
        }

        CopyTextToClipboard(path, "已复制数据库路径。");
    }

    private void Refresh()
    {
        _items = _favoriteService?.GetAllFavorites().ToList() ?? [];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var category = CategoryBox.SelectedItem as string ?? "全部";
        var language = LanguageBox.SelectedItem as string ?? "全部";
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;

        var filtered = _items.Where(item =>
            (category == "全部" || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (language == "全部" || string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase)) &&
            (keyword.Length == 0 ||
             item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
             (item.Note?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();

        CountText.Text = filtered.Count == _items.Count
            ? $"共 {_items.Count} 条收藏"
            : $"筛选命中 {filtered.Count} / {_items.Count} 条";
        EmptyHint.IsVisible = filtered.Count == 0;

        _entries.Clear();
        foreach (var item in filtered)
        {
            _entries.Add(new FavoriteDisplayViewModel(item));
        }
    }

    private void OnCopyItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteDisplayViewModel vm })
        {
            return;
        }

        CopyToClipboard(FormatEntry(vm.Item), 1);
    }

    private void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        var entries = _entries.OfType<FavoriteDisplayViewModel>().ToList();
        if (entries.Count == 0)
        {
            Status.Show("当前没有可复制的收藏。", InfoSeverity.Warning);
            return;
        }

        var text = string.Join("\r\n\r\n", entries.Select(vm => FormatEntry(vm.Item)));
        CopyToClipboard(text, entries.Count);
    }

    private async void OnDeleteItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteDisplayViewModel vm } || _favoriteService is null)
        {
            return;
        }

        var removed = await _favoriteService.RemoveFavoriteAsync(vm.Item.Content);
        if (removed)
        {
            Status.Show($"已删除「{vm.Item.Content}」。", InfoSeverity.Success);
        }
        else
        {
            Status.Show("删除失败：条目可能已被移除。", InfoSeverity.Warning);
        }

        Refresh();
    }

    private void OnEditItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteDisplayViewModel vm })
        {
            return;
        }

        BeginEdit(vm.Item);
    }

    internal void BeginEdit(FavoriteItem item)
    {
        FavoriteDisplayViewModel? display = null;
        var index = -1;
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is FavoriteDisplayViewModel candidate && candidate.Item.Id == item.Id)
            {
                display = candidate;
                index = i;
                break;
            }
        }

        if (index < 0 || display is null)
        {
            return;
        }

        _entries[index] = new FavoriteEditViewModel(display.Item);
    }

    private void OnEditContentAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    /// <summary>编辑态失焦提交的轮询间隔与次数：等下拉展开造成的焦点漂移落定（约 1 秒封顶）。</summary>
    private static readonly TimeSpan EditFocusSettleDelay = TimeSpan.FromMilliseconds(40);
    private const int EditFocusSettleAttempts = 25;

    private async void OnEditFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control field || field.FindAncestorOfType<Border>() is not { } card)
        {
            return;
        }

        await CommitEditWhenFocusLeavesAsync(card);
    }

    /// <summary>
    /// 指针按下（隧道阶段）即判定去向：点在编辑卡片外就立刻提交，不依赖焦点最终落到哪个控件，
    /// 所以点其他收藏条目、列表空白这类不可聚焦区域也能收起。
    /// </summary>
    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (EditCard is not { } card || e.Source is not Visual source)
        {
            return;
        }

        if (IsInsideCard(card, source))
        {
            return;
        }

        // 离开编辑框（含点其他收藏、列表空白、顶部筛选栏）→ 立即收起并保存。
        _ = CommitEditAsync(card);
    }

    /// <summary>自测钩子：直接驱动指针去留判定，无需真实鼠标。</summary>
    internal void OnPagePointerPressedForTest(PointerPressedEventArgs e) => OnPagePointerPressed(this, e);

    /// <summary>当前处于编辑态的卡片（同一时间最多一张）。</summary>
    private Border? EditCard =>
        FavoriteList.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.DataContext is FavoriteEditViewModel);

    /// <summary>事件源是否落在编辑卡片内（沿可视/逻辑祖先向上，可跨 popup 边界命中徽标与输入框）。</summary>
    private static bool IsInsideCard(Border card, Visual source)
    {
        // 逻辑树可能因模板化控件形成回遥，用访问集保证遍历终止。
        var visited = new HashSet<Visual>(ReferenceEqualityComparer.Instance);
        for (Visual? current = source; current is not null && visited.Add(current); current = NextAncestor(current))
        {
            if (ReferenceEquals(current, card))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>下一个祖先：popup 根节点的可视父级为空，需沿逻辑父级回到宿主控件才能命中卡片。</summary>
    private static Visual? NextAncestor(Visual visual) =>
        visual.GetVisualParent() ?? visual.GetLogicalParent() as Visual;

    /// <summary>
    /// 编辑卡片真正失去键盘焦点后才提交。
    /// 点开类别/语言下拉时，焦点会从输入框转移到弹出列表（甚至是独立 popup 窗口），
    /// 此时若立即提交会刷新列表并拆掉正在展开的下拉——这正是「下拉框点不开」的根因。
    /// 因此下拉展开期间只等待不提交：焦点回到编辑区即视为仍在编辑，超时后按最终状态兜底一次。
    /// </summary>
    internal async Task CommitEditWhenFocusLeavesAsync(Border card)
    {
        for (var attempt = 0; attempt < EditFocusSettleAttempts; attempt++)
        {
            await Task.Delay(EditFocusSettleDelay);

            if (!card.GetVisualAncestors().Any())
            {
                // 卡片已被刷新/替换（例如别处触发了提交），无需再处理。
                return;
            }

            if (IsEditDropDownOpen(card))
            {
                continue;
            }

            if (card.IsKeyboardFocusWithin)
            {
                // 焦点回到编辑区（含下拉选完值后回归），保持编辑态。
                return;
            }

            await CommitEditAsync(card);
            return;
        }

        if (!IsEditDropDownOpen(card) && !card.IsKeyboardFocusWithin)
        {
            await CommitEditAsync(card);
        }
    }

    /// <summary>编辑卡片内是否有下拉列表正处于展开状态（展开期间不得提交，否则会拆掉正在展开的下拉）。</summary>
    internal static bool IsEditDropDownOpen(Border card) =>
        card.GetVisualDescendants().OfType<ComboBox>().Any(box => box.IsDropDownOpen);

    /// <summary>最近一次下拉是否由选中项导致收起：按 ComboBox 实例记录，用于决定是否把焦点收回下拉框本身。</summary>
    private readonly HashSet<ComboBox> _editComboBoxSelections = [];

    private void OnEditComboBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 只有展开中的下拉发生选中才算“用户选择”；模板初次绑定 SelectedItem 时不得置位。
        if (sender is ComboBox { IsDropDownOpen: true } box)
        {
            _editComboBoxSelections.Add(box);
        }
    }

    private void OnEditComboBoxDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox box || box.FindAncestorOfType<Border>() is not { } card ||
            card.DataContext is not FavoriteEditViewModel)
        {
            return;
        }

        var selected = _editComboBoxSelections.Remove(box);

        // 非可编辑 ComboBox 收起后不会自动归还焦点（选完项/按 Esc 后焦点会悬在已销毁的弹层里）。
        // 仅当焦点没有落到可用的外部控件时，才把焦点收回下拉框，避免把“点到卡片外”也当成延续编辑。
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (selected || focused is null || TopLevel.GetTopLevel(focused) is null)
        {
            box.Focus();
        }

        // 下拉收起本身可能是“点了卡片外”的收尾动作；重新盯一次焦点，
        // 避免编辑框在下拉长时间展开、上一轮轮询超时后漏掉这次提交。
        _ = CommitEditWhenFocusLeavesAsync(card);
    }

    private void OnEditFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control field || field.FindAncestorOfType<Border>() is not { } card)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelEdit(card);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _ = CommitEditAsync(card);
            e.Handled = true;
        }
    }

    private void CancelEdit(Border card)
    {
        if (card.DataContext is not FavoriteEditViewModel editVm)
        {
            return;
        }

        var index = _entries.IndexOf(editVm);
        if (index < 0)
        {
            return;
        }

        _entries[index] = new FavoriteDisplayViewModel(editVm.Item);
    }

    private Task CommitEditAsync(Border card)
    {
        if (card.DataContext is not FavoriteEditViewModel editVm)
        {
            return Task.CompletedTask;
        }

        return SaveEditAsync(
            editVm.Item,
            editVm.EditContent ?? string.Empty,
            editVm.EditNote,
            editVm.EditCategory,
            editVm.EditLanguage);
    }

    internal async Task<bool> SaveEditAsync(FavoriteItem item, string content, string? note, string category, string language)
    {
        if (_favoriteService is null)
        {
            Status.Show("收藏服务不可用，无法保存。", InfoSeverity.Error);
            return false;
        }

        var trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            Status.Show("内容不能为空，已保留编辑状态。", InfoSeverity.Warning);
            return false;
        }

        var normalized = FavoriteContentNormalizer.Normalize(trimmed);

        if (_items.Any(x => x.Id != item.Id && FavoriteContentNormalizer.Equals(x.Content, normalized)))
        {
            Status.Show($"已存在相同内容的收藏（{normalized}），保存被拒绝。", InfoSeverity.Warning);
            return false;
        }

        item.Category = string.IsNullOrWhiteSpace(category) ? "单词" : category;
        item.Content = trimmed;
        item.Note = string.IsNullOrWhiteSpace(note) ? null : note;
        item.Language = string.IsNullOrWhiteSpace(language) ? "日本語" : language;

        var saved = await _favoriteService.UpdateFavoriteAsync(item);
        if (!saved)
        {
            Status.Show("保存失败：条目可能已被移除，或与另一条收藏内容重复。", InfoSeverity.Warning);
            return false;
        }

        Status.Show($"已保存「{item.Content}」。", InfoSeverity.Success);
        Refresh();
        return true;
    }

    private static string FormatEntry(FavoriteItem item)
    {
        var builder = new StringBuilder();
        var language = string.IsNullOrWhiteSpace(item.Language) ? "日本語" : item.Language;
        builder.AppendLine($"[{item.Category} | {language}] {item.Content}");
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            builder.AppendLine(item.Note);
        }

        return builder.ToString().TrimEnd();
    }

    private void CopyToClipboard(string text, int count) =>
        CopyTextToClipboard(text, count == 1 ? "已复制该条收藏。" : $"已复制 {count} 条收藏。");

    private async void CopyTextToClipboard(string text, string successMessage)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(transfer);
            }

            Status.Show(successMessage, InfoSeverity.Success);
        }
        catch (Exception exception)
        {
            Status.Show($"复制失败：{exception.Message}", InfoSeverity.Error);
        }
    }
}

internal sealed class FavoriteDisplayViewModel(FavoriteItem item)
{
    public FavoriteItem Item { get; } = item;

    public string FormattedCreatedAt => Item.CreatedAt.ToString("yyyy-MM-dd HH:mm");
}

internal sealed class FavoriteEditViewModel
{
    public FavoriteEditViewModel(FavoriteItem item)
    {
        Item = item;
        EditContent = item.Content;
        EditNote = item.Note ?? string.Empty;
        EditCategory = item.Category;
        EditLanguage = string.IsNullOrWhiteSpace(item.Language) ? "日本語" : item.Language;
    }

    public FavoriteItem Item { get; }

    public string EditContent { get; set; }

    public string EditNote { get; set; }

    public string EditCategory { get; set; }

    public string EditLanguage { get; set; }

    public IReadOnlyList<string> CategoryOptions { get; } =
        FavoritesPage.CategoryFilters.Skip(1).ToArray();

    public IReadOnlyList<string> LanguageOptions { get; } =
        LanguageChoices.All.Select(choice => choice.Name).ToArray();
}
