using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;

namespace NaniKore.App.Controls;

/// <summary>
/// 设置分组卡片：标题在卡片外上方，内容区自带圆角、描边与统一内边距。
/// 用分组代替原先的一整张平铺表格，让字段有语义归属，视觉上也形成层次。
/// </summary>
internal class SettingsGroup : HeaderedContentControl
{
}

/// <summary>
/// 单个设置行：左侧标题与可选说明，右侧承载输入控件。
/// 支持普通并排模式与整行堆叠模式（多行文本框等）。
/// </summary>
[PseudoClasses(":stacked")]
internal class SettingsRow : ContentControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<SettingsRow, string>(nameof(Header), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SettingsRow, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<object?> HeaderActionProperty =
        AvaloniaProperty.Register<SettingsRow, object?>(nameof(HeaderAction));

    public static readonly StyledProperty<double> ContentWidthProperty =
        AvaloniaProperty.Register<SettingsRow, double>(nameof(ContentWidth), double.NaN);

    public static readonly StyledProperty<bool> IsStackedProperty =
        AvaloniaProperty.Register<SettingsRow, bool>(nameof(IsStacked), false);

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderAction
    {
        get => GetValue(HeaderActionProperty);
        set => SetValue(HeaderActionProperty, value);
    }

    public double ContentWidth
    {
        get => GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    public bool IsStacked
    {
        get => GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }

    static SettingsRow()
    {
        IsStackedProperty.Changed.AddClassHandler<SettingsRow>((row, _) =>
        {
            row.PseudoClasses.Set(":stacked", row.IsStacked);
        });
    }

    public SettingsRow()
    {
        PseudoClasses.Set(":stacked", IsStacked);
    }
}
