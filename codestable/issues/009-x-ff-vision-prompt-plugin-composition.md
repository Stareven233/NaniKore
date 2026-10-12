---
kind: issue
title: "视觉一条龙改为 Base+语言插件动态组装，补齐注音格式规约"
type: ff
status: closed
priority: normal
created: 2026-09-24
---

# 视觉一条龙改为 Base+语言插件动态组装，补齐注音格式规约

> **读者：** 负责 `PromptTemplates` 三路径组装与自测的开发者。
> **主人 2026-09-24 决议（随本 issue 一并落地）：** ① `prompts.vision_oneshot` 更名为 `prompts.vision_base`；② 删除 `LanguageAlignmentRule` 与 `ExplainLanguageAlignmentRule`，目标语言约束并入各 Base；③ `ExplainRolePrompt` + `ExplainStructureRule` 合并为单一必填字面量 `DefaultExplainBasePrompt`；④ 不回补视觉角色首句。
> **耦合关系：** 依赖 `010-x-ff-mixed-language-independent-plugin-gating.md` 的英语插件文案修正（"拉丁字母拼写的"限定）；与其共享同一批插件常量，**010 需先落地**（010 已于 2026-09-24 关闭：门控独立化 + 英语文案限定 + 组装级自测均已落地，见该文「做了什么」）。
> **结论：** 已按方案落地（2026-09-24），文末附实施记录；唯一未闭环项是生成器重跑——它依赖 011 的 `scripts/prompt-preview.html`（仓库中尚不存在），已用临时桩文件验证机械同步通过。
> **关联文件：**
> - `src/NaniKore/Translation/PromptTemplates.cs`（顶层 doc 注释、`DefaultVisionBasePrompt`、`DefaultExplainBasePrompt`、`BuildVisionSystemPrompt`、两个 `Build*` 方法、删除的对齐常量）
> - `src/NaniKore/Config/AppConfig.cs` / `config.yaml`（`VisionOneshot` → `VisionBase`）
> - `src/NaniKore/App/Settings/TranslationPage.xaml(.cs)`（属性名与"视觉 Base"措辞）
> - `src/NaniKore/Diagnostics/SelfTest.Translation.cs`（视觉/精讲断言、组装样本、字面量清单）
> - `scripts/update-prompt-preview.ps1` + `scripts/prompt-preview.html`（required 清单同步改名，见 `011-x-ff-prompt-preview-html-and-dump-prompt.md`）

## 背景

三条路径的提示词组装现状（`PromptTemplates.GetSystemPrompt`）：

- 路径一（文本）：`DefaultTranslationBasePrompt` + 条件注入 `JapanesePlugin` / `EnglishPlugin` + `LanguageAlignmentRule`；
- 路径二（视觉）：仅 `DefaultVisionOneshotPrompt` 一个自包含字符串，不跑语言探测、不拼插件（该路径无原文可探测，设计上成立）；
- 路径三（精讲）：`ExplainRolePrompt` + `ExplainStructureRule` + 条件注入两个插件 + `ExplainLanguageAlignmentRule`。

## 问题描述

**当前行为：** 视觉提示词只要求"将转录并完成注音标注的原文……输出"，但**完全没有定义注音格式**——日语振假名的 `漢字[かな]` 方括号规约（路径一由 `JapanesePlugin` 提供）与英语 IPA 规约（路径一由 `EnglishPlugin` 提供）在视觉路径双双缺失。

**后果：** 视觉模式（`vision_oneshot`）下模型自由发挥注音格式（如 `私(わたし)`），下游 `RubyTextParser.Parse` 严格按 `字[かな]` 解析，原文区假名富文本排版解析失败或退化为纯文本；两条输入路径输出格式不一致。暂存区中 `PromptTemplates.cs` 的 doc 注释已自标此TODO（"注音标注的格式完全由本提示词自行约定（待主人定夺是否补格式规约）"）。

**最初的修补设想**（硬编码追加一段规约进视觉字面量）被否定：与两个插件常量内容重复，用户自定义插件不惠及视觉路径，双份漂移风险。

**预期行为：** 视觉系统提示词由组装产生，格式规约与路径一/三**同源同文**；必填段不拆碎、可选项不重复。

## 方案（推荐）：组装而非追加

```
路径二 system prompt = VisionBase（prompts.vision_base 优先，回落 DefaultVisionBasePrompt）
                     + JapanesePlugin（prompts.japanese_plugin 优先）
                     + EnglishPlugin（prompts.english_plugin 优先）
```

1. **新增 `BuildVisionSystemPrompt(UnderstandRequest, PromptConfig?)`**：与 `BuildModularSystemPrompt` / `BuildExplainSystemPrompt` 同构（`string.Join("\n\n", sections)` 后 `ExpandTargetLanguage`）；`GetSystemPrompt` 视觉分支改调它。
2. **两个插件无条件拼接**：视觉路径无原文，探测无从做起；两条插件的规则是条件式的（"对汉字标注振假名" / "为原文中拉丁字母拼写的少见、困难单词标注IPA"），对纯英语图或纯日语图均无错误行为，对日英混排图（游戏 CG、菜单，高频场景）两条规则各取所需。附带收益：视觉 system prompt **与输入无关、恒定单变体**，前缀缓存 100% 命中（路径一有 ≤4 个变体）。
3. **`prompts.vision_oneshot` → `prompts.vision_base` 键更名**：
   - 语义从"整段提示词 verbatim 覆盖"变为"视觉 Base，与语言插件共同组装"，与 `prompts.base` 对称，也消除了"模式名叫 vision_oneshot、提示词键也叫 vision_oneshot 但语义还要再解释"的别扭；
   - C# 属性 `PromptConfig.VisionOneshot` → `VisionBase`；内置常量 `DefaultVisionOneshotPrompt` → `DefaultVisionBasePrompt`；`update-prompt-preview.ps1` 的 required 清单、设置页"恢复默认"、`SelfTest` 引用一并机械替换；
   - **迁移**：`config.yaml` 模板键改名；用户旧配置里的 `vision_oneshot` 键反序列化后无对应属性、被忽略并回落空串（= 用内置视觉 Base）。默认配置无实际值，无损；如用户曾自定义，需在设置页重填一次（不抛异常、不改配置版本号）。
   - 注意 `pipeline.mode` 的 `vision_oneshot` **模式值不动**——那是输入模式名，与提示词键是两回事。
4. **视觉 Base 不拼对齐段**：它已内联"翻译为{{target_language}}"从句；且本次两个对齐常量整体删除（见联动变更），此条从"备选不做"变为既成事实。
5. **不动 `LanguageDetector`**：视觉路径本就不跑探测，空文本兜底逻辑只服务文本路径。

### 联动变更 A：删除两个对齐常量，约束并入 Base

`LanguageAlignmentRule`、`ExplainLanguageAlignmentRule` 判定无用（主人："都是没用的，放在 Base 就够了"），整体删除：

- `DefaultTranslationBasePrompt` 末尾追加一行（顶格，符合两空格契约中的顶格行先例）：

```
译文全部使用{{target_language}}撰写。
```

- 精讲路径的目标语言约束并入合并后的 `DefaultExplainBasePrompt`（见联动变更 B），作为 `[约束]` 的第三条 bullet：

```
  - 全部释义、语法剖析与语境说明必须使用{{target_language}}撰写
```

- `BuildModularSystemPrompt` / `BuildExplainSystemPrompt` 去掉对齐段；`GetSystemPrompt` 顶层 doc 注释的三路径描述同步改写。
- **已知副作用（已接受）**：用户自定义 `prompts.base` 时若不含目标语言句，对齐约束即缺席——"放进 Base"方案的固有代价。

### 联动变更 B：精讲两个必填段合并为一个

`ExplainRolePrompt`（职责声明）与 `ExplainStructureRule`（结构规约）合并为单一必填字面量 `DefaultExplainBasePrompt`（必填段不拆碎，与 `DefaultTranslationBasePrompt` / `DefaultVisionBasePrompt` 形成"三 Base"对称）：

```
你是严谨的外语精讲导师，擅长拆解句法、提炼重点并给出地道的解释。

按以下三个小节输出结构化 Markdown：

### [词汇/短语]
  词汇 | 假名读音或IPA | 词性 | 详细释义与在句中的语境分析

### [语法/句型]
  句型 | 语法功能与释义

### [翻译精讲]
  结合语境与背景/语法知识分析解释为何如此翻译

[约束]
  - 动词只给辞書形（原形），严格采用日本語教育文法术语
  - 仅列出 JLPT N3 及以上难度的重点词汇、短语与语法句型
  - 全部释义、语法剖析与语境说明必须使用{{target_language}}撰写
```

- `BuildExplainSystemPrompt` 的 sections 从 `[ExplainRolePrompt, ExplainStructureRule, …]` 收缩为 `[DefaultExplainBasePrompt, …]`；
- 原 `ExplainRolePrompt` 单行 const 删除；`ExplainStructureRule` 删除。
- `update-prompt-preview.ps1` 的数据键 `explainRole` / `explainStructure` 合并为 `explainBase`。

### 已拒绝 / 已决议事项

- **硬编码规约进视觉字面量**（拒绝）：与插件常量重复；用户自定义插件不惠及视觉路径；"提示词说一套、视觉做一套"的漂移风险。
- **视觉 Base 去翻译从句 + 统一拼对齐块**（原备选，随对齐块删除作废）。
- **补回视觉角色首句**"你是准确、自然的视觉翻译引擎"（`a82a822a` 遗留，无测试钉住）：**决议不补回**，主人认为无用。
- **视觉路径跑文本探测猜语言再决定拼哪些插件**（拒绝）：图为唯一输入，猜错成本高于无条件两条条件式规则。

## Token 与缓存影响

TokenEstimator 口径（target=中文（简体），联动变更后）：

| 场景 | 组装 | tokens |
|---|---|---|
| 文本·纯中文 | Base | ~155 |
| 文本·纯英 | Base + 英语插件 | ~197 |
| 文本·纯日 | Base + 日语插件 | ~235 |
| 文本·日英混排 | Base + 日语 + 英语 | ~277 |
| 视觉 | 视觉 Base + 日语 + 英语 | ~276（恒定单变体） |
| 精讲 | 精讲 Base + 一个插件 | ~280 / ~242 |

- 对齐块删除的净效果：纯英 198→197、纯日 243→235（不再有独立 section 头与 join 开销）。
- 视觉 ~276 与硬编码方案（~271）持平，但零重复、缓存亲和、用户自定义插件自动生效。

## 验收标准

- [x] 默认配置下视觉 system prompt = 视觉 Base + 两个内置插件，含 `漢字[かな]` 与 `国际音标（IPA）`；唯一哨兵；无 `{{target_language}}` 残留；无 CR、首尾无空白
- [x] `prompts.vision_base`（新键）自定义视觉 Base 生效；自定义 `japanese_plugin` / `english_plugin` 在视觉路径优先生效；旧键 `vision_oneshot` 被优雅忽略（不抛异常、回落默认）
- [x] `LanguageAlignmentRule` / `ExplainLanguageAlignmentRule` / `ExplainRolePrompt` / `ExplainStructureRule` 四个常量已删除；`DefaultTranslationBasePrompt` 末行含目标语言句；`DefaultExplainBasePrompt` 合并落地且 `[约束]` 含第三条 bullet
- [x] `BuildExplainSystemPrompt` sections 收缩为 `[DefaultExplainBasePrompt, 插件…]`；`GetSystemPrompt` doc 注释三路径描述改写
- [x] `TestTranslationPromptTemplates` 现有视觉断言保持通过；新增断言：视觉提示词含两规约、自定义插件在视觉路径优先生效
- [x] `TestModularPromptTemplates` 精讲断言（三小节、辞書形、JLPT N3、目标语言）改由合并字面量满足
- [x] `TestPromptLiteralFormatting` 字面量清单更新为 6 项（Base / VisionBase / ExplainBase / ExplainUser / 日语 / 英语），assembled 样本新增视觉样本，全绿
- [x] 设置页视觉编辑器改"视觉 Base"措辞（注明与语言插件共同组装）；`config.yaml` 模板 keys 为 `base / japanese_plugin / english_plugin / vision_base / explain` 且注释更新
- [x] `update-prompt-preview.ps1` required 清单与数据键同步（`DefaultVisionBasePrompt`、`DefaultExplainBasePrompt`、`explainBase`），生成逻辑已用桩文件验证通过（正式重跑待 011 落地 HTML）
- [x] `./scripts/dev.ps1 test` 全绿（271 PASS / 0 FAIL）

## 备注

- **未决议的对称性问题（实现时请主人确认）**：`prompts.explain` 键目前是 verbatim 整体覆盖（不拼插件），与本 issue 后 `vision_base` 的"Base+插件"语义不再对称。多轮会话下精讲 system prompt 实际锁定自阶段一，自定义 explain 只在独立精讲请求生效，影响面小；暂保持 verbatim，如需统一可作顺手项。
- 时序：010（英语文案）→ 本 issue → 011 的生成器重跑。

---

## 做了什么（2026-09-24）

### 一、`PromptTemplates.cs`：三 Base + 两插件的纯粹模块化架构

1. **新增 `BuildVisionSystemPrompt`**：`DefaultVisionBasePrompt` + 两个语言插件，`string.Join("\n\n")` 后 `ExpandTargetLanguage`，与另外两个 Build 方法同构；`GetSystemPrompt` 视觉分支改调它。两个插件**恒定同拼**（视觉路径没有原文可探测，而两条规则本身是条件式的）。
2. **删除四个常量**：`LanguageAlignmentRule`、`ExplainLanguageAlignmentRule`、`ExplainRolePrompt`、`ExplainStructureRule`。目标语言约束改为内联：`DefaultTranslationBasePrompt` 末行追加顶格的 `译文全部使用{{target_language}}撰写。`；精讲侧并入合并后的 `DefaultExplainBasePrompt` 的 `[约束]` 第三条 bullet。
3. **`ExplainRolePrompt` + `ExplainStructureRule` → 单一 `DefaultExplainBasePrompt`**：`BuildExplainSystemPrompt` 的 sections 收缩为 `[DefaultExplainBasePrompt, 插件…]`。
4. **抽出私有 `AppendLanguagePlugins(sections, detection, prompts)`**：文本与精讲两条路径共用同一段门控实现，"同构"由构造保证而非靠复制粘贴维持；视觉路径因其无条件语义不走该 helper。
5. **顶层 doc 注释按三路径改写**：三个 Base、两个插件、探测机制、门控职责分离、视觉恒定同拼的理由与缓存收益、多轮会话锁定值。

### 二、配置键迁移 `vision_oneshot` → `vision_base`

- `PromptConfig.VisionOneshot` → `VisionBase`（`AppConfig.cs` 属性 + `AppConfigCloner`）；`ConfigStore.Normalize` 的 `??= string.Empty` 同步改名。
- `config.yaml` 模板键改名并加注释（"视觉 Base：与日语、英语两个插件共同组装成视觉一条龙的 system prompt"）。`pipeline.mode` 的 `vision_oneshot` **模式值未动**（输入模式名，与提示词键是两回事）。
- 设置页 `TranslationPage`：`VisionPromptBox` 装载/应用/恢复默认三处改引用新属性与新常量；XAML 行头改"视觉一条龙 Base 提示词"，描述注明"与日语、英语两个语言插件共同组装……注音格式由插件提供。留空则使用内置默认 Base。"

### 三、自测

- `TestVisionPromptComposition`（新增，4 项断言）：默认视觉提示词含两规约 + 唯一哨兵 + 目标语言已展开；**视觉 system prompt 与原文无关**（空文本 / 英文 / 日文三种请求逐字相同，证明恒定单变体）；自定义 `VisionBase` 与两个自定义插件在视觉路径优先；无残留独立对齐段。
- `TestTranslationPromptTemplates`：自定义视觉提示词断言从"verbatim 相等"改为"以自定义 Base 开头且两插件同拼"（语义已变）。
- `TestPromptLiteralFormatting`：字面量清单 8 → 6 项；`assembled` 样本新增日英混排样本（5 个）。
- `TestModularPromptTemplates` 精讲断言追加"全部释义、语法剖析与语境说明必须使用中文（简体）撰写"，钉住联动变更 A。
- `SelfTest.Explanation.TestExplanationPromptN3Filter` 改引 `DefaultExplainBasePrompt`。
- `SelfTest.Config`：两个 YAML 夹具的键改名；**新增 `TestLegacyVisionOneshotKeyIsIgnored`**（旧键被静默忽略、版本号不动、重写时不复活）。
- `SelfTest.Ui`：三处引用改名。

### 四、`scripts/update-prompt-preview.ps1`

- required 清单 9 → 6 项（`DefaultTranslationBasePrompt` / `JapanesePlugin` / `EnglishPlugin` / `DefaultVisionBasePrompt` / `DefaultExplainBasePrompt` / `DefaultExplainUserPrompt`）；删除 `ExplainRolePrompt` 单行 const 的特判提取（该常量已不存在，留着会直接 throw）。
- 数据键重排为 `translationBase / visionBase / explainBase / explainUser / japanese / english`；doc 注释"九个提示词变量"改"六个"。

### 验证

- `./scripts/dev.ps1 test`：**271 PASS / 0 FAIL**（010 的 268 项之上净增 3 项，无回归）。
- 生成器：仓库中 `scripts/prompt-preview.html` 尚不存在（属 011 交付物），脚本按契约 throw；已把脚本复制到临时目录配桩 HTML 实跑一遍，输出 6 个字面量与 `visionBase / explainBase` 新键，`detector` 区间抽取不受影响。011 落地 HTML 后正式重跑即可。
- 残留引用扫描：`LanguageAlignmentRule` / `ExplainRolePrompt` / `ExplainStructureRule` / `ExplainLanguageAlignmentRule` / `DefaultVisionOneshotPrompt` / `VisionOneshot` 在 `src/`、`scripts/`、`config.yaml` 中零残留（仅 `SelfTest.Config` 的迁移回归测试按命名需要提及旧键字符串）。
