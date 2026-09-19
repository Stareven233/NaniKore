namespace NaniKore.App.Controls;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NaniKore.Favorites;
using NaniKore.Translation;

/// <summary>
/// 词汇与短语卡片视图模型，支持属性通知与收藏状态即时响应。
/// </summary>
public sealed class VocabularyCardViewModel : INotifyPropertyChanged
{
    private bool _isFavorited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExplanationVocabularyCard Model { get; }

    public string Term => Model.Term;
    public string Reading => Model.Reading;
    public string Role => Model.Role;
    public string Meaning => Model.Meaning;

    /// <summary>标题展示文本：如果有读音且与词汇不重复，显示为“词汇 (读音)”。</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Reading) || string.Equals(Reading, Term, StringComparison.OrdinalIgnoreCase)
        ? Term
        : $"{Term} ({Reading})";

    /// <summary>Tag 只展示日本语教育语法纯净词性（一类动词、二类动词、名词等）。</summary>
    public string RoleBadgeText => Role;
    public Visibility RoleVisibility => string.IsNullOrWhiteSpace(Role) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility MeaningVisibility => string.IsNullOrWhiteSpace(Meaning) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsFavorited
    {
        get => _isFavorited;
        set
        {
            if (_isFavorited != value)
            {
                _isFavorited = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteIcon));
                OnPropertyChanged(nameof(FavoriteTooltip));
                OnPropertyChanged(nameof(FavoriteBrush));
            }
        }
    }

    /// <summary>实心星 \uE735，空心星 \uE734。</summary>
    public string FavoriteIcon => IsFavorited ? "\uE735" : "\uE734";

    public string FavoriteTooltip => IsFavorited ? "已收藏 (点击取消收藏)" : "收藏至生词本";

    public Brush FavoriteBrush => IsFavorited
        ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) // 暖金色高亮
        : (Brush)Application.Current.FindResource("Brush.Text.Secondary");

    public VocabularyCardViewModel(ExplanationVocabularyCard model, bool isFavorited)
    {
        Model = model;
        _isFavorited = isFavorited;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 语法与句型卡片视图模型。
/// </summary>
public sealed class GrammarCardViewModel : INotifyPropertyChanged
{
    private bool _isFavorited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExplanationGrammarCard Model { get; }

    public string Pattern => Model.Pattern;
    public string Meaning => Model.Meaning;

    public bool IsFavorited
    {
        get => _isFavorited;
        set
        {
            if (_isFavorited != value)
            {
                _isFavorited = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteIcon));
                OnPropertyChanged(nameof(FavoriteTooltip));
                OnPropertyChanged(nameof(FavoriteBrush));
            }
        }
    }

    public string FavoriteIcon => IsFavorited ? "\uE735" : "\uE734";

    public string FavoriteTooltip => IsFavorited ? "已收藏 (点击取消收藏)" : "收藏至生词本";

    public Brush FavoriteBrush => IsFavorited
        ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
        : (Brush)Application.Current.FindResource("Brush.Text.Secondary");

    public GrammarCardViewModel(ExplanationGrammarCard model, bool isFavorited)
    {
        Model = model;
        _isFavorited = isFavorited;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 精讲微结构化卡片与降级纯文本复合展示控件。
/// </summary>
public partial class ExplanationCardView : UserControl
{
    private readonly StringBuilder _accumulatedMarkdown = new();
    private IFavoriteService? _favoriteService;
    private bool _isDegraded;

    public ObservableCollection<VocabularyCardViewModel> VocabularyItems { get; } = new();
    public ObservableCollection<GrammarCardViewModel> GrammarItems { get; } = new();

    /// <summary>当卡片被用户收藏或取消收藏时激发的事件通知。</summary>
    public event EventHandler<(string Content, string Category, bool Added)>? FavoriteToggled;

    /// <summary>当前是否因模型未按格式输出而降级为纯文本排版展示。</summary>
    public bool IsDegraded
    {
        get => _isDegraded;
        private set
        {
            if (_isDegraded != value)
            {
                _isDegraded = value;
                DegradedChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<bool>? DegradedChanged;

    /// <summary>获取当前累计的原始全量精讲文本。</summary>
    public string RawText => _accumulatedMarkdown.ToString();

    public ExplanationCardView()
    {
        InitializeComponent();
        VocabularyItemsControl.ItemsSource = VocabularyItems;
        GrammarItemsControl.ItemsSource = GrammarItems;
    }

    /// <summary>注入收藏服务并订阅外部变更事件，实现字符串级别的收藏状态双向同步。</summary>
    public void SetFavoriteService(IFavoriteService? service)
    {
        if (ReferenceEquals(_favoriteService, service))
        {
            return;
        }

        if (_favoriteService != null)
        {
            _favoriteService.FavoriteAdded -= OnExternalFavoriteChanged;
            _favoriteService.FavoriteRemoved -= OnExternalFavoriteRemoved;
        }

        _favoriteService = service;

        if (_favoriteService != null)
        {
            _favoriteService.FavoriteAdded += OnExternalFavoriteChanged;
            _favoriteService.FavoriteRemoved += OnExternalFavoriteRemoved;
        }

        RefreshFavoriteStates();
    }

    /// <summary>清空所有卡片与累积流式文本。</summary>
    public void Clear()
    {
        _accumulatedMarkdown.Clear();
        VocabularyItems.Clear();
        GrammarItems.Clear();
        ContextExplanationTextBox.Text = string.Empty;
        VocabularyGroup.Visibility = Visibility.Collapsed;
        GrammarGroup.Visibility = Visibility.Collapsed;
        ContextGroup.Visibility = Visibility.Collapsed;
        FallbackTextBox.Clear();
        FallbackTextBox.Visibility = Visibility.Collapsed;
        CardsScrollViewer.Visibility = Visibility.Visible;
        IsDegraded = false;
    }

    /// <summary>追加流式增量并更新卡片视图。</summary>
    public void AppendChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _accumulatedMarkdown.Append(chunk);
        UpdateViewFromMarkdown();
    }

    /// <summary>一次性加载完整 Markdown 文本（测试或历史加载）。</summary>
    public void SetText(string markdown)
    {
        Clear();
        if (!string.IsNullOrEmpty(markdown))
        {
            _accumulatedMarkdown.Append(markdown);
            UpdateViewFromMarkdown();
        }
    }

    /// <summary>流式生成结束后的收束确认。</summary>
    public void Complete(bool success)
    {
        if (!success && _accumulatedMarkdown.Length == 0)
        {
            Clear();
            return;
        }

        UpdateViewFromMarkdown();
    }

    private void UpdateViewFromMarkdown()
    {
        var raw = _accumulatedMarkdown.ToString();
        var doc = ExplanationParser.Parse(raw);

        if (!doc.IsStructured)
        {
            // 降级为纯文本排版展示
            IsDegraded = true;
            CardsScrollViewer.Visibility = Visibility.Collapsed;
            FallbackTextBox.Visibility = Visibility.Visible;
            FallbackTextBox.Text = raw;
            FallbackTextBox.ScrollToEnd();
            return;
        }

        IsDegraded = false;
        FallbackTextBox.Visibility = Visibility.Collapsed;
        CardsScrollViewer.Visibility = Visibility.Visible;

        // 同步词汇卡片
        SyncVocabularyCards(doc.VocabularyCards);
        VocabularyGroup.Visibility = VocabularyItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 同步语法卡片
        SyncGrammarCards(doc.GrammarCards);
        GrammarGroup.Visibility = GrammarItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 同步语境说明
        ContextExplanationTextBox.Text = doc.ContextExplanation;
        ContextGroup.Visibility = !string.IsNullOrWhiteSpace(doc.ContextExplanation) ? Visibility.Visible : Visibility.Collapsed;

        CardsScrollViewer.ScrollToEnd();
    }

    private void SyncVocabularyCards(List<ExplanationVocabularyCard> cards)
    {
        // 增量同步以避免频繁重建卡片引起闪烁
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var isFavorited = _favoriteService?.IsFavorited(card.Term, "单词") ?? false;
            if (i < VocabularyItems.Count)
            {
                var vm = VocabularyItems[i];
                if (!string.Equals(vm.Term, card.Term, StringComparison.Ordinal) ||
                    !string.Equals(vm.Meaning, card.Meaning, StringComparison.Ordinal) ||
                    !string.Equals(vm.Reading, card.Reading, StringComparison.Ordinal))
                {
                    VocabularyItems[i] = new VocabularyCardViewModel(card, isFavorited);
                }
            }
            else
            {
                VocabularyItems.Add(new VocabularyCardViewModel(card, isFavorited));
            }
        }

        while (VocabularyItems.Count > cards.Count)
        {
            VocabularyItems.RemoveAt(VocabularyItems.Count - 1);
        }
    }

    private void SyncGrammarCards(List<ExplanationGrammarCard> cards)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var isFavorited = _favoriteService?.IsFavorited(card.Pattern, "语法") ?? false;
            if (i < GrammarItems.Count)
            {
                var vm = GrammarItems[i];
                if (!string.Equals(vm.Pattern, card.Pattern, StringComparison.Ordinal) ||
                    !string.Equals(vm.Meaning, card.Meaning, StringComparison.Ordinal))
                {
                    GrammarItems[i] = new GrammarCardViewModel(card, isFavorited);
                }
            }
            else
            {
                GrammarItems.Add(new GrammarCardViewModel(card, isFavorited));
            }
        }

        while (GrammarItems.Count > cards.Count)
        {
            GrammarItems.RemoveAt(GrammarItems.Count - 1);
        }
    }

    private void RefreshFavoriteStates()
    {
        if (_favoriteService == null)
        {
            return;
        }

        foreach (var item in VocabularyItems)
        {
            item.IsFavorited = _favoriteService.IsFavorited(item.Term, "单词");
        }
        foreach (var item in GrammarItems)
        {
            item.IsFavorited = _favoriteService.IsFavorited(item.Pattern, "语法");
        }
    }

    private void OnExternalFavoriteChanged(object? sender, FavoriteItem item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var vocab in VocabularyItems)
            {
                if (string.Equals(vocab.Term, item.Content, StringComparison.OrdinalIgnoreCase))
                {
                    vocab.IsFavorited = true;
                }
            }
            foreach (var grammar in GrammarItems)
            {
                if (string.Equals(grammar.Pattern, item.Content, StringComparison.OrdinalIgnoreCase))
                {
                    grammar.IsFavorited = true;
                }
            }
        });
    }

    private void OnExternalFavoriteRemoved(object? sender, string content)
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var vocab in VocabularyItems)
            {
                if (string.Equals(vocab.Term, content, StringComparison.OrdinalIgnoreCase))
                {
                    vocab.IsFavorited = false;
                }
            }
            foreach (var grammar in GrammarItems)
            {
                if (string.Equals(grammar.Pattern, content, StringComparison.OrdinalIgnoreCase))
                {
                    grammar.IsFavorited = false;
                }
            }
        });
    }

    private async void OnVocabularyFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VocabularyCardViewModel vm })
        {
            return;
        }

        var newFavoritedState = !vm.IsFavorited;
        vm.IsFavorited = newFavoritedState;

        if (_favoriteService != null)
        {
            if (newFavoritedState)
            {
                await _favoriteService.AddFavoriteAsync(new FavoriteItem
                {
                    Category = "单词",
                    Content = vm.Term,
                    Note = vm.Model.ToFavoriteNote()
                });
            }
            else
            {
                await _favoriteService.RemoveFavoriteAsync(vm.Term);
            }
        }

        FavoriteToggled?.Invoke(this, (vm.Term, "单词", newFavoritedState));
    }

    private async void OnGrammarFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GrammarCardViewModel vm })
        {
            return;
        }

        var newFavoritedState = !vm.IsFavorited;
        vm.IsFavorited = newFavoritedState;

        if (_favoriteService != null)
        {
            if (newFavoritedState)
            {
                await _favoriteService.AddFavoriteAsync(new FavoriteItem
                {
                    Category = "语法",
                    Content = vm.Pattern,
                    Note = vm.Model.ToFavoriteNote()
                });
            }
            else
            {
                await _favoriteService.RemoveFavoriteAsync(vm.Pattern);
            }
        }

        FavoriteToggled?.Invoke(this, (vm.Pattern, "语法", newFavoritedState));
    }
}

