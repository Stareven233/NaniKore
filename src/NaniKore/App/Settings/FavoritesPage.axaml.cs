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

    private void OnEditFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control field || field.FindAncestorOfType<Border>() is not { } card)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = CommitEditAsync(card, skipWhenFocusWithin: true), DispatcherPriority.Input);
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
            _ = CommitEditAsync(card, skipWhenFocusWithin: false);
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

    private Task CommitEditAsync(Border card, bool skipWhenFocusWithin)
    {
        if (card.DataContext is not FavoriteEditViewModel editVm)
        {
            return Task.CompletedTask;
        }

        if (skipWhenFocusWithin && card.IsKeyboardFocusWithin)
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
