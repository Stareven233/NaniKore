using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>
/// 内置提示词工场：下列变量即最终下发给模型的全部内容，按实际拼接顺序排列，
/// 段与段之间以空行相接，{{target_language}} 展开为请求的目标语言。
///
/// 三个必填 Base（设置页对应 base / vision_base / explain 三键，留空回落内置）：
///   {DefaultTranslationBasePrompt}  路径一 · 文本翻译
///   {DefaultVisionBasePrompt}       路径二 · 视觉一条龙
///   {DefaultExplainUserPrompt}      路径三 · 精讲（同时充当阶段二追问消息）
/// 三个语言特化插件（对应 japanese_plugin / english_plugin / chinese_plugin 三键）由探测结果与目标语言互斥共同决定是否纳入。
///
/// 语言探测机制（LanguageDetector，对原文做纯内存字符扫描，< 1ms）：
///   - 含假名 → 日语；含拉丁字母 → 英语；命中简体独占字或简体偏旁（讠纟钅门马车贝页风鸟）→ 简体中文
///
/// 插件门控职责分离：探测器只回答「这篇原文要不要纳入某种语言的规约」，不判断该语言的适用范围；
/// 噪声防护（片假名外来语、日文汉字不得误标 IPA、常见汉字不得误标拼音）由语言插件的文案限定承担。
/// 因此三个门控彼此独立，日英中混排句同享振仮名、IPA 与拼音三套规约。
/// 目标语言互斥：故目标为日语/英语/中文时整条剔除对应插件；中文目标无注音插件、维持现状。
///
/// 路径一 · 文本翻译：
///   必填  {DefaultTranslationBasePrompt}
///   条件注入（探测到日语且目标语言非日语时）                {JapanesePlugin}
///   条件注入（含拉丁字母且目标语言非英语时）                {EnglishPlugin}
///   条件注入（命中简体独占字且目标语言非中文时）            {ChinesePlugin}
///
/// 路径二 · 视觉一条龙（请求携带图片时整体替代路径一）：
///   必填  {DefaultVisionBasePrompt}
///   条件注入（目标语言非日语时）              {JapanesePlugin}
///   条件注入（目标语言非英语时）              {EnglishPlugin}
///   条件注入（目标语言非中文时）              {ChinesePlugin}
///   图为唯一输入、语言探测无从做起，三个插件先统统入围成为候选，再经目标语言互斥剔除同语种插件；
///   插件的规则本身都是条件式的，模型按图中实际出现的文字逐条取舍。目标语言为中文时日英两者保留，与旧版「恒定同拼」等价；
///   即便目标为日/英语剔除其一，目标语言也是全局静态项，视觉 system prompt 仍是唯一单变体，前缀缓存命中不受损。
///
/// 路径三 · 精讲（追问消息为 {DefaultExplainUserPrompt}）：
///   必填  {DefaultExplainUserPrompt}（精讲专注词汇释义与文法解析，不注入翻译注音插件）
///
/// 多轮会话：请求携带 TranslationSession 时，系统提示词原样采用阶段一的锁定值——
/// 不为阶段二重拼（前缀缓存命中的前提），config.yaml 的 explain 键此时充当追问消息。
///
/// 设置页填写的对应提示词优先，留空则回落到上述内置变量；
/// 翻译阶段严禁混入精讲指令，保证首字延迟与前缀缓存稳定命中。
/// </summary>
internal static class PromptTemplates
{
    /// <summary>两阶段流式输出唯一的分段标记：区分标注原文与目标语言译文。</summary>
    internal const string TranslationSentinel = "---TRANSLATION---";

    /// <summary>目标语言占位符；仅展开这一个，未知占位符原样保留，避免悄悄改变用户提示词语义。</summary>
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    // ── 三个必填 Base：文本 / 视觉 / 精讲 ─────────────────────────────

    /// <summary>文本 Base：两段式输出协议、翻译纪律与目标语言约束；设置页「翻译提示词」对应项。</summary>
    internal static readonly string DefaultTranslationBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，你需要按照要求将输入准确、自然地翻译为{{target_language}}，并将注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。若原文中已有部分词句属于{{target_language}}（如专有名词、已为目标语言的短语），在译文中直接保留或自然融入，无需生硬二次转译；若整段已是{{target_language}}，忠实呈现即可。若原文无需注音或无对应注音规范，第一部分直接输出原始文本即可。忠实地按文本原有格式进行翻译，追求信达雅，严禁输出诸如【原文】、【译文】等前缀或 Markdown 代码块标记，直接从第一部分内容开始输出
        """);

    // ── 语言特化插件：日语、英语、中文依次 ─────────────────────────

    /// <summary>日语插件：约束汉字振假名注音格式；只服务原文标注，不携带精讲文法。</summary>
    internal static readonly string JapanesePlugin = PromptLiteral("""
        [日语注音规范]
        对 JLPT N3 及以上难度的日本汉字标注振假名，格式为 漢字[かな]（例：躊躇[ちゅうちょ]）
        """);

    /// <summary>英语插件：约束国际音标（IPA）标注规范，且只作用于拉丁字母拼写的单词。</summary>
    internal static readonly string EnglishPlugin = PromptLiteral("""
        [英语音标规范]
        为原文中拉丁字母拼写的少见、困难单词用方括号标注国际音标（IPA），如 ubiquitous[juːˈbɪkwɪtəs]；常见基础词、专有名词与技术缩写不标注
        """);

    /// <summary>中文插件：约束汉字拼音标注格式；目标语言即中文时整条剔除。</summary>
    internal static readonly string ChinesePlugin = PromptLiteral("""
        [中文拼音规范]
        为原文中少见、难读的汉字标注拼音，格式为 汉字[pinyin]（例：蕞尔[zuì ěr]）
        """);

    // ── 路径二 · 视觉一条龙 Base ───────────────────────────────────

    /// <summary>视觉 Base：转录图中文字并直接给出标注原文与译文；注音格式由语言插件提供，自身不重复声明。</summary>
    internal static readonly string DefaultVisionBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，需翻译文本都在输入图片里，你需要按照要求将其准确、自然地翻译为{{target_language}}，并将转录并完成注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实呈现图片中实际出现的内容，维持原有顺序与分行
        """);

    // ── 路径三 · 精讲 Base ─────────────────────────────────────────

    /// <summary>两阶段精讲的默认追问消息；同时也充当不带 session 的一次性精讲请求的 system prompt。</summary>
    internal static readonly string DefaultExplainUserPrompt = PromptLiteral("""
        按以下三个小节输出结构化 Markdown；每个条目独占一行、以「- 」开头，字段间用竖线「|」分隔，严禁输出 Markdown 表格：
        ```
        ### [词汇/短语]
        - **词汇** | 读音或音标 | 词性 | 释义

        ### [语法/句型]
        - **句型** | 语法功能与释义

        ### [精讲]
        结合语境与背景/语法知识分析解释为何如此翻译
        ```

        注：仅挑出中高级难度的重点词汇、短语与语法句型，常见基础表达不列出；
        「词汇」与「句型」第一字段只写原型本身（例：気付く、～ない），严禁在内部附带振假名或括号读音（如 気付く[きづく]、気付く (きづく)），读音只写入专属的「读音或音标」列
        """);

    /// <summary>行尾归一化：仓库按 core.autocrlf=true 检出，原始字符串字面量会继承 \r\n，统一收敛为 \n 后再下发。</summary>
    private static string PromptLiteral(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// 最终下发给模型的 system prompt：经 <see cref="DescribeSections"/> 拆段、<see cref="Compose"/> 拼接
    /// 并展开目标语言占位符，与 --dump-prompt 预览同路径，杜绝「预览一套、下发另一套」的漂移。
    /// </summary>
    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts) =>
        Compose(DescribeSections(request, prompts), request.TargetLanguage);

    /// <summary>
    /// 按请求分派组装路径，顺序与 <see cref="GetSystemPrompt"/> 一致：先精讲、再视觉、最后文本；
    /// 携带 TranslationSession 的多轮请求由 <see cref="GetSystemPrompt"/> 沿用阶段一锁定值，不经过本分派。
    /// </summary>
    internal static PromptPath ResolvePath(UnderstandRequest request) => request.Kind == UnderstandKind.Explain
        ? PromptPath.Explain
        : request.ImagePng is { Length: > 0 }
            ? PromptPath.Vision
            : PromptPath.Text;

    /// <summary>
    /// 把最终下发的 system prompt 还原为「带来源标注的有序段序列」，供 --dump-prompt CLI 与
    /// prompt-preview 生成器复现组装顺序——组装真值只此一处，前端与脚本不得自行模拟。
    /// 与 <see cref="GetSystemPrompt"/> 共用同一套分派与取值逻辑，区别只在不拼接；
    /// 段序列按 \n\n 连接并展开 {{target_language}} 后必须与 <see cref="GetSystemPrompt"/> 逐字一致
    /// （自测的段描述反漂移闸门守住）。
    /// </summary>
    internal static IReadOnlyList<PromptSection> DescribeSections(UnderstandRequest request, PromptConfig? prompts = null) =>
        // 多轮会话：系统提示词原样沿用阶段一锁定值，不为阶段二重拼，也不拆分。
        request.Session?.SystemPrompt is { Length: > 0 } sessionPrompt
            ? [new PromptSection("session", PromptSectionSource.Session, sessionPrompt)]
            : ResolvePath(request) switch
            {
                PromptPath.Explain => DescribeExplainSections(prompts),
                PromptPath.Vision => DescribeVisionSections(request.TargetLanguage, prompts),
                _ => DescribeTextSections(request, prompts),
            };

    /// <summary>
    /// 按探测结果把语言插件追加进 sections，并施加「目标语言互斥」门控剪枝：语言特化插件服务源语言学习者，
    /// 目标即日语/英语/中文时给同语种内容注音无价值，整条剔除对应插件；中文目标无注音插件、维持现状。
    /// 剪枝后目标语言仍是全局静态项，文本/视觉 prompt 各自保持唯一变体，前缀缓存命中不受损。
    /// 探测结果决定「纳入哪些规约」，目标语言决定「剔除哪些」，职责分离；三个门控彼此独立，
    /// 用户自定义插件优先、留空回落内置，文本与视觉路径共用本方法。
    /// </summary>
    private static void AppendLanguagePlugins(
        List<PromptSection> sections,
        LanguageDetectionResult detection,
        string targetLanguage,
        PromptConfig? prompts)
    {
        var japaneseIsTarget = LanguageDetector.IsJapaneseTarget(targetLanguage);
        var englishIsTarget = LanguageDetector.IsEnglishTarget(targetLanguage);
        var chineseIsTarget = LanguageDetector.IsChineseTarget(targetLanguage);

        // 探测到日语即纳入振假名规约；目标语言已是日语时剔除。
        if (!japaneseIsTarget && (detection.NeedsJapaneseRuby || detection.HasJapanese))
        {
            sections.Add(Choose("japanese_plugin", prompts?.JapanesePlugin, JapanesePlugin));
        }

        // 含拉丁字母即纳入 IPA 规约，不设是否混排日语的限制；噪声防护（片假名外来语不得误标）
        // 由插件文案「拉丁字母拼写的单词」承担；目标语言已是英语时剔除。
        if (!englishIsTarget && (detection.NeedsEnglishIpa || detection.HasEnglish))
        {
            sections.Add(Choose("english_plugin", prompts?.EnglishPlugin, EnglishPlugin));
        }

        // 命中简体独占字即纳入拼音规约；噪声防护由插件文案「少见、难读的汉字」承担；
        // 简体判定复用探测器的 SimplifiedChinese 特征位；目标语言已是中文时剔除。
        if (!chineseIsTarget && detection.HasSimplifiedChinese)
        {
            sections.Add(Choose("chinese_plugin", prompts?.ChinesePlugin, ChinesePlugin));
        }
    }

    /// <summary>路径一 · 文本翻译的组成：翻译 Base + 按探测结果纳入的语言插件。</summary>
    private static List<PromptSection> DescribeTextSections(UnderstandRequest request, PromptConfig? prompts)
    {
        var sections = new List<PromptSection> { Choose("base", prompts?.Base, DefaultTranslationBasePrompt) };
        AppendLanguagePlugins(sections, LanguageDetector.Detect(request.SourceText, request.TargetLanguage), request.TargetLanguage, prompts);
        return sections;
    }

    /// <summary>
    /// 视觉路径的探测替身：图为唯一输入、无从预扫字符，按「日英中皆可能在图中出现」给出候选，
    /// 再交由 <see cref="AppendLanguagePlugins"/> 做目标语言互斥剪枝——宁全勿缺，又不会给同语种内容乱注音。
    /// </summary>
    internal static readonly LanguageDetectionResult VisionBothLanguages =
        new(LanguageFeatures.Japanese | LanguageFeatures.English | LanguageFeatures.SimplifiedChinese,
            NeedsJapaneseRuby: false,
            NeedsEnglishIpa: false,
            IsPureHanziJapaneseCandidate: false);

    /// <summary>
    /// 路径二 · 视觉一条龙的组成：视觉 Base + 经目标语言互斥后的语言插件。
    /// 图为唯一输入、语言探测无从做起，以 <see cref="VisionBothLanguages"/> 令三个插件统统入围成为候选，
    /// 再剔除同语种插件；目标语言为中文时日英两者保留，与旧版「恒定同拼」等价。
    /// </summary>
    private static List<PromptSection> DescribeVisionSections(string targetLanguage, PromptConfig? prompts)
    {
        var sections = new List<PromptSection> { Choose("vision_base", prompts?.VisionBase, DefaultVisionBasePrompt) };
        AppendLanguagePlugins(sections, VisionBothLanguages, targetLanguage, prompts);
        return sections;
    }

    /// <summary>
    /// 路径三 · 精讲的组成：仅下发唯一的精讲提示词（用户自定义或内置回落），不注入翻译注音插件。
    /// 应用内的精讲是阶段二追问消息，这里只服务不带 session 的一次性精讲请求。
    /// </summary>
    private static List<PromptSection> DescribeExplainSections(PromptConfig? prompts) =>
        [Choose("explain", prompts?.Explain, DefaultExplainUserPrompt)];

    /// <summary>取用户自定义值：非空即标记 Custom 并裁掉首尾空白；否则回落内置常量并标记 Builtin。</summary>
    private static PromptSection Choose(string name, string? custom, string builtin) =>
        !string.IsNullOrWhiteSpace(custom)
            ? new PromptSection(name, PromptSectionSource.Custom, custom.Trim())
            : new PromptSection(name, PromptSectionSource.Builtin, builtin);

    /// <summary>
    /// 把有序段序列拼接为最终 system prompt：段间统一以 \n\n 连接，随后只展开已声明的目标语言占位符。
    /// 单段会话锁定值原样返回：其占位符在阶段一已展开，二次展开只会改变语义。
    /// </summary>
    private static string Compose(IReadOnlyList<PromptSection> sections, string targetLanguage) =>
        sections is [{ Source: PromptSectionSource.Session } session]
            ? session.Text
            : ExpandTargetLanguage(string.Join("\n\n", sections.Select(section => section.Text)), targetLanguage);

    private static string ExpandTargetLanguage(string prompt, string targetLanguage) =>
        // 只展开已声明的占位符；未知占位符原样保留，避免悄悄改变用户提示词语义。
        prompt.Replace(TargetLanguagePlaceholder, targetLanguage, StringComparison.Ordinal);
}

/// <summary>system prompt 的三条组装路径；分派顺序与 <see cref="PromptTemplates.GetSystemPrompt"/> 一一对应。</summary>
internal enum PromptPath
{
    /// <summary>路径一 · 文本翻译：Base + 按探测结果纳入的语言插件。</summary>
    Text,

    /// <summary>路径二 · 视觉一条龙：视觉 Base + 经目标语言互斥后的语言插件（中文目标时日英两者俱全）。</summary>
    Vision,

    /// <summary>路径三 · 精讲：精讲 Base 独立下发，不含翻译注音插件。</summary>
    Explain,
}

/// <summary>一段 system prompt 的取值来源：内置常量回落、用户配置覆盖，或阶段一锁定的会话值。</summary>
internal enum PromptSectionSource
{
    Builtin,
    Custom,
    Session,
}

/// <summary>
/// system prompt 的一段有序组成：段名、取值来源与文本（占位符尚未展开）；
/// 供 --dump-prompt CLI 与 prompt-preview 生成器复现组装顺序。
/// </summary>
internal sealed record PromptSection(string Name, PromptSectionSource Source, string Text);
