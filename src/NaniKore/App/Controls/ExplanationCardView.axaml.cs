using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NaniKore.Favorites;
using NaniKore.Translation;

namespace NaniKore.App.Controls;

/// <summary>
/// 精讲微结构化卡片与降级纯文本复合展示控件。
/// </summary>
public partial class ExplanationCardView : UserControl
{
    private const string FallbackFavoriteLanguage = "日本語";

    private readonly StringBuilder _accumulatedMarkdown = new();
    private IFavoriteService? _favoriteService;
    private Func<string>? _sourceLanguageProvider;
    private bool _isDegraded;

    public ObservableCollection<VocabularyCardViewModel> VocabularyItems { get; } = [];
    public ObservableCollection<GrammarCardViewModel> GrammarItems { get; } = [];

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
        DataContext = this;
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
            _favoriteService.FavoriteUpdated -= OnExternalFavoriteUpdated;
        }

        _favoriteService = service;

        if (_favoriteService != null)
        {
            _favoriteService.FavoriteAdded += OnExternalFavoriteChanged;
            _favoriteService.FavoriteRemoved += OnExternalFavoriteRemoved;
            _favoriteService.FavoriteUpdated += OnExternalFavoriteUpdated;
        }

        RefreshFavoriteStates();
    }

    /// <summary>
    /// 注入本轮源语言探测器：卡片收藏时直接取当前值作为默认语言，不做选择 UI。
    /// </summary>
    public void SetSourceLanguageProvider(Func<string>? provider)
    {
        _sourceLanguageProvider = provider;
    }

    /// <summary>当前轮次要写入收藏的语言：优先面板探测映射值，缺省回落兜底常量。</summary>
    private string CurrentFavoriteLanguage => _sourceLanguageProvider?.Invoke() is { Length: > 0 } detected
        ? detected
        : FallbackFavoriteLanguage;

    /// <summary>清空所有卡片与累积流式文本。</summary>
    public void Clear()
    {
        _accumulatedMarkdown.Clear();
        VocabularyItems.Clear();
        GrammarItems.Clear();
        ContextExplanationTextBox.Text = string.Empty;
        VocabularyGroup.IsVisible = false;
        GrammarGroup.IsVisible = false;
        ContextGroup.IsVisible = false;
        FallbackTextBox.Clear();
        FallbackTextBox.IsVisible = false;
        CardsScrollViewer.IsVisible = true;
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
            CardsScrollViewer.IsVisible = false;
            FallbackTextBox.IsVisible = true;
            FallbackTextBox.Text = raw;
            return;
        }

        IsDegraded = false;
        FallbackTextBox.IsVisible = false;
        CardsScrollViewer.IsVisible = true;

        // 同步词汇卡片
        SyncVocabularyCards(doc.VocabularyCards);
        VocabularyGroup.IsVisible = VocabularyItems.Count > 0;

        // 同步语法卡片
        SyncGrammarCards(doc.GrammarCards);
        GrammarGroup.IsVisible = GrammarItems.Count > 0;

        // 同步语境说明
        ContextExplanationTextBox.Text = doc.ContextExplanation;
        ContextGroup.IsVisible = !string.IsNullOrWhiteSpace(doc.ContextExplanation);
    }

    private void SyncVocabularyCards(List<ExplanationVocabularyCard> cards)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var isFavorited = _favoriteService?.IsFavorited(card.Term) ?? false;
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
            var isFavorited = _favoriteService?.IsFavorited(card.Pattern) ?? false;
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
            item.IsFavorited = _favoriteService.IsFavorited(item.Term);
        }
        foreach (var item in GrammarItems)
        {
            item.IsFavorited = _favoriteService.IsFavorited(item.Pattern);
        }
    }

    private void OnExternalFavoriteUpdated(object? sender, FavoriteItem item)
    {
        Dispatcher.UIThread.InvokeAsync(RefreshFavoriteStates);
    }

    private void OnExternalFavoriteChanged(object? sender, FavoriteItem item)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var vocab in VocabularyItems)
            {
                if (FavoriteContentNormalizer.Equals(vocab.Term, item.Content))
                {
                    vocab.IsFavorited = true;
                }
            }
            foreach (var grammar in GrammarItems)
            {
                if (FavoriteContentNormalizer.Equals(grammar.Pattern, item.Content))
                {
                    grammar.IsFavorited = true;
                }
            }
        });
    }

    private void OnExternalFavoriteRemoved(object? sender, string content)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var vocab in VocabularyItems)
            {
                if (FavoriteContentNormalizer.Equals(vocab.Term, content))
                {
                    vocab.IsFavorited = false;
                }
            }
            foreach (var grammar in GrammarItems)
            {
                if (FavoriteContentNormalizer.Equals(grammar.Pattern, content))
                {
                    grammar.IsFavorited = false;
                }
            }
        });
    }

    private async void OnVocabularyFavoriteClick(object? sender, RoutedEventArgs e)
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
                    Language = CurrentFavoriteLanguage,
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

    private async void OnGrammarFavoriteClick(object? sender, RoutedEventArgs e)
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
                    Language = CurrentFavoriteLanguage,
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
