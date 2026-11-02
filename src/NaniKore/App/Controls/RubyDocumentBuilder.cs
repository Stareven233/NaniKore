using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using NaniKore.Translation;

namespace NaniKore.App.Controls;

/// <summary>
/// 针对 Avalonia SelectableTextBlock 的注音内联构建与精准选区文本提取工具。
/// </summary>
internal static class RubyDocumentBuilder
{
    /// <summary>
    /// 将带注音文本解析并构建为包含振假名内联组件的 Avalonia Inlines 集合。
    /// </summary>
    public static InlineCollection BuildInlines(string? annotatedText, double fontSize = 14)
    {
        var inlines = new InlineCollection();
        if (string.IsNullOrEmpty(annotatedText))
        {
            return inlines;
        }

        var lines = annotatedText.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (lineIndex > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (line.Length == 0)
            {
                continue;
            }

            var segments = RubyTextParser.Parse(line);
            foreach (var segment in segments)
            {
                if (!segment.IsRuby)
                {
                    inlines.Add(new Run(segment.Text));
                }
                else
                {
                    var control = new RubyInlineControl(segment.Text, segment.Ruby!, fontSize);
                    var container = new InlineUIContainer(control);
                    inlines.Add(container);
                }
            }
        }

        return inlines;
    }

    /// <summary>
    /// 从 SelectableTextBlock 中提取指定格式的文本。
    /// 若有选区则提取选区，若无选区则提取全文。
    /// Avalonia 中 InlineUIContainer 在字符流中精准占据 1 个 U+FFFC 字符位置，
    /// 据此实现完全确定性的偏移量映射。
    /// </summary>
    public static string ExtractText(SelectableTextBlock block, RubyCopyKind kind)
    {
        var inlines = block.Inlines;
        if (inlines is null || inlines.Count == 0)
        {
            return string.Empty;
        }

        int selStart = Math.Min(block.SelectionStart, block.SelectionEnd);
        int selEnd = Math.Max(block.SelectionStart, block.SelectionEnd);

        // 无选区时默认提取全量
        bool hasSelection = selStart != selEnd;
        if (!hasSelection)
        {
            selStart = 0;
            selEnd = int.MaxValue;
        }

        return ExtractFromInlines(inlines, selStart, selEnd, kind);
    }

    /// <summary>
    /// 根据给定的字符流选区范围从 Inlines 集合中提取指定格式文本。
    /// </summary>
    public static string ExtractFromInlines(InlineCollection inlines, int selStart, int selEnd, RubyCopyKind kind)
    {
        if (inlines is null || inlines.Count == 0 || selStart >= selEnd)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        int currentOffset = 0;

        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                var text = run.Text ?? string.Empty;
                int runLen = text.Length;
                int runStart = currentOffset;
                int runEnd = currentOffset + runLen;

                int overlapStart = Math.Max(selStart, runStart);
                int overlapEnd = Math.Min(selEnd, runEnd);

                if (overlapStart < overlapEnd)
                {
                    int relStart = overlapStart - runStart;
                    int relLen = overlapEnd - overlapStart;
                    sb.Append(text.AsSpan(relStart, relLen));
                }

                currentOffset += runLen;
            }
            else if (inline is LineBreak)
            {
                int lbStart = currentOffset;
                int lbEnd = currentOffset + 1;

                if (selStart <= lbStart && selEnd >= lbEnd)
                {
                    sb.Append(Environment.NewLine);
                }

                currentOffset += 1;
            }
            else if (inline is InlineUIContainer container)
            {
                int uicPos = currentOffset;
                if (selStart <= uicPos && selEnd > uicPos)
                {
                    if (container.Child is RubyInlineControl ruby)
                    {
                        switch (kind)
                        {
                            case RubyCopyKind.Plain:
                                sb.Append(ruby.BaseText);
                                break;
                            case RubyCopyKind.Annotated:
                                sb.Append(ruby.BaseText);
                                sb.Append('[');
                                sb.Append(ruby.RubyText);
                                sb.Append(']');
                                break;
                            case RubyCopyKind.ReadingOnly:
                                sb.Append(ruby.RubyText);
                                break;
                        }
                    }
                }

                currentOffset += 1; // InlineUIContainer 占 1 字符 (U+FFFC)
            }
        }

        return sb.ToString();
    }
}
