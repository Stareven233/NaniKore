using System.Windows;
using System.Windows.Controls;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace NaniKore.App.Controls;

/// <summary>
/// 振假名内联排版控件：上行小字注音假名，下行汉字正文底字，垂直靠底对齐。
/// </summary>
internal sealed class RubyInlineControl : Grid
{
    public string BaseText { get; }

    public string RubyText { get; }

    public RubyInlineControl(string baseText, string rubyText, double baseFontSize = 14)
    {
        BaseText = baseText;
        RubyText = rubyText;

        Focusable = false;
        IsHitTestVisible = false; // 允许鼠标拖选事件穿透至 FlowDocument 容器
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(1.5, 0, 1.5, 0);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var rubyBlock = new TextBlock
        {
            Text = rubyText,
            FontSize = Math.Max(9, baseFontSize * 0.68),
            FontFamily = WpfApplication.Current?.TryFindResource("Font.Ui") as WpfFontFamily ?? new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = WpfApplication.Current?.TryFindResource("Brush.Text.Secondary") as WpfBrush ?? WpfBrushes.Gray,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0.5),
        };
        SetRow(rubyBlock, 0);
        Children.Add(rubyBlock);

        var baseBlock = new TextBlock
        {
            Text = baseText,
            FontSize = baseFontSize,
            FontFamily = WpfApplication.Current?.TryFindResource("Font.Ui") as WpfFontFamily ?? new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = WpfApplication.Current?.TryFindResource("Brush.Text.Primary") as WpfBrush ?? WpfBrushes.Black,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        SetRow(baseBlock, 1);
        Children.Add(baseBlock);
    }
}

