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
/// 两个语言特化插件（对应 japanese_plugin / english_plugin 两键）由探测结果决定是否纳入。
///
/// 语言探测机制（LanguageDetector，对原文做纯内存字符扫描，< 1ms）：
///   - 含假名 → 日语；含拉丁字母 → 英语；命中简体独占字或简体偏旁（讠纟钅门马车贝页风鸟）→ 简体中文，不注音；
///   - 无假名的纯汉字且目标语言为中文 → 判定纯汉字日文（進入禁止、案内図），注入日语注音；目标语言非中文时不注入；
///   - 空文本（图像场景原文未到时）且目标语言为中文 → 兜底推荐日语注音插件；英语在空文本下不注入。
///
/// 插件门控职责分离：探测器只回答「这篇原文要不要纳入某种语言的规约」，不判断该语言的适用范围；
/// 噪声防护（片假名外来语、日文汉字不得误标 IPA）由英语插件的文案限定承担。
/// 因此两个门控彼此独立，日英混排句同享振仮名与 IPA 两套规约。
///
/// 路径一 · 文本翻译：
///   必填  {DefaultTranslationBasePrompt}
///   条件注入（探测到日语时）                {JapanesePlugin}
///   条件注入（含拉丁字母时）                {EnglishPlugin}
///
/// 路径二 · 视觉一条龙（请求携带图片时整体替代路径一）：
///   必填  {DefaultVisionBasePrompt}
///   必填  {JapanesePlugin}
///   必填  {EnglishPlugin}
///   图为唯一输入，语言探测无从做起，两个插件恒定同拼；两者的规则本身都是条件式的
///   （「对日式汉字标注振假名」/「为原文中拉丁字母拼写的单词标注 IPA」），模型按图中
///   实际出现的文字逐条取舍。附带收益：视觉 system prompt 与输入无关、恒定单变体，
///   前缀缓存 100% 命中（路径一有 ≤3 个变体）。
///
/// 路径三 · 精讲（追问消息为 {DefaultExplainUserPrompt}）：
///   必填  {DefaultExplainUserPrompt}（精讲专注词汇释义与文法解析，不注入翻译注音插件）
///
/// 多轮会话：请求携带 TranslationSession 时，系统提示词原样采用阶段一的锁定值，
/// 不为阶段二重拼——前缀缓存命中的前提；config.yaml 的 explain 键此时充当追问消息。
///
/// 设置页填写的对应提示词优先，留空则回落到上述内置变量；
/// 翻译阶段严禁混入精讲指令，保证首字延迟与前缀缓存稳定命中。
/// </summary>
internal static class PromptTemplates
{
    /// <summary>两阶段流式输出唯一的分段标记：区分标注原文与目标语言译文。</summary>
    internal const string TranslationSentinel = "---TRANSLATION---";

    /// <summary>目标语言占位符；仅展开已声明的这一个，未知占位符原样保留，避免悄悄改变用户提示词语义。</summary>
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    // ── 三个必填 Base：文本 / 视觉 / 精讲 ─────────────────────────────

    /// <summary>文本 Base：两段式输出协议、翻译纪律与目标语言约束；设置页「翻译提示词」展示与恢复默认即此变量。</summary>
    internal static readonly string DefaultTranslationBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，所有的输入都是需要翻译的文本，你需要按照要求将其准确、自然地翻译为{{target_language}}，并将注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实地按文本原有格式进行翻译，追求信达雅

        译文/分析全部使用{{target_language}}撰写
        """);

    // ── 语言特化插件：日语在前、英语在后 ─────────────────────────────

    /// <summary>日语插件：约束汉字振假名注音格式；只服务原文标注，不携带精讲文法。</summary>
    internal static readonly string JapanesePlugin = PromptLiteral("""
        [日语注音规范]
          - 对日式汉字标注振假名，格式为 漢字[かな]（例：私[わたし]は 学生[がくせい]です。）
        """);

    /// <summary>英语插件：约束国际音标（IPA）标注规范，且只作用于拉丁字母拼写的单词。</summary>
    internal static readonly string EnglishPlugin = PromptLiteral("""
        [英语音标规范]
          - 为原文中拉丁字母拼写的少见、困难单词用方括号标注国际音标（IPA），如 apple[ˈæpl]
        """);

    // ── 路径二 · 视觉一条龙 Base（与两个语言插件共同组装） ───────────

    /// <summary>视觉 Base：转录图中文字并直接给出标注原文与译文；注音格式由两个语言插件提供，自身不重复声明。</summary>
    internal static readonly string DefaultVisionBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，所有输入图片都是需翻译文本的截图，你需要按照要求将其准确、自然地翻译为{{target_language}}，并将转录并完成注音标注的原文、对应译文按顺序输出，两部分中间用"---TRANSLATION---"分隔，例如：

        <annotated_source>
        ---TRANSLATION---
        <translation>

        注意 <annotated_source> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实呈现图片中实际出现的内容，维持原有顺序与分行
        """);

    // ── 路径三 · 精讲 Base（职责 + 结构规约 + 目标语言约束） ──────────

    /// <summary>两阶段精讲的默认追问消息；同时也充当不带 session 的一次性精讲请求的 system prompt。</summary>
    internal static readonly string DefaultExplainUserPrompt = PromptLiteral("""
        按以下三个小节输出结构化 Markdown：

        ### [词汇/短语]
          - **词汇** | 假名读音或IPA | 词性 | 详细释义与在句中的语境分析

        ### [语法/句型]
          - **句型** | 语法功能与释义

        ### [翻译精讲]
          结合语境与背景/语法知识分析解释为何如此翻译

        注：全部使用教育语法术语，仅列出 JLPT N3 及以上难度的重点词汇、短语与语法句型
        """);

    /// <summary>行尾归一化闸门：仓库按 core.autocrlf=true 检出，原始字符串字面量会继承 \r\n，统一收敛为 \n 后再下发。</summary>
    private static string PromptLiteral(string raw) =>
        raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// 最终下发给模型的 system prompt。统一经 <see cref="DescribeSections"/> 拆段、
    /// <see cref="Compose"/> 拼接并展开目标语言占位符，杜绝「预览一套、下发另一套」的漂移。
    /// </summary>
    public static string GetSystemPrompt(UnderstandRequest request, PromptConfig prompts) =>
        Compose(DescribeSections(request, prompts), request.TargetLanguage);

    /// <summary>
    /// 按请求分派组装路径，顺序与 <see cref="GetSystemPrompt"/> 完全一致：先精讲、再视觉、最后文本。
    /// 携带 TranslationSession 的多轮请求由 <see cref="GetSystemPrompt"/> 直接沿用阶段一锁定值，不经过本分派。
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
    /// 段序列按 \n\n 连接并展开 {{target_language}} 后必须与 <see cref="GetSystemPrompt"/> 逐字一致，
    /// 该契约由自测的段描述反漂移闸门守住。
    /// </summary>
    internal static IReadOnlyList<PromptSection> DescribeSections(UnderstandRequest request, PromptConfig? prompts = null) =>
        // 多轮会话：系统提示词原样沿用阶段一锁定值，不为阶段二重拼（前缀缓存命中的前提），自然也不拆分。
        request.Session?.SystemPrompt is { Length: > 0 } sessionPrompt
            ? [new PromptSection("session", PromptSectionSource.Session, sessionPrompt)]
            : ResolvePath(request) switch
            {
                PromptPath.Explain => DescribeExplainSections(prompts),
                PromptPath.Vision => DescribeVisionSections(prompts),
                _ => DescribeTextSections(request, prompts),
            };

    /// <summary>
    /// 按探测结果把语言插件追加进 sections：日语门控与英语门控彼此独立，
    /// 用户自定义插件优先，留空回落到内置常量。文本路径专用。
    /// </summary>
    private static void AppendLanguagePlugins(
        List<PromptSection> sections,
        LanguageDetectionResult detection,
        PromptConfig? prompts)
    {
        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            sections.Add(Choose("japanese_plugin", prompts?.JapanesePlugin, JapanesePlugin));
        }

        // 英语插件门控独立化：曾以 !HasJapanese 排他，防止片假名外来语被误标 IPA，
        // 代价是日英混排句（Appleの新しいiPhone発表会）的英语部分拿不到任何 IPA 指引。
        // 现由插件文案限定「拉丁字母拼写的单词」承担噪声防护，门控只回答
        // 「这篇原文要不要纳入英语规约」——含拉丁字母就要，与是否混排日语无关。
        if (detection.NeedsEnglishIpa || detection.HasEnglish)
        {
            sections.Add(Choose("english_plugin", prompts?.EnglishPlugin, EnglishPlugin));
        }
    }

    /// <summary>路径一 · 文本翻译的组成：翻译 Base + 按探测结果纳入的语言插件。</summary>
    private static List<PromptSection> DescribeTextSections(UnderstandRequest request, PromptConfig? prompts)
    {
        var sections = new List<PromptSection> { Choose("base", prompts?.Base, DefaultTranslationBasePrompt) };
        AppendLanguagePlugins(sections, LanguageDetector.Detect(request.SourceText, request.TargetLanguage), prompts);
        return sections;
    }

    /// <summary>路径二 · 视觉一条龙的组成：视觉 Base + 两个语言插件，恒定同拼、宁全勿缺。</summary>
    private static List<PromptSection> DescribeVisionSections(PromptConfig? prompts) =>
    [
        Choose("vision_base", prompts?.VisionBase, DefaultVisionBasePrompt),
        Choose("japanese_plugin", prompts?.JapanesePlugin, JapanesePlugin),
        Choose("english_plugin", prompts?.EnglishPlugin, EnglishPlugin),
    ];

    /// <summary>
    /// 路径三 · 精讲的组成：仅下发唯一的精讲提示词（用户自定义或内置回落），
    /// 不注入翻译注音插件，与两阶段阶段二追问契约保持纯粹一致。
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

    /// <summary>路径二 · 视觉一条龙：视觉 Base + 两个语言插件恒定同拼。</summary>
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
/// system prompt 的一段有序组成：段名、取值来源与文本（占位符尚未展开）。
/// 供 --dump-prompt CLI 与 prompt-preview 生成器复现组装顺序，是「所见即所发」的真值载体。
/// </summary>
internal sealed record PromptSection(string Name, PromptSectionSource Source, string Text);
