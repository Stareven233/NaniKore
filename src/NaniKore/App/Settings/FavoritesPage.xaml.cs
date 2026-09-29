using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Favorites;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

/// <summary>
/// 收藏管理页：横向导航条筛选、数据库路径单行、卡片列表直铺与就地编辑。
/// 数据由长期持有的 <see cref="IFavoriteService"/> 提供，不写入 <see cref="AppConfig"/>；
/// 因此 <see cref="Validate"/> 恒为空、<see cref="Apply"/> 为空操作，宿主“保存”不触及收藏。
/// </summary>
internal sealed partial class FavoritesPage : UserControl, ISettingsPage
{
    /// <summary>类别筛选项：全部 + 四个收藏类别（与 favorites.category 取值一致）。</summary>
    internal static readonly string[] CategoryFilters = ["全部", "单词", "短语", "语法", "备注"];

    /// <summary>语言筛选项：全部 + 共享语言列表（母语写法，与设置页目标语言/划词气泡下拉同一份）。</summary>
    private static readonly string[] LanguageFilters = ["全部", .. LanguageChoices.All.Select(choice => choice.Name)];

    private readonly IFavoriteService? _favoriteService;

    /// <summary>服务快照（筛选源）。</summary>
    private List<FavoriteItem> _items = [];

    /// <summary>列表绑定源：展示态/编辑态视图模型混排，由 <see cref="FavoriteTemplateSelector"/> 切换模板。</summary>
    private readonly List<object> _entries = [];

    public FavoritesPage(IFavoriteService? favoriteService = null)
    {
        InitializeComponent();
        _favoriteService = favoriteService;

        foreach (var category in CategoryFilters)
        {
            CategoryBox.Items.Add(category);
        }

        foreach (var language in LanguageFilters)
        {
            LanguageBox.Items.Add(language);
        }

        CategoryBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex = 0;
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

    /// <summary>结果卡/划词气泡的收藏变更经事件回流，与本页刷新按钮最终都汇聚到同一路径。</summary>
    private void OnFavoriteAdded(object? sender, FavoriteItem e) => PostRefresh();

    private void OnFavoriteRemoved(object? sender, string e) => PostRefresh();

    /// <summary>就地编辑保存成功后同样刷新：编辑态视图模型随之还原为展示态（issue 015 / 2.5）。</summary>
    private void OnFavoriteUpdated(object? sender, FavoriteItem e) => PostRefresh();

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

    /// <summary>复制数据库绝对路径到剪贴板（issue 015 / 2.1：单行 + 图标按钮，长说明降级为 ToolTip）。</summary>
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
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

    /// <summary>从服务拉取全量快照，再按当前筛选条件呈现。</summary>
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
            // 语言筛选与类别、关键词三者 AND；旧库行经迁移回填「日本語」后同样可被筛中。
            (language == "全部" || string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase)) &&
            (keyword.Length == 0 ||
             item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
             (item.Note?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();

        CountText.Text = filtered.Count == _items.Count
            ? $"共 {_items.Count} 条收藏"
            : $"筛选命中 {filtered.Count} / {_items.Count} 条";
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _entries.Clear();
        foreach (var item in filtered)
        {
            _entries.Add(new FavoriteDisplayViewModel(item));
        }

        RebindEntries();
    }

    /// <summary>重新挂载绑定源：ItemsControl 的列表内容变化需要重新指一次才会重排容器。</summary>
    private void RebindEntries()
    {
        FavoriteList.ItemsSource = null;
        FavoriteList.ItemsSource = _entries;
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteDisplayViewModel vm })
        {
            return;
        }

        CopyToClipboard(FormatEntry(vm.Item), 1);
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var entries = FavoriteList.Items.OfType<FavoriteDisplayViewModel>().ToList();
        if (entries.Count == 0)
        {
            Status.Show("当前没有可复制的收藏。", InfoSeverity.Warning);
            return;
        }

        // 每条之间空行分隔，粘贴到记事本/聊天窗后仍然保持条目边界。
        var text = string.Join("\r\n\r\n", entries.Select(vm => FormatEntry(vm.Item)));
        CopyToClipboard(text, entries.Count);
    }

    private async void OnDeleteItemClick(object sender, RoutedEventArgs e)
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

        // 事件回调通常已触发刷新；兜底再刷一次保证列表与服务状态一致。
        Refresh();
    }

    /// <summary>进入就地编辑态：条目卡片切换为编辑模板，内容框自动聚焦（issue 015 / 2.5）。</summary>
    private void OnEditItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteDisplayViewModel vm })
        {
            return;
        }

        BeginEdit(vm.Item);
    }

    /// <summary>把指定条目切换为就地编辑态（内部入口，同时供无 UI 自测驱动编辑往返）。</summary>
    internal void BeginEdit(FavoriteItem item)
    {
        // 按主键匹配而不是引用：SQLite 读出的快照与列表绑定的条目是不同实例。
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
        RebindEntries();
    }

    /// <summary>编辑态内容框就绪即聚焦并全选，减少一次点击。</summary>
    private void OnEditContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>
    /// 失焦即保存：焦点仍在同一张卡片内（切换到另一编辑字段/点开下拉）时不算失焦；
    /// 真正离开卡片才提交。判定延迟到输入优先级执行，避开下拉弹窗期间焦点短暂外移的误保存。
    /// </summary>
    private async void OnEditFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement field || FindAncestor<Border>(field) is not { } card)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            () => _ = CommitEditAsync(card, skipWhenFocusWithin: true),
            DispatcherPriority.Input);
    }

    /// <summary>编辑态键盘快捷路径：Esc 取消还原，Enter 提交（失焦保存之外的显式触发）。</summary>
    private void OnEditFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement field || FindAncestor<Border>(field) is not { } card)
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
            // 显式提交不受焦点在卡片内的抑制；Enter 落在多行备注框时交给 TextBox 自行换行。
            _ = CommitEditAsync(card, skipWhenFocusWithin: false);
            e.Handled = true;
        }
    }

    /// <summary>取消编辑：还原为展示态视图模型，丢弃未保存的修改。</summary>
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
        RebindEntries();
    }

    /// <summary>
    /// 提交编辑：先做归一化冲突预检（命中另一行即拒绝并留在编辑态），再落库更新。
    /// 成功后刷新列表，编辑态随 FavoriteUpdated 事件与兜底刷新一并还原为展示态。
    /// </summary>
    private Task CommitEditAsync(Border card, bool skipWhenFocusWithin)
    {
        if (card.DataContext is not FavoriteEditViewModel editVm)
        {
            return Task.CompletedTask;
        }

        // 焦点还在卡片内说明用户只是切换到另一字段/下拉，不当成失焦保存。
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

    /// <summary>
    /// 编辑保存主链路（issue 015 / 2.5）：归一化冲突预检 → 服务层更新 → 状态提示 → 刷新列表。
    /// 失焦与 Enter 两条交互路径共用；返回 false 表示拒绝或失败（调用方保留编辑态）。
    /// </summary>
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

        // 冲突预检：归一化后命中另一行（Id 不同）→ 拒绝保存，不静默合并、不覆盖。
        // 判据与服务层 NormalizedKeyTakenByOtherRow 完全一致，给用户即时的原地反馈。
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
        // FavoriteUpdated 事件通常会触发刷新；显式再刷一次保证编辑原子落地。
        Refresh();
        return true;
    }

    /// <summary>单条纯文本格式：核心文本一行，note 分层原样跟行，复制出去免去二次整理。</summary>
    private static string FormatEntry(FavoriteItem item)
    {
        var builder = new StringBuilder();
        // 前缀带上语言（issue 015 / 2.3-C）：导出阅读时一眼可分语种，不占额外一行。
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

    /// <summary>剪贴板写入与提示的公共通道：收藏内容与数据库路径共用，异常时原地提示不弹窗。</summary>
    private void CopyTextToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            Status.Show(successMessage, InfoSeverity.Success);
        }
        catch (Exception exception)
        {
            // 极少数宿主环境会拒绝剪贴板访问；原地提示而不是弹窗打断管理流程。
            Status.Show($"复制失败：{exception.Message}", InfoSeverity.Error);
        }
    }

    /// <summary>沿视觉树上溯查找指定类型的祖先（编辑卡片容器定位用）。</summary>
    private static T? FindAncestor<T>(DependencyObject node)
        where T : DependencyObject
    {
        var current = node;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}

/// <summary>收藏条目模板选择器：编辑态视图模型切换到编辑模板，其余走展示模板。</summary>
internal sealed class FavoriteTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DisplayTemplate { get; set; }

    public DataTemplate? EditTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is FavoriteEditViewModel ? EditTemplate : DisplayTemplate;
}

/// <summary>收藏条目展示态视图模型。</summary>
internal sealed class FavoriteDisplayViewModel
{
    public FavoriteDisplayViewModel(FavoriteItem item) => Item = item;

    public FavoriteItem Item { get; }
}

/// <summary>
/// 收藏条目编辑态视图模型（issue 015 / 2.5）：就地编辑 content / note / category / language，
/// 失焦即保存；冲突（归一化后命中另一行）由页面预检拒绝，服务层另有兜底校验。
/// </summary>
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

    /// <summary>编辑态类别候选项（不含「全部」）。</summary>
    public IReadOnlyList<string> CategoryOptions { get; } =
        FavoritesPage.CategoryFilters.Skip(1).ToArray();

    /// <summary>编辑态语言候选项：共享语言列表母语写法（不含「全部」）。</summary>
    public IReadOnlyList<string> LanguageOptions { get; } =
        LanguageChoices.All.Select(choice => choice.Name).ToArray();
}
