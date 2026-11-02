using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using NaniKore.Translation;
using Color = Avalonia.Media.Color;

namespace NaniKore.App.Controls;

/// <summary>
/// 词汇与短语卡片视图模型，支持属性通知与收藏状态即时响应。
/// </summary>
public sealed class VocabularyCardViewModel : INotifyPropertyChanged
{
    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly IBrush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0x90));

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

    /// <summary>Tag 只展示语法纯净词性（一类动词、二类动词、名词等）。</summary>
    public string RoleBadgeText => Role;
    public bool RoleVisible => !string.IsNullOrWhiteSpace(Role);
    public bool MeaningVisible => !string.IsNullOrWhiteSpace(Meaning);

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
                OnPropertyChanged(nameof(FavoriteText));
                OnPropertyChanged(nameof(FavoriteTooltip));
                OnPropertyChanged(nameof(FavoriteBrush));
            }
        }
    }

    /// <summary>收藏图标：实心星与空心星。</summary>
    public string FavoriteIcon => IsFavorited ? "★" : "☆";

    /// <summary>收藏按钮文案。</summary>
    public string FavoriteText => IsFavorited ? "★ 已收藏" : "★ 收藏";

    public string FavoriteTooltip => IsFavorited ? "已收藏 (点击取消收藏)" : "收藏至生词本";

    public IBrush FavoriteBrush => IsFavorited ? GoldBrush : GrayBrush;

    public VocabularyCardViewModel(ExplanationVocabularyCard model, bool isFavorited = false)
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
    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly IBrush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0x90));

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
                OnPropertyChanged(nameof(FavoriteText));
                OnPropertyChanged(nameof(FavoriteTooltip));
                OnPropertyChanged(nameof(FavoriteBrush));
            }
        }
    }

    public string FavoriteIcon => IsFavorited ? "★" : "☆";

    public string FavoriteText => IsFavorited ? "★ 已收藏" : "★ 收藏";

    public string FavoriteTooltip => IsFavorited ? "已收藏 (点击取消收藏)" : "收藏至生词本";

    public IBrush FavoriteBrush => IsFavorited ? GoldBrush : GrayBrush;

    public GrammarCardViewModel(ExplanationGrammarCard model, bool isFavorited = false)
    {
        Model = model;
        _isFavorited = isFavorited;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
