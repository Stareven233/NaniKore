namespace NaniKore.App.Controls;

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NaniKore.App.Settings;
using NaniKore.Favorites;

/// <summary>
/// 正文自由划词微型悬浮收藏气泡。
/// 支持原地微调、分类选择、失焦自动关闭及根据字符串匹配即时显示已收藏状态。
/// </summary>
public partial class SelectionBubblePopup : UserControl
{
    private IFavoriteService? _favoriteService;
    private bool _isCurrentContentFavorited;

    /// <summary>收藏动作被触发时激发的事件。</summary>
    public event EventHandler<FavoriteItem>? FavoriteRequested;

    /// <summary>取消收藏动作被触发时激发的事件。</summary>
    public event EventHandler<string>? FavoriteRemoved;

    public SelectionBubblePopup()
    {
        InitializeComponent();

        // 语言下拉与设置页目标语言、收藏页筛选共用同一份共享列表（母语写法），永不漂移。
        foreach (var language in LanguageChoices.All)
        {
            LanguageComboBox.Items.Add(language);
        }

        LanguageComboBox.SelectedIndex = 0;
    }

    /// <summary>注入收藏服务以进行字符串匹配与状态更新。</summary>
    public void SetFavoriteService(IFavoriteService? service)
    {
        _favoriteService = service;
        UpdateFavoriteState();
    }

    /// <summary>紧贴鼠标划选松开位置（即当前光标处）弹出微型气泡，消除 DPI 缩放与多行文本偏移。</summary>
    public void ShowAtMouse(string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;

        CategoryComboBox.SelectedIndex = 0;

        // 打开前清理残留鼠标捕获，杜绝“划选残留捕获把后续点击导回文本框”的根因分支。
        ReleaseStaleCapture();

        BubblePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        BubblePopup.HorizontalOffset = 4;
        BubblePopup.VerticalOffset = 16;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>
    /// 紧贴指定目标元素与局部矩形区域弹出气泡（基于 DIP 逻辑坐标，自适应翻转防出界）。
    /// </summary>
    public void ShowAt(UIElement target, Rect targetRect, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;
        CategoryComboBox.SelectedIndex = 0;
        ReleaseStaleCapture();

        BubblePopup.PlacementTarget = target;
        BubblePopup.PlacementRectangle = targetRect;
        BubblePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        BubblePopup.HorizontalOffset = 0;
        BubblePopup.VerticalOffset = 4;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>以指定控件及相对坐标弹出微型气泡（优先使用 Relative，防止 DPI 缩放漂移）。</summary>
    public void ShowAt(UIElement target, Point relativePoint, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;

        CategoryComboBox.SelectedIndex = 0;
        ReleaseStaleCapture();

        BubblePopup.PlacementTarget = target;
        BubblePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        BubblePopup.HorizontalOffset = relativePoint.X;
        BubblePopup.VerticalOffset = relativePoint.Y + 4;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>在屏幕指定坐标弹出微型气泡（带 DPI 自动修正）。</summary>
    public void ShowAt(Point screenPoint, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;

        // 默认类别为“单词”
        CategoryComboBox.SelectedIndex = 0;
        ReleaseStaleCapture();

        BubblePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;

        var source = PresentationSource.FromVisual(this);
        var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        BubblePopup.HorizontalOffset = screenPoint.X / dpiX;
        BubblePopup.VerticalOffset = (screenPoint.Y / dpiY) + 4;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>主动关闭气泡。</summary>
    public void Hide()
    {
        BubblePopup.IsOpen = false;
    }

    /// <summary>
    /// 气泡是否持有键盘焦点（issue 015 / 1.1-B）：从 <see cref="Keyboard.FocusedElement"/> 沿逻辑树上溯，
    /// 命中本控件即算持有。逻辑树可跨 PopupRoot 的 HWND 边界，比 IsKeyboardFocusWithin
    /// （视觉树被 PopupRoot 截断）可靠——窗口 Deactivated 误判的延迟判定以此为据。
    /// </summary>
    public bool ContainsLogicalFocus() => ContainsLogicalSubtree(Keyboard.FocusedElement as DependencyObject);

    /// <summary>沿逻辑树判断节点是否位于本控件子树内（含控件自身）。</summary>
    private bool ContainsLogicalSubtree(DependencyObject? node)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }

            current = LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// 打开前清理残留鼠标捕获（issue 015 / 1.1-B）：文本框拖选残留的捕获会把后续点击导回文本框，
    /// 既可能清空选区也可能让气泡被误判关闭；捕获目标不在气泡逻辑树内时一律释放。
    /// </summary>
    private void ReleaseStaleCapture()
    {
        if (Mouse.Captured is not { } captured)
        {
            return;
        }

        if (captured is DependencyObject node && ContainsLogicalSubtree(node))
        {
            return;
        }

        Mouse.Capture(null);
    }

    /// <summary>
    /// 由面板在每次弹出前调用：把本轮探测的源语言灌入语言下拉默认选中项。
    /// 用户此前的临场改选只在本次打开期间有效，下次划词重新取面板默认值。
    /// </summary>
    public void SetDefaultLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        var match = LanguageChoices.All.FirstOrDefault(choice =>
            string.Equals(choice.Name, language, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            LanguageComboBox.SelectedItem = match;
        }
    }

    /// <summary>当前语言下拉选中项（母语写法展示名）。</summary>
    private string GetSelectedLanguage() =>
        LanguageComboBox.SelectedItem is LanguageChoice choice ? choice.Name : "日本語";

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        // 气泡关闭后清空内容
        ContentTextBox.Clear();
    }

    private void OnContentTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFavoriteState();
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFavoriteState();
    }

    private string GetSelectedCategory()
    {
        if (CategoryComboBox?.SelectedItem is ComboBoxItem item && item.Content is string category)
        {
            return category;
        }
        return "单词";
    }

    private void UpdateFavoriteState()
    {
        if (ContentTextBox == null || FavoriteIconTextBlock == null || FavoriteLabelTextBlock == null || FavoriteButton == null)
        {
            return;
        }

        var content = ContentTextBox.Text?.Trim() ?? string.Empty;

        // 已收藏判定按归一化 content 匹配且不强制类别（issue 015 / 1.4）：
        // 用户微调出的注音形态（気付く[きづく]、気付く (きづく)）同样识别为已收藏，不会重复入库。
        _isCurrentContentFavorited = _favoriteService?.IsFavorited(content) ?? false;

        if (_isCurrentContentFavorited)
        {
            FavoriteIconTextBlock.Text = "\uE735"; // 实心星
            FavoriteIconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            FavoriteLabelTextBlock.Text = "已收藏";
            FavoriteButton.ToolTip = "当前词条已在生词本中（点击可取消收藏）";
        }
        else
        {
            FavoriteIconTextBlock.Text = "\uE734"; // 空心星
            var normalBrush = Application.Current?.TryFindResource("Brush.Text.Primary") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x1F, 0x2E, 0x27));
            FavoriteIconTextBlock.Foreground = normalBrush;
            FavoriteLabelTextBlock.Text = "收藏";
            FavoriteButton.ToolTip = "将此词条添加到生词本";
        }
    }

    /// <summary>
    /// 收藏/取消收藏动作（issue 015 / 1.1-B）：触发点从 Click 前移到 PreviewMouseLeftButtonDown，
    /// 使 Popup 的“外部点击自动关闭”竞态来不及执行；向下标记 Handled 阻止 Button 随后再补发 Click，
    /// 避免同一次点击被计数两次。
    /// </summary>
    private async void OnFavoriteClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var content = ContentTextBox.Text.Trim();
        if (string.IsNullOrEmpty(content))
        {
            Hide();
            return;
        }

        var category = GetSelectedCategory();

        if (_isCurrentContentFavorited)
        {
            // 取消收藏
            if (_favoriteService != null)
            {
                await _favoriteService.RemoveFavoriteAsync(content);
            }
            FavoriteRemoved?.Invoke(this, content);
            UpdateFavoriteState();
        }
        else
        {
            // 新增收藏
            var item = new FavoriteItem
            {
                Category = category,
                Content = content,
                Language = GetSelectedLanguage(),
                Note = null,
                CreatedAt = DateTimeOffset.Now
            };

            if (_favoriteService != null)
            {
                await _favoriteService.AddFavoriteAsync(item);
            }

            FavoriteRequested?.Invoke(this, item);
            UpdateFavoriteState();

            // 给予 300ms 视觉正反馈后自动平滑收起
            await Task.Delay(300);
            Hide();
        }
    }
}
