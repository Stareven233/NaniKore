using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>
/// 内置提示词工场：下列变量即最终下发给模型的全部内容，按实际拼接顺序排列，
/// 段与段之间以空行相接，{{target_language}} 展开为请求的目标语言。
///
/// 三个必填 Base（设置页对应 base / vision_base / explain 三键，留空回落内置）：
///   {DefaultTranslationBasePrompt}  路径一 · 文本翻译（只产出译文）
///   {DefaultVisionBasePrompt}       路径二 · 视觉链路（纯转录 + 译文两段）
///   {DefaultExplainUserPrompt}      路径三 · 精讲（同时充当阶段二追问消息）
///
/// 注音职责已移交本地注音引擎（issue 008）：汉字振假名 / 拼音 / IPA 一律由结果面板
/// 在翻译流结束后单次异步叠加，不再经提示词协商。历史上 japanese_plugin /
/// english_plugin / chinese_plugin 三个语言注音插件键随之退役——旧配置中的自定义值
/// 由 <c>PromptConfig</c> 原样保留（不再参与组装），设置页不再展示这三行编辑器。
///
/// 路径一 · 文本翻译（源文本已知，只输出译文，无原文回声、无分段标记）：
///   必填  {DefaultTranslationBasePrompt}
///
/// 路径二 · 视觉一条龙（请求携带图片时整体替代路径一）：
///   必填  {DefaultVisionBasePrompt}
///   图为唯一输入，需要先转录后翻译；转录与译文两段之间以 <see cref="TranslationSentinel"/> 分隔，
///   分栏路由器据此把纯转录送进原文通道、译文送进译文通道；转录段只回文字，不经提示词协商注音。
///
/// 路径三 · 精讲（追问消息为 {DefaultExplainUserPrompt}）：
///   必填  {DefaultExplainUserPrompt}（精讲专注词汇释义与文法解析，不涉及翻译注音）
///
/// 多轮会话：请求携带 TranslationSession 时，系统提示词原样采用阶段一的锁定值——
/// 不为阶段二重拼（前缀缓存命中的前提），config.yaml 的 explain 键此时充当追问消息。
///
/// 设置页填写的对应提示词优先，留空则回落到上述内置变量；
/// 翻译阶段严禁混入精讲指令，保证首字延迟与前缀缓存稳定命中。
/// </summary>
internal static class PromptTemplates
{
    /// <summary>视觉链路两段式输出唯一的分段标记：区分纯转录与目标语言译文（仅视觉路径使用）。</summary>
    internal const string TranslationSentinel = "---TRANSLATION---";

    /// <summary>目标语言占位符；仅展开这一个，未知占位符原样保留，避免悄悄改变用户提示词语义。</summary>
    internal const string TargetLanguagePlaceholder = "{{target_language}}";

    // ── 三个必填 Base：文本 / 视觉 / 精讲 ─────────────────────────────

    /// <summary>
    /// 文本 Base：纯粹的翻译纪律与目标语言约束。原文由调用方另行持有（结果面板原文区），
    /// 模型无需回显原文、无需输出分段标记、无需承担任何注音规约（注音由本地引擎叠加）。
    /// </summary>
    internal static readonly string DefaultTranslationBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，你需要按照要求将输入准确、自然地翻译为{{target_language}}，只输出译文本身

        忠实地按文本原有格式进行翻译，追求信达雅。若原文中已有部分词句属于{{target_language}}（如专有名词、已为目标语言的短语），在译文中直接保留或自然融入，无需生硬二次转译；若整段已是{{target_language}}，忠实呈现即可
        """);

    // ── 路径二 · 视觉一条龙 Base ───────────────────────────────────

    /// <summary>
    /// 视觉 Base：两段式协议——先纯转录图中文字，再给出目标语言译文；
    /// 分段哨兵仅供分栏路由识别，注音规约一概不在此声明（本地引擎负责）。
    /// </summary>
    internal static readonly string DefaultVisionBasePrompt = PromptLiteral("""
        你当前工作在一个翻译应用中，需翻译文本都在输入图片里。请分两段输出：第一段原样转录图片中实际出现的文字，不做任何翻译、解释或增删；第二段将转录内容准确、自然地翻译为{{target_language}}。两段中间用"---TRANSLATION---"分隔，例如：

        <transcribed_text>
        ---TRANSLATION---
        <translation>

        注意 <transcribed_text> 与 <translation> 是结构占位符，输出时替换为对应内容，本身不保留。忠实呈现图片中实际出现的内容，维持原有顺序与分行；若原文中已有部分词句属于{{target_language}}，译文中直接保留或自然融入。严禁输出 Markdown 代码块标记，直接从第一段内容开始输出
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
                PromptPath.Vision => DescribeVisionSections(prompts),
                _ => DescribeTextSections(prompts),
            };

    /// <summary>路径一 · 文本翻译的组成：只有翻译 Base 一段（注音已移交本地引擎，不再有条件插件）。</summary>
    private static List<PromptSection> DescribeTextSections(PromptConfig? prompts) =>
        [Choose("base", prompts?.Base, DefaultTranslationBasePrompt)];

    /// <summary>
    /// 路径二 · 视觉链路的组成：只有视觉 Base 一段。
    /// 图中语言无从预扫、且注音改由本地引擎在转录落地后叠加，模型侧不再需要任何语言插件；
    /// 即便目标语言为中文（本地拼音引擎对日文汉字同样生效），system prompt 仍是唯一单变体，前缀缓存命中不受损。
    /// </summary>
    private static List<PromptSection> DescribeVisionSections(PromptConfig? prompts) =>
        [Choose("vision_base", prompts?.VisionBase, DefaultVisionBasePrompt)];

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
    /// <summary>路径一 · 文本翻译：只含翻译 Base 一段，直接产出译文。</summary>
    Text,

    /// <summary>路径二 · 视觉链路：只含视觉 Base 一段，先纯转录再译文，哨兵分段。</summary>
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
