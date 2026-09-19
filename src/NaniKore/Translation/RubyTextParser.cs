using System.Text;

namespace NaniKore.Translation;

/// <summary>
/// 振假名/注音段落表示：普通文本时 Ruby 为 null，注音词汇时 Text 为底字，Ruby 为注音假名/音标。
/// </summary>
internal readonly record struct RubySegment(string Text, string? Ruby = null)
{
    public bool IsRuby => !string.IsNullOrEmpty(Ruby);
}

/// <summary>
/// 假名注音语法解析器：解析“漢字[かな]”规约，支持汉字底字智能回溯、纯文本提取、读音提取与格式互转。
/// </summary>
internal static class RubyTextParser
{
    /// <summary>
    /// 对模型流式或 OCR 产生的非标注音文本进行容错清洗与规范化：
    /// 1. 清理汉字/字母与 [ 之间的悬挂问号或噪点（如“亡?[な]” -> “亡[な]”）；
    /// 2. 清理方括号内部的尾随问号或首尾多余标点（如“[な?]” -> “[な]”）；
    /// 3. 清理非括号区域紧随汉字/假名后的孤立闭括号与问号（如“く?]な?]った” -> “くなった”）。
    /// </summary>
    public static string NormalizeRawAnnotatedText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = input;

        // 1. 汉字/字母/假名与开括号 [ 之间的噪点问号（如 亡?[な] -> 亡[な]）
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"([\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff\u3040-\u30ff\w\d])\s*[?？]+\s*\[",
            "$1[");

        // 2. 假名括号内部的尾随问号（如 [な?] -> [な]）
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[\s*([^\]\r\n]+?)\s*[?？]+\s*\]",
            "[$1]");

        // 3. 孤立闭括号与问号清洗（如 く?]、な?]、した] 等不在 [...] 内部的孤立闭括号）
        var sb = new StringBuilder(text.Length);
        var inBracket = false;
        for (var idx = 0; idx < text.Length; idx++)
        {
            var ch = text[idx];
            if (ch == '[')
            {
                inBracket = true;
                sb.Append(ch);
            }
            else if (ch == ']')
            {
                if (inBracket)
                {
                    inBracket = false;
                    sb.Append(ch);
                }
                else
                {
                    // 孤立闭括号！检查前面如果是刚刚添加的问号，顺带剔除
                    if (sb.Length > 0 && (sb[^1] == '?' || sb[^1] == '？'))
                    {
                        sb.Length--;
                    }
                    // 丢弃孤立的 ']'
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 将包含注音方括号（如“私[わたし]は 学生[がくせい]です。”）的文本解析为结构化注音段落序列。
    /// </summary>
    public static IReadOnlyList<RubySegment> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        // 预先清洗非标或破损的括号噪点（如“亡?[な]く?]な?]った”）
        text = NormalizeRawAnnotatedText(text);

        var segments = new List<RubySegment>();
        var lastPlainIndex = 0;
        var i = 0;

        while (i < text.Length)
        {
            var openBracket = text.IndexOf('[', i);
            if (openBracket < 0)
            {
                break;
            }

            var closeBracket = text.IndexOf(']', openBracket + 1);
            if (closeBracket < 0)
            {
                break;
            }

            // 注音内容不允许跨换行
            var rubyContent = text.AsSpan(openBracket + 1, closeBracket - openBracket - 1);
            if (rubyContent.IsEmpty || rubyContent.IndexOfAny('\r', '\n') >= 0)
            {
                i = openBracket + 1;
                continue;
            }

            // 括号前必须有有效底字
            if (openBracket == 0)
            {
                i = closeBracket + 1;
                continue;
            }

            var prevChar = text[openBracket - 1];
            if (char.IsWhiteSpace(prevChar) || IsPunctuationOrSymbol(prevChar))
            {
                // 孤立括号（如 markdown 链接或行首标签），视为普通文本
                i = closeBracket + 1;
                continue;
            }

            // 智能回溯识别底字范围
            var baseStart = FindBaseWordStart(text, openBracket - 1);
            var baseWord = text[baseStart..openBracket];
            var rubyText = rubyContent.ToString().Trim('?', '？', ' ');

            // 启发式排除：代码中的数组下标（如 arr[0]、buf[index]）不是语言注音
            if (IsCodeBracket(baseWord, rubyText))
            {
                i = closeBracket + 1;
                continue;
            }

            // 如果底字前有未提交的普通文本，先加入段落
            if (baseStart > lastPlainIndex)
            {
                segments.Add(new RubySegment(text[lastPlainIndex..baseStart]));
            }

            // 加入注音段
            segments.Add(new RubySegment(baseWord, rubyText));
            lastPlainIndex = closeBracket + 1;
            i = closeBracket + 1;
        }

        if (lastPlainIndex < text.Length)
        {
            segments.Add(new RubySegment(text[lastPlainIndex..]));
        }

        return segments;
    }

    /// <summary>
    /// 剥离所有注音括号，仅保留纯正文内容（如“私[わたし]は” -> “私は”）。
    /// </summary>
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var segments = Parse(text);
        var sb = new StringBuilder(text.Length);
        foreach (var segment in segments)
        {
            sb.Append(segment.Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 提取纯读音文本：将汉字替换为注音假名，其余纯文本保持不变（如“私[わたし]は” -> “わたしは”）。
    /// </summary>
    public static string ToReadingText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var segments = Parse(text);
        var sb = new StringBuilder(text.Length);
        foreach (var segment in segments)
        {
            sb.Append(segment.Ruby ?? segment.Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 将分段序列重新拼合为带方括号注音的标准格式。
    /// </summary>
    public static string ToAnnotatedText(IEnumerable<RubySegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.IsRuby)
            {
                sb.Append(segment.Text).Append('[').Append(segment.Ruby).Append(']');
            }
            else
            {
                sb.Append(segment.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从方括号前一个字符向前回溯，识别同类字符组成的底字起点。
    /// 例如“ここは通[とお]”中的“通”，“進入禁止[しんにゅうきんし]”中的“進入禁止”。
    /// </summary>
    private static int FindBaseWordStart(string text, int endIndex)
    {
        var lastChar = text[endIndex];

        // 1. 若底字以汉字结尾：向前连续截取所有 CJK 汉字与叠字标记
        if (IsCjkKanji(lastChar))
        {
            var start = endIndex;
            while (start > 0 && IsCjkKanji(text[start - 1]))
            {
                start--;
            }
            return start;
        }

        // 2. 若底字以平假名/片假名结尾：向前连续截取假名
        if (IsKana(lastChar))
        {
            var start = endIndex;
            while (start > 0 && IsKana(text[start - 1]))
            {
                start--;
            }
            return start;
        }

        // 3. 若底字以拉丁字母结尾：向前截取英文单词
        if (char.IsAsciiLetter(lastChar))
        {
            var start = endIndex;
            while (start > 0 && (char.IsAsciiLetter(text[start - 1]) || text[start - 1] == '\''))
            {
                start--;
            }
            return start;
        }

        // 4. 其余情况取单字符
        return endIndex;
    }

    /// <summary>
    /// 判定字符是否为 CJK 汉字（包含统一汉字、扩展区、兼容区及叠字符号“々”）。
    /// </summary>
    public static bool IsCjkKanji(char c) =>
        (c >= '\u4e00' && c <= '\u9fff') || // CJK Unified Ideographs
        (c >= '\u3400' && c <= '\u4dbf') || // CJK Extension A
        (c >= '\uf900' && c <= '\ufaff') || // CJK Compatibility Ideographs
        c == '\u3005' || c == '\u3006' || c == '\u303b'; // 々, 〆, 〵

    /// <summary>
    /// 判定字符是否为日文平假名或片假名。
    /// </summary>
    public static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u309f') || // 平假名
        (c >= '\u30a0' && c <= '\u30ff') || // 片假名
        (c >= '\uff65' && c <= '\uff9f');   // 半角片假名

    /// <summary>
    /// 检查是否为标点、符号或排版空白。
    /// </summary>
    private static bool IsPunctuationOrSymbol(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c) || c is '（' or '）' or '【' or '】' or '「' or '」' or '、' or '。';

    /// <summary>
    /// 检查是否为代码中的括号索引（例如 arr[0]、items[i]）。
    /// </summary>
    private static bool IsCodeBracket(string baseWord, string rubyText)
    {
        // 若底字为纯 ASCII 标识符，且括号内全为数字，则判定为数组索引
        if (baseWord.All(char.IsAsciiLetterOrDigit) && rubyText.All(char.IsAsciiDigit))
        {
            return true;
        }

        return false;
    }
}

