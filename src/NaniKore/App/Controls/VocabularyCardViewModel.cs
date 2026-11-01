namespace NaniKore.App.Controls;

using System;
using System.ComponentModel;
using NaniKore.Translation;

/// <summary>
/// 词汇与短语卡片视图模型，支持属性通知与收藏状态即时响应。
/// </summary>
public sealed class VocabularyCardViewModel : INotifyPropertyChanged
{
    private bool _isFavorited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public VocabularyCardViewModel(ExplanationVocabularyCard model, bool isFavorited = false)
    {
        Model = model;
        _isFavorited = isFavorited;
    }

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorited)));
            }
        }
    }
}
