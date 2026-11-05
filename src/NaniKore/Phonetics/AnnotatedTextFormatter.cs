using System.Text;
using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 注音区间 → 带方括号注音文本（底字[读音]）的唯一出口。
/// 渲染层（RubyDocumentBuilder）与下游纯文本提取（RubyTextParser.ToPlainText）都建立在该规约上；
/// 纯原文本身不持有任何注音，收藏/精讲/Session 只读纯文本，杜绝 IPA/拼音括号泄漏进下游链路。
/// </summary>
internal static class AnnotatedTextFormatter
{
    /// <summary>按区间在原文中插入 读音 方括号；区间为空时原样返回。</summary>
    public static string Compose(string plainText, IReadOnlyList<AnnotationSpan> spans)
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
            builder.Append(plainText, span.Start, span.Length);
            builder.Append('[').Append(span.Reading).Append(']');
            cursor = span.Start + span.Length;
            lastEnd = cursor;
        }

        builder.Append(plainText, cursor, plainText.Length - cursor);
        var composed = builder.ToString();

        // 绝对保真防御保障（Lossless Compose）：
        // 提取纯文本必须与原文 100% 恒等；遇到任何异常错位即刻防御性熔断回退为原文，杜绝文字丢失与乱码。
        if (!string.Equals(RubyTextParser.ToPlainText(composed), plainText, StringComparison.Ordinal))
        {
            return plainText;
        }

        return composed;
    }
}
