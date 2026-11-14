using System.Text;

namespace NaniKore.Phonetics;

/// <summary>
/// 注音区间 → 带方括号注音文本（底字[读音]）的唯一出口。
/// 方括号格式仅用于导出，不参与原文或显示结构的还原。
/// 纯原文与区间始终独立保存，收藏/精讲/Session 只读纯文本。
/// </summary>
internal static class AnnotatedTextFormatter
{
    /// <summary>按区间导出方括号注音或用读音替换底字；未注音区间（含原始换行）保持原样。</summary>
    public static string Compose(string plainText, IReadOnlyList<AnnotationSpan> spans, bool readingOnly = false)
    {
        if (string.IsNullOrEmpty(plainText) || spans.Count == 0)
        {
            return plainText;
        }

        var ordered = spans
            .Where(span => span is { Length: > 0 } && !string.IsNullOrEmpty(span.Reading))
            .OrderBy(span => span.Start)
            .ToArray();

        var builder = new StringBuilder(plainText.Length + ordered.Sum(span => span.Reading.Length + 2));
        var cursor = 0;
        var lastEnd = -1;
        foreach (var span in ordered)
        {
            if (span.Start < cursor || span.Start + span.Length > plainText.Length)
            {
                // 越界或倒退：宁可漏注也不破坏原文结构（区间默认不重叠，此处仅兜底防呆）
                continue;
            }

            if (span.Start < lastEnd)
            {
                continue;
            }

            builder.Append(plainText, cursor, span.Start - cursor);
            if (readingOnly)
            {
                builder.Append(span.Reading);
            }
            else
            {
                builder.Append(plainText, span.Start, span.Length);
                builder.Append('[').Append(span.Reading).Append(']');
            }
            cursor = span.Start + span.Length;
            lastEnd = cursor;
        }

        builder.Append(plainText, cursor, plainText.Length - cursor);
        return builder.ToString();
    }
}
