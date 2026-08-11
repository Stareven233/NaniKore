using System.Windows;
using System.Windows.Controls;
using ContentControl = System.Windows.Controls.ContentControl;

namespace NaniKore.App.Controls;

/// <summary>
/// 设置分组卡片：标题在卡片外上方，内容区自带圆角、描边与统一内边距。
/// 用分组代替原先的一整张平铺表格，让字段有语义归属，视觉上也形成层次。
/// </summary>
internal sealed class SettingsGroup : HeaderedContentControl
{
    static SettingsGroup() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(SettingsGroup),
        new FrameworkPropertyMetadata(typeof(SettingsGroup)));
}

/// <summary>
/// 单个设置行：左侧标题与可选说明，右侧承载输入控件。
/// 说明文字与字段同行同块，取代旧版游离在页面底部的整段提示。
/// </summary>
internal sealed class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(string),
        typeof(SettingsRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingsRow),
        new PropertyMetadata(string.Empty));

    /// <summary>字段控件的建议宽度；为 <see cref="double.NaN"/> 时占满右侧剩余宽度。</summary>
    public static readonly DependencyProperty ContentWidthProperty = DependencyProperty.Register(
        nameof(ContentWidth),
        typeof(double),
        typeof(SettingsRow),
        new PropertyMetadata(double.NaN));

    /// <summary>为 true 时字段换到标题下方整行显示，适合多行文本这类需要完整宽度的控件。</summary>
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(
        nameof(IsStacked),
        typeof(bool),
        typeof(SettingsRow),
        new PropertyMetadata(false));

    static SettingsRow() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(SettingsRow),
        new FrameworkPropertyMetadata(typeof(SettingsRow)));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    public bool IsStacked
    {
        get => (bool)GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }
}
