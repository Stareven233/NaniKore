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

    /// <summary>词性与原形（例如：动词原形、名词、辞书形）。</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>详细释义与在当前句中的语境分析。</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>是否已被收藏（联动收藏服务字符串匹配）。</summary>
    public bool IsFavorited { get; set; }

    /// <summary>转换为切片 5 SQLite 规范的标准分层纯文本备注。</summary>
    public string ToFavoriteNote()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Reading) && Reading != "-")
        {
            sb.AppendLine($"读音: {Reading.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(Role))
        {
            sb.AppendLine($"原形: {Role.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(Meaning))
        {
            sb.AppendLine($"释义: {Meaning.Trim()}");
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

    /// <summary>转换为切片 5 SQLite 规范的标准分层纯文本备注。</summary>
    public string ToFavoriteNote()
    {
        return !string.IsNullOrWhiteSpace(Meaning)
            ? $"释义: {Meaning.Trim()}"
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

    private static readonly Regex ContextHeaderRegex = new(
        @"^#{2,4}\s*\[?(语境|地道表达|语境与地道表达|Context)\]?",
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

    private static bool TryParseVocabularyLine(string line, out ExplanationVocabularyCard card)
    {
        card = null!;
        // 匹配前缀如 - 或 * 或数字列表
        var stripped = Regex.Replace(line, @"^[-*•]\s+|\d+\.\s+", string.Empty).Trim();
        if (string.IsNullOrEmpty(stripped) || !stripped.Contains('|'))
        {
            return false;
        }

        var parts = stripped.Split('|');
        if (parts.Length < 2)
        {
            return false;
        }

        var term = CleanMarkdownFormatting(parts[0]);
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var reading = parts.Length > 1 ? CleanMarkdownFormatting(parts[1]) : string.Empty;
        var role = parts.Length > 2 ? CleanMarkdownFormatting(parts[2]) : string.Empty;
        var meaning = parts.Length > 3 ? CleanMarkdownFormatting(string.Join('|', parts, 3, parts.Length - 3)) : string.Empty;

        // 若只有 2 或 3 段时的容错调整
        if (parts.Length == 2)
        {
            meaning = reading;
            reading = string.Empty;
        }
        else if (parts.Length == 3)
        {
            meaning = role;
            role = string.Empty;
        }

        card = new ExplanationVocabularyCard
        {
            Term = term,
            Reading = reading == "-" ? string.Empty : reading,
            Role = role,
            Meaning = meaning
        };
        return true;
    }

    private static bool TryParseGrammarLine(string line, out ExplanationGrammarCard card)
    {
        card = null!;
        var stripped = Regex.Replace(line, @"^[-*•]\s+|\d+\.\s+", string.Empty).Trim();
        if (string.IsNullOrEmpty(stripped) || !stripped.Contains('|'))
        {
            return false;
        }

        var parts = stripped.Split('|');
        if (parts.Length < 2)
        {
            return false;
        }

        var pattern = CleanMarkdownFormatting(parts[0]);
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

    private static string CleanMarkdownFormatting(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = input.Trim();
        // 去除外层的 **粗体** 或 *斜体* 或 `代码`
        text = Regex.Replace(text, @"^\*\*([^*]+)\*\*$", "$1");
        text = Regex.Replace(text, @"^\*([^*]+)\*$", "$1");
        text = Regex.Replace(text, @"^`([^`]+)`$", "$1");
        return text.Trim();
    }
}

