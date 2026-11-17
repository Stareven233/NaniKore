using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NaniKore.App.Settings;
using NaniKore.Favorites;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace NaniKore.App.Controls;

/// <summary>
/// 正文自由划词微型悬浮收藏气泡。
/// 支持原地微调、分类选择、失焦自动关闭及根据字符串匹配即时显示已收藏状态。
/// </summary>
public partial class SelectionBubblePopup : UserControl
{
    private IFavoriteService? _favoriteService;
    private bool _isCurrentContentFavorited;
    private string? _pendingNote;

    /// <summary>收藏动作被触发时激发的事件。</summary>
    public event EventHandler<FavoriteItem>? FavoriteRequested;

    /// <summary>取消收藏动作被触发时激发的事件。</summary>
    public event EventHandler<string>? FavoriteRemoved;

    /// <summary>收藏库操作失败时激发：气泡保持打开且星标不变，宿主据此提示用户。</summary>
    public event EventHandler<string>? FavoriteFailed;

    public Popup PopupHost => BubblePopup;

    public ComboBox CategoryPicker => CategoryComboBox;

    public ComboBox LanguagePicker => LanguageComboBox;

    public TextBox ContentEditor => ContentTextBox;

    public Button FavoriteBtn => FavoriteButton;

    public SelectionBubblePopup()
    {
        InitializeComponent();

        // 语言下拉与全局共享列表保持一致
        foreach (var language in LanguageChoices.All)
        {
            LanguageComboBox.Items.Add(language);
        }

        LanguageComboBox.SelectedIndex = 0;
    }

    /// <summary>注入收藏服务以进行状态同步。</summary>
    public void SetFavoriteService(IFavoriteService? service)
    {
        _favoriteService = service;
        UpdateFavoriteState();
    }

    /// <summary>设置默认展示的语言选项。</summary>
    public void SetDefaultLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        for (var i = 0; i < LanguageComboBox.Items.Count; i++)
        {
            if (LanguageComboBox.Items[i] is LanguageChoice choice && choice.Matches(language))
            {
                LanguageComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>在指定目标元素与矩形区域弹出气泡。</summary>
    public void ShowAt(Control target, Rect targetRect, string selectedText, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;
        CategoryComboBox.SelectedIndex = 0;
        _pendingNote = string.IsNullOrWhiteSpace(note) ? null : note;

        BubblePopup.PlacementTarget = target;
        BubblePopup.PlacementRect = targetRect;
        BubblePopup.Placement = PlacementMode.Bottom;
        BubblePopup.VerticalOffset = 6;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>在指定相对坐标弹出气泡（供自测或绝对定位使用）。</summary>
    public void ShowAt(Point point, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            Hide();
            return;
        }

        ContentTextBox.Text = selectedText.Trim();
        ContentTextBox.CaretIndex = ContentTextBox.Text.Length;
        CategoryComboBox.SelectedIndex = 0;
        _pendingNote = null;

        BubblePopup.PlacementTarget = this;
        BubblePopup.Placement = PlacementMode.Pointer;

        UpdateFavoriteState();
        BubblePopup.IsOpen = true;
    }

    /// <summary>关闭气泡。</summary>
    public void Hide()
    {
        BubblePopup.IsOpen = false;
        _pendingNote = null;
    }

    /// <summary>判断气泡当前是否拥有逻辑焦点。</summary>
    public bool ContainsLogicalFocus()
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        var focusedElement = focusManager?.GetFocusedElement() as Visual;
        if (focusedElement == null)
        {
            return false;
        }

        if (focusedElement == this || this.IsVisualAncestorOf(focusedElement))
        {
            return true;
        }

        if (BubblePopup.Child is Visual bubbleVisual &&
            (focusedElement == bubbleVisual || bubbleVisual.IsVisualAncestorOf(focusedElement)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断指定的交互指针事件是否落在气泡内容区域或其子交互组件（如下拉菜单）内部。
    /// 优先通过可视树祖先判定，辅以跨层级几何命中测试，杜绝点击气泡选项时被误判为外部点击而撤销。
    /// </summary>
    public bool ContainsPointerEvent(PointerEventArgs e)
    {
        if (!BubblePopup.IsOpen)
        {
            return false;
        }

        // 1. 若下拉菜单正处于展开交互状态，绝不视为外部点击
        if (CategoryComboBox.IsDropDownOpen || LanguageComboBox.IsDropDownOpen)
        {
            return true;
        }

        // 2. 检查事件源 Visual 是否属于气泡或气泡弹出内容的后代
        if (e.Source is Visual sourceVisual)
        {
            if (sourceVisual == this || this.IsVisualAncestorOf(sourceVisual))
            {
                return true;
            }

            if (BubblePopup.Child is Visual bubbleContent &&
                (sourceVisual == bubbleContent || bubbleContent.IsVisualAncestorOf(sourceVisual)))
            {
                return true;
            }
        }

        // 3. 几何命中测试：计算指针相对气泡内容容器的本地坐标（仅在内容已具有真实尺寸时生效）
        if (BubblePopup.Child is Visual visual && visual.IsVisible)
        {
            var bounds = visual.Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                try
                {
                    var ptInBubble = e.GetPosition(visual);
                    if (ptInBubble.X >= 0 && ptInBubble.X <= bounds.Width &&
                        ptInBubble.Y >= 0 && ptInBubble.Y <= bounds.Height)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 坐标转换遭遇极端状态时安全跳过
                }
            }
        }

        return false;
    }

    /// <summary>判断屏幕点是否落在气泡的可视区域内。</summary>
    public bool ContainsScreenPoint(Point screenPoint)
    {
        if (!BubblePopup.IsOpen)
        {
            return false;
        }

        var visual = BubblePopup.Child as Visual ?? this;
        var bounds = visual.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        try
        {
            var topLeft = visual.PointToScreen(new Point(0, 0));
            var rect = new Rect(topLeft.X, topLeft.Y, bounds.Width, bounds.Height);
            return rect.Contains(screenPoint);
        }
        catch
        {
            return false;
        }
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFavoriteState();
    }

    private void OnContentTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateFavoriteState();
    }

    private void UpdateFavoriteState()
    {
        var content = ContentTextBox?.Text?.Trim() ?? string.Empty;
        _isCurrentContentFavorited = !string.IsNullOrEmpty(content) && IsFavoritedQuietly(content);
        ApplyFavoriteVisual();
    }

    /// <summary>库不可读时按未收藏降级展示：读失败不阻塞气泡输入，写失败另有提示。</summary>
    private bool IsFavoritedQuietly(string content)
    {
        if (_favoriteService is null)
        {
            return false;
        }

        try
        {
            return _favoriteService.IsFavorited(content);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按当前收藏态刷新星标与按钮样式，不访问收藏库。</summary>
    private void ApplyFavoriteVisual()
    {
        if (FavoriteIconTextBlock is not null && FavoriteLabelTextBlock is not null && FavoriteButton is not null)
        {
            if (_isCurrentContentFavorited)
            {
                FavoriteIconTextBlock.Text = "★";
                FavoriteLabelTextBlock.Text = "已收藏";
                FavoriteButton.Classes.Clear();
                FavoriteButton.Classes.Add("accent");
            }
            else
            {
                FavoriteIconTextBlock.Text = "☆";
                FavoriteLabelTextBlock.Text = "收藏";
                FavoriteButton.Classes.Clear();
                FavoriteButton.Classes.Add("secondary");
            }
        }
    }

    private async void OnFavoriteClick(object? sender, RoutedEventArgs e) => await ToggleFavoriteAsync();

    /// <summary>
    /// 收藏/取消收藏气泡内容：成功后才切换状态，异常与拒绝都收敛为提示，
    /// 气泡保持打开以便重试，绝不把异常抛出 async void 事件。
    /// </summary>
    internal async Task ToggleFavoriteAsync()
    {
        var content = ContentTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        try
        {
            if (_isCurrentContentFavorited)
            {
                if (_favoriteService is not null)
                {
                    // 删除未命中（条目已被移除）视同完成：星标目标态本就是未收藏。
                    await _favoriteService.RemoveFavoriteAsync(content);
                }

                _isCurrentContentFavorited = false;
                ApplyFavoriteVisual();
                FavoriteRemoved?.Invoke(this, content);
                return;
            }

            var item = new FavoriteItem
            {
                Category = (CategoryComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "单词",
                Content = content,
                Language = (LanguageComboBox.SelectedItem as LanguageChoice)?.Name ?? "日本語",
                Note = _pendingNote
            };

            if (_favoriteService is not null && !await _favoriteService.AddFavoriteAsync(item))
            {
                FavoriteFailed?.Invoke(this, $"收藏失败：收藏库未保存「{content}」，请稍后重试");
                return;
            }

            _isCurrentContentFavorited = true;
            ApplyFavoriteVisual();
            FavoriteRequested?.Invoke(this, item);
        }
        catch (Exception exception)
        {
            FavoriteFailed?.Invoke(this, FavoriteFailure.Describe(_isCurrentContentFavorited ? "取消收藏" : "收藏", exception));
        }
    }
}
