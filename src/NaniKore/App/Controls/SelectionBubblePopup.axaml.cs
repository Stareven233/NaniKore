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
        BubblePopup.Placement = PlacementMode.Bottom;

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
        return focusedElement != null && (focusedElement == this || this.IsVisualAncestorOf(focusedElement));
    }

    /// <summary>判断屏幕点是否落在气泡的可视区域内。</summary>
    public bool ContainsScreenPoint(Point screenPoint)
    {
        if (!BubblePopup.IsOpen)
        {
            return false;
        }

        var bounds = Bounds;
        try
        {
            var topLeft = this.PointToScreen(new Point(0, 0));
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
        _isCurrentContentFavorited = !string.IsNullOrEmpty(content) &&
                                     (_favoriteService?.IsFavorited(content) ?? false);

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

    private async void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        var content = ContentTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        if (_isCurrentContentFavorited)
        {
            // 取消收藏
            if (_favoriteService != null)
            {
                await _favoriteService.RemoveFavoriteAsync(content);
            }

            _isCurrentContentFavorited = false;
            UpdateFavoriteState();
            FavoriteRemoved?.Invoke(this, content);
        }
        else
        {
            // 执行收藏
            var category = (CategoryComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "单词";
            var language = (LanguageComboBox.SelectedItem as LanguageChoice)?.Name ?? "日本語";

            var item = new FavoriteItem
            {
                Category = category,
                Content = content,
                Language = language,
                Note = _pendingNote
            };

            if (_favoriteService != null)
            {
                await _favoriteService.AddFavoriteAsync(item);
            }

            _isCurrentContentFavorited = true;
            UpdateFavoriteState();
            FavoriteRequested?.Invoke(this, item);
        }
    }
}
