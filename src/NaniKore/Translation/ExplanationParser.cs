namespace NaniKore.Translation;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 词汇与短语卡片数据模型。
/// </summary>
public sealed class ExplanationVocabularyCard
{
    /// <summary>词汇或短语名称（已去除 Markdown 加粗）。</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>读音假名或国际音标 (IPA)。</summary>
    public string Reading { get; set; } = string.Empty;

    /// <summary>词性与原形（例如：动词原形、名词、辞書形）。</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>详细释义与在当前句中的语境分析。</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>是否已被收藏（联动收藏服务字符串匹配）。</summary>
    public bool IsFavorited { get; set; }

    /// <summary>
    /// 转换为收藏备注：每行一项纯文本（读音 / 词性原形 / 释义），不写「读音：」这类字段前缀——
    /// 前缀会让收藏页搜索「读音」「释义」等无意义提示词命中全部条目。
    /// </summary>
    public string ToFavoriteNote()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Reading) && Reading != "-")
        {
            sb.AppendLine(Reading.Trim());
        }
        if (!string.IsNullOrWhiteSpace(Role))
        {
            sb.AppendLine(Role.Trim());
        }
        if (!string.IsNullOrWhiteSpace(Meaning))
        {
            sb.AppendLine(Meaning.Trim());
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 语法与句型卡片数据模型。
/// </summary>
public sealed class ExplanationGrammarCard
{
    /// <summary>句型或语法点名称。</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>语法功能与释义分析。</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>是否已被收藏（联动收藏服务字符串匹配）。</summary>
    public bool IsFavorited { get; set; }

    /// <summary>转换为收藏备注：仅释义正文一行，不写「释义：」前缀（同词汇卡片口径）。</summary>
    public string ToFavoriteNote()
    {
        return !string.IsNullOrWhiteSpace(Meaning)
            ? Meaning.Trim()
            : string.Empty;
    }
}

/// <summary>
/// 深度精讲微结构化解析文档。
/// </summary>
public sealed class ExplanationDocument
{
    /// <summary>词汇与短语卡片列表。</summary>
    public List<ExplanationVocabularyCard> VocabularyCards { get; } = new();

    /// <summary>语法与句型卡片列表。</summary>
    public List<ExplanationGrammarCard> GrammarCards { get; } = new();

    /// <summary>语境与地道表达自然分析文本。</summary>
    public string ContextExplanation { get; set; } = string.Empty;

    /// <summary>模型输出的原始 Markdown 全量文本。</summary>
    public string RawMarkdown { get; set; } = string.Empty;

    /// <summary>是否成功识别为微结构化格式；若否，UI 将平滑降级为纯文本排版展示。</summary>
    public bool IsStructured { get; set; }
}

/// <summary>
/// 深度精讲微结构化卡片解析器。
/// 具备流式容错、多变体前缀识别与状态机行切分能力。
/// </summary>
public static class ExplanationParser
{
    private enum SectionKind
    {
        None,
        Vocabulary,
        Grammar,
        Context
    }

    private static readonly Regex VocabularyHeaderRegex = new(
        @"^#{2,4}\s*\[?(词汇|短语|重点词汇|Vocabulary)\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GrammarHeaderRegex = new(
        @"^#{2,4}\s*\[?(语法|句型|文法|Grammar)\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ListPrefixRegex = new(
        @"^[-*•]\s+|\d+\.\s+",
        RegexOptions.Compiled);

    /// <summary>剥离列表条目行首的 - * • 或有序序号前缀。</summary>
    private static string StripListPrefix(string line) =>
        ListPrefixRegex.Replace(line, string.Empty).Trim();

    // 「精讲」（旧版「翻译精讲」）是内置精讲提示词采用的第三个小节名，与「语境与地道表达」同属语境分析段，一并纳入识别
    private static readonly Regex ContextHeaderRegex = new(
        @"^#{2,4}\s*\[?(语境|地道表达|语境与地道表达|翻译精讲|精讲|Context)\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析 Markdown 格式的精讲输出为结构化卡片文档。
    /// </summary>
    public static ExplanationDocument Parse(string markdown)
    {
        var doc = new ExplanationDocument
        {
            RawMarkdown = markdown ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.IsStructured = false;
            return doc;
        }

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var currentSection = SectionKind.None;
        var contextBuilder = new StringBuilder();
        var recognizedSectionCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                if (currentSection == SectionKind.Context && contextBuilder.Length > 0)
                {
                    contextBuilder.AppendLine();
                }
                continue;
            }

            // 检查是否进入新分区
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (VocabularyHeaderRegex.IsMatch(line))
                {
                    currentSection = SectionKind.Vocabulary;
                    recognizedSectionCount++;
                    continue;
                }
                if (GrammarHeaderRegex.IsMatch(line))
                {
                    currentSection = SectionKind.Grammar;
                    recognizedSectionCount++;
                    continue;
                }
                if (ContextHeaderRegex.IsMatch(line))
                {
                    currentSection = SectionKind.Context;
                    recognizedSectionCount++;
                    continue;
                }
            }

            switch (currentSection)
            {
                case SectionKind.Vocabulary:
                    if (TryParseVocabularyLine(line, out var vocabCard))
                    {
                        doc.VocabularyCards.Add(vocabCard);
                    }
                    break;

                case SectionKind.Grammar:
                    if (TryParseGrammarLine(line, out var grammarCard))
                    {
                        doc.GrammarCards.Add(grammarCard);
                    }
                    break;

                case SectionKind.Context:
                    contextBuilder.AppendLine(line);
                    break;

                case SectionKind.None:
                default:
                    // 在尚未进入显式分区前，如果遇到符合词汇或语法行特征的条目，也允许启发式捕获
                    if (TryParseVocabularyLine(line, out var earlyVocab))
                    {
                        doc.VocabularyCards.Add(earlyVocab);
                    }
                    break;
            }
        }

        doc.ContextExplanation = contextBuilder.ToString().Trim();

        // 判定是否微结构化成功：
        // 只要匹配到了至少一个分区标题，或提取到了词汇/语法卡片，即视为结构化输出
        doc.IsStructured = recognizedSectionCount > 0 || doc.VocabularyCards.Count > 0 || doc.GrammarCards.Count > 0;

        return doc;
    }

    /// <summary>
    /// 日本语教育语法 (日本語教育文法) 词性自动归一化。
    /// 统一将“一段动词”、“他动词”、“五段活用”等复杂或传统术语收敛为：
    /// 一类动词、二类动词、三类动词、一类形容词、二类形容词、名词、副词等纯净标签。
    /// </summary>
    public static string NormalizeEducationalRole(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var text = input.Trim();

        // 形容词：一类（い形容词）、二类（な形容词/形容动词）
        if (text.Contains("い形", StringComparison.Ordinal) || text.Contains("一类形容词", StringComparison.Ordinal))
        {
            return "一类形容词";
        }
        if (text.Contains("な形", StringComparison.Ordinal) || text.Contains("形容动词", StringComparison.Ordinal) || text.Contains("二类形容词", StringComparison.Ordinal))
        {
            return "二类形容词";
        }
        if (text.Contains("形容词", StringComparison.Ordinal) || text.Equals("adj", StringComparison.OrdinalIgnoreCase))
        {
            return "形容词";
        }

        // 动词：二类（一段）、一类（五段）、三类（サ变/カ变）
        if (text.Contains("一段", StringComparison.Ordinal) || text.Contains("二类", StringComparison.Ordinal) || text.Contains("Ⅱ类", StringComparison.Ordinal))
        {
            return "二类动词";
        }
        if (text.Contains("五段", StringComparison.Ordinal) || text.Contains("一类", StringComparison.Ordinal) || text.Contains("Ⅰ类", StringComparison.Ordinal))
        {
            return "一类动词";
        }
        if (text.Contains("サ变", StringComparison.Ordinal) || text.Contains("カ变", StringComparison.Ordinal) || text.Contains("三类", StringComparison.Ordinal) || text.Contains("Ⅲ类", StringComparison.Ordinal))
        {
            return "三类动词";
        }
        if (text.Contains("动词", StringComparison.Ordinal) || text.Equals("v", StringComparison.OrdinalIgnoreCase) || text.Equals("verb", StringComparison.OrdinalIgnoreCase))
        {
            return "动词";
        }

        // 其它常见现代词性
        if (text.Contains("名词", StringComparison.Ordinal) || text.Equals("n", StringComparison.OrdinalIgnoreCase) || text.Equals("noun", StringComparison.OrdinalIgnoreCase))
        {
            return "名词";
        }
        if (text.Contains("副词", StringComparison.Ordinal) || text.Equals("adv", StringComparison.OrdinalIgnoreCase))
        {
            return "副词";
        }
        if (text.Contains("连词", StringComparison.Ordinal) || text.Contains("接续词", StringComparison.Ordinal))
        {
            return "连词";
        }
        if (text.Contains("助词", StringComparison.Ordinal))
        {
            return "助词";
        }
        if (text.Contains("叹词", StringComparison.Ordinal) || text.Contains("感叹", StringComparison.Ordinal))
        {
            return "感叹词";
        }
        if (text.Contains("短语", StringComparison.Ordinal) || text.Contains("惯用", StringComparison.Ordinal))
        {
            return "短语";
        }

        // 剔除他动词/自动词等多余说明修饰
        text = text.Replace("他动词", "", StringComparison.Ordinal)
                   .Replace("自动词", "", StringComparison.Ordinal)
                   .Trim(',', '，', '、', ' ');

        return text.Length > 8 ? text[..8] : text;
    }

    private static bool IsRoleIndicator(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.Contains("动词", StringComparison.Ordinal) ||
               t.Contains("名词", StringComparison.Ordinal) ||
               t.Contains("形容", StringComparison.Ordinal) ||
               t.Contains("副词", StringComparison.Ordinal) ||
               t.Contains("连词", StringComparison.Ordinal) ||
               t.Contains("助词", StringComparison.Ordinal) ||
               t.Contains("叹词", StringComparison.Ordinal) ||
               t.Contains("一段", StringComparison.Ordinal) ||
               t.Contains("五段", StringComparison.Ordinal) ||
               t.Contains("サ变", StringComparison.Ordinal) ||
               t.Contains("カ变", StringComparison.Ordinal) ||
               t.Equals("v", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("verb", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("n", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("noun", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("adj", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("adv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeReading(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if ((t.StartsWith('/') && t.EndsWith('/')) || (t.StartsWith('[') && t.EndsWith(']'))) return true;
        return t.All(c => RubyTextParser.IsKana(c) || c is '・' or 'ー' or ' ' or '-');
    }

    private static bool TryParseVocabularyLine(string line, out ExplanationVocabularyCard card)
    {
        card = null!;
        var stripped = StripListPrefix(line);
        if (string.IsNullOrEmpty(stripped) || !stripped.Contains('|'))
        {
            return false;
        }

        var rawParts = stripped.Split('|').Select(CleanMarkdownFormatting).ToArray();
        if (rawParts.Length < 2)
        {
            return false;
        }

        // 提示词已禁止词条内嵌注音，但模型随机性仍可能写出 気付く[きづく]；
        // 解析层兜底剥离，杜绝与 DisplayTitle 追加的读音形成「気付く[きづく] (きづく)」双层展示。
        var term = StripEmbeddedRuby(rawParts[0], out var embeddedReading);
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        string reading = string.Empty;
        string role = string.Empty;
        string meaning = string.Empty;

        if (rawParts.Length == 2)
        {
            if (IsRoleIndicator(rawParts[1]))
            {
                role = NormalizeEducationalRole(rawParts[1]);
            }
            else
            {
                meaning = rawParts[1];
            }
        }
        else if (rawParts.Length == 3)
        {
            var part1 = rawParts[1];
            var part2 = rawParts[2];

            if (IsRoleIndicator(part1))
            {
                role = NormalizeEducationalRole(part1);
                meaning = part2;
            }
            else if (LooksLikeReading(part1))
            {
                reading = part1;
                if (IsRoleIndicator(part2))
                {
                    role = NormalizeEducationalRole(part2);
                }
                else
                {
                    meaning = part2;
                }
            }
            else
            {
                if (part1.Length <= 10 && !part2.Contains('。'))
                {
                    role = NormalizeEducationalRole(part1);
                    meaning = part2;
                }
                else
                {
                    meaning = part1 + " " + part2;
                }
            }
        }
        else
        {
            var part1 = rawParts[1];
            var part2 = rawParts[2];
            var part3 = string.Join('|', rawParts.Skip(3));

            if (IsRoleIndicator(part1) && !IsRoleIndicator(part2))
            {
                role = NormalizeEducationalRole(part1);
                meaning = $"{part2} | {part3}".Trim(' ', '|');
            }
            else
            {
                reading = LooksLikeReading(part1) ? part1 : (part1 == "-" ? string.Empty : part1);
                role = NormalizeEducationalRole(part2);
                meaning = part3;
            }
        }

        // 词条内嵌读音与读音列去重：两处一致时只保留读音列一份；读音列缺失时以内嵌读音兜底，
        // 既消灭重复展示，也不丢失原本只写在内嵌括号里的读音信息。
        if (!string.IsNullOrEmpty(embeddedReading))
        {
            reading = string.IsNullOrEmpty(reading) ? embeddedReading : reading;
        }

        if (reading == "-" || string.Equals(reading, term, StringComparison.OrdinalIgnoreCase))
        {
            reading = string.Empty;
        }

        card = new ExplanationVocabularyCard
        {
            Term = term,
            Reading = reading,
            Role = role,
            Meaning = meaning
        };
        return true;
    }

    private static bool TryParseGrammarLine(string line, out ExplanationGrammarCard card)
    {
        card = null!;
        var stripped = StripListPrefix(line);
        if (string.IsNullOrEmpty(stripped) || !stripped.Contains('|'))
        {
            return false;
        }

        var parts = stripped.Split('|');
        if (parts.Length < 2)
        {
            return false;
        }

        // 语法卡片同样只有一个「句型」字段，模型附带的振假名一律剥离且不做保留（无读音列可去重）。
        var pattern = StripEmbeddedRuby(CleanMarkdownFormatting(parts[0]), out _);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var meaning = CleanMarkdownFormatting(string.Join('|', parts, 1, parts.Length - 1));

        card = new ExplanationGrammarCard
        {
            Pattern = pattern,
            Meaning = meaning
        };
        return true;
    }

    // 单元格外层 Markdown 包裹：粗体、斜体与行内代码三选一，整体剥离
    private static readonly Regex MarkdownWrapRegex = new(
        @"^\*\*([^*]+)\*\*$|^\*([^*]+)\*$|^`([^`]+)`$",
        RegexOptions.Compiled);

    /// <summary>
    /// 注音括号内允许的字符集合：假名、长音符号、中黑与空白。
    /// 英文 IPA 含 ˈ、ː 等非假名字符，代码下标为数字，[语法/句型] 式标签含汉字与斜杠，均会被此判定排除。
    /// </summary>
    private static bool IsRubyOnlyContent(string content) => RubyTextParser.IsRubyAnnotationContent(content);

    /// <summary>
    /// 剥离词条/句型第一字段内模型擅自附带的注音形态：<c>漢字[かな]</c> 与 <c>漢字 (かな)</c>（半角/全角括号、括号前允许一个空格）。
    /// 误伤防护（与 RubyTextParser.IsCodeBracket 同思路）：括号前必须是汉字或假名底字——
    /// 拉丁字母或数字作底字一律视为代码/IPA 语境（如 <c>arr[0]</c>、<c>ubiquitous[juːˈbɪkwɪtəs]</c>）原样保留；
    /// 行首或空白后的孤立方括号（如 <c>[语法/句型]</c> 标签）同样保留。
    /// 与收藏归一化（<c>FavoriteContentNormalizer</c>）共用同一套剥离实现，杜绝两处判据漂移。
    /// </summary>
    private static string StripEmbeddedRuby(string term, out string embeddedReading) =>
        RubyTextParser.StripRubyAnnotations(term, out embeddedReading);

    private static string CleanMarkdownFormatting(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // 去除外层的 **粗体** 或 *斜体* 或 `代码`
        return MarkdownWrapRegex.Replace(input.Trim(), "$1$2$3").Trim();
    }
}

