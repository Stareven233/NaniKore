using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NaniKore.Translation;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace NaniKore.App.Controls;

/// <summary>
/// 复制格式枚举：纯正文、带方括号注音文本、纯读音假名。
/// </summary>
internal enum RubyCopyKind
{
    Plain,       // 纯正文（例如：“ここは通れないよ”）
    Annotated,   // 带注音方括号（例如：“ここは通[とお]れないよ”）
    ReadingOnly, // 纯读音（例如：“ここはとおれないよ”）
}

/// <summary>
/// 振假名 FlowDocument 构建与选区文本提取工具。
/// </summary>
internal static class RubyDocumentBuilder
{
    /// <summary>
    /// 将带注音文本解析并构建为包含振假名内联组件的 FlowDocument。
    /// </summary>
    public static FlowDocument Build(string? annotatedText, double fontSize = 14)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = WpfApplication.Current?.TryFindResource("Font.Ui") as WpfFontFamily ?? new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = fontSize,
            Foreground = WpfApplication.Current?.TryFindResource("Brush.Text.Primary") as WpfBrush ?? WpfBrushes.Black,
            Background = WpfBrushes.Transparent,
            IsHyphenationEnabled = false,
            TextAlignment = TextAlignment.Left,
        };

        if (string.IsNullOrEmpty(annotatedText))
        {
            return document;
        }

        var lines = annotatedText.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, lineIndex == lines.Length - 1 ? 0 : 4),
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            };

            if (line.Length == 0)
            {
                // 空行保留段落高度
                paragraph.Inlines.Add(new Run(" "));
                document.Blocks.Add(paragraph);
                continue;
            }

            var segments = RubyTextParser.Parse(line);
            foreach (var segment in segments)
            {
                if (!segment.IsRuby)
                {
                    paragraph.Inlines.Add(new Run(segment.Text));
                }
                else
                {
                    var control = new RubyInlineControl(segment.Text, segment.Ruby!, fontSize);
                    var container = new InlineUIContainer(control)
                    {
                        BaselineAlignment = BaselineAlignment.TextBottom,
                    };
                    paragraph.Inlines.Add(container);
                }
            }

            document.Blocks.Add(paragraph);
        }

        return document;
    }

    /// <summary>
    /// 从选区中提取指定格式的文本（自动处理 InlineUIContainer 中的注音与底字）。
    /// </summary>
    public static string ExtractFromSelection(TextRange? range, RubyCopyKind kind)
    {
        if (range is null || range.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var pointer = range.Start;
        var visitedRubies = new HashSet<RubyInlineControl>();

        while (pointer is not null && pointer.CompareTo(range.End) < 0)
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);

            switch (context)
            {
                case TextPointerContext.Text:
                    var text = pointer.GetTextInRun(LogicalDirection.Forward);
                    var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                    if (next is not null && next.CompareTo(range.End) > 0)
                    {
                        var runLength = pointer.GetOffsetToPosition(range.End);
                        sb.Append(text[..Math.Min(text.Length, Math.Max(0, runLength))]);
                        return sb.ToString().TrimEnd('\r', '\n');
                    }
                    sb.Append(text);
                    break;

                case TextPointerContext.ElementStart:
                    // WPF 中遍历至 InlineUIContainer 起始处时 context 为 ElementStart
                    var elementStart = pointer.GetAdjacentElement(LogicalDirection.Forward);
                    if (elementStart is InlineUIContainer { Child: RubyInlineControl rubyFromContainer } &&
                        visitedRubies.Add(rubyFromContainer))
                    {
                        AppendRubyText(sb, rubyFromContainer, kind);
                    }
                    break;

                case TextPointerContext.EmbeddedElement:
                    // 容器内部相邻的子元素直接为 RubyInlineControl 实例
                    var element = pointer.GetAdjacentElement(LogicalDirection.Forward);
                    if (element is RubyInlineControl rubyDirect && visitedRubies.Add(rubyDirect))
                    {
                        AppendRubyText(sb, rubyDirect, kind);
                    }
                    else if (element is InlineUIContainer { Child: RubyInlineControl rubyInner } &&
                             visitedRubies.Add(rubyInner))
                    {
                        AppendRubyText(sb, rubyInner, kind);
                    }
                    break;

                case TextPointerContext.ElementEnd:
                    if (pointer.Parent is Paragraph && sb.Length > 0 && !sb.ToString().EndsWith('\n'))
                    {
                        sb.AppendLine();
                    }
                    break;
            }

            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendRubyText(StringBuilder sb, RubyInlineControl ruby, RubyCopyKind kind)
    {
        switch (kind)
        {
            case RubyCopyKind.Plain:
                sb.Append(ruby.BaseText);
                break;
            case RubyCopyKind.Annotated:
                sb.Append(ruby.BaseText).Append('[').Append(ruby.RubyText).Append(']');
                break;
            case RubyCopyKind.ReadingOnly:
                sb.Append(ruby.RubyText);
                break;
        }
    }
}
