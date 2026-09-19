namespace NaniKore.App.Controls;

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        BubblePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        BubblePopup.HorizontalOffset = 4;
        BubblePopup.VerticalOffset = 16;

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
        var category = GetSelectedCategory();

        _isCurrentContentFavorited = _favoriteService?.IsFavorited(content, category) ?? false;

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

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
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
