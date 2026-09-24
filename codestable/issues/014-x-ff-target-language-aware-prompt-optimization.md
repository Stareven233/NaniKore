---
kind: issue
title: "目标语言感知提示词门控与质量优化：插件互斥剪枝与规则示例自洽"
type: ff
status: closed
priority: normal
created: 2026-09-24
---

# 目标语言感知提示词门控与质量优化：插件互斥剪枝与规则示例自洽

> **读者：** 负责翻译链路与大模型工程的开发者。
> **背景：** 源自 `codestable/talks/003-目标语言感知提示词门控与质量优化.md`。该 talk 收敛了提示词体系四个维度的决策，本 Issue 落地其中三项——**插件门控的目标语言语义剪枝**、**规则与示例自洽**、**Base 混排纪律**；第四项「精讲 Prompt 深度重构」经评估列为后续专项，本次不纳入（见文末「暂不纳入」）。
> 核心理念不变：固定必需的 Base、语种独有规范按需注入、两阶段解耦以保障 TTFT 与前缀缓存命中；本 Increment 在其上补一层「目标语言感知」，并理顺注音密度与降噪。

---

## 现状剖析与问题清单

### 问题一：插件门控是纯「源文本视角」，未感知目标语言（反客为主注音）
- **现象：** 目标语言设为英语、原文为 `Nintendo Switchの最新ゲームをプレイする` 时，`LanguageDetector` 扫到拉丁字母即置 `HasEnglish`，`AppendLanguagePlugins` 据此注入 `EnglishPlugin`，诱导模型给本就属于目标语言的英文商标注 IPA——无价值且是噪点。
- **对称盲区：** 目标语言为日语时，源文本中引述的假名/汉字也会触发 `JapanesePlugin`，甚至诱导按日文汉字标注振假名。
- **已确认原则：** 语言特化插件（`JapanesePlugin` / `EnglishPlugin`）的本质定位是「为源语言的学习者提供生词阅读辅助（振假名 / 音标）」；一旦该语言本身就是目标语言，辅助即失效，绝不注入。

### 问题二：两个语言插件「规则高门槛、示例超低幼」自相矛盾
- `JapanesePlugin`：规则限定「JLPT N3 及以上」，示例却标注入门 N5 的 `私`/`学生`——模型倾向模仿示例，导致满屏假名噪音。
- `EnglishPlugin`：规则限定「少见、困难单词」，示例却是 `apple`——且缺品牌名/缩写/代码防护，易对 `API`、`GitHub`、`dog` 乱打音标。

### 问题三：文本 Base 缺混排保留与前导标签负向抑制
- `DefaultTranslationBasePrompt` 含绝对化措辞「所有的输入都是需要翻译的文本」，与「混排文本中已有目标语言部分」的处置冲突。
- 缺少「无注音规范时第一部分直接输出原文」的兜底，也缺少对 `【原文】`/`【译文】`前缀与 Markdown 代码围栏的负向抑制——后者是下游 `SectionChannelRouter` 被迫维护大量噪声标签剥离状态机的根因之一，从源头抑制可降 TTFT。

---

## 改造与落地步骤

1. **目标语言谓词（探测层）：** `LanguageDetector` 新增 `IsEnglishTarget` / `IsJapaneseTarget`，与既有 `IsJapaneseToChinesePreference` 同款宽松口径，覆盖展示名 / 英文·日文名 / 语言代码三形态；空值回落 `false`（不剪枝）。
2. **组装层互斥：** `AppendLanguagePlugins` 增加 `targetLanguage` 形参，目标即日语/英语时整条剔除对应插件；`DescribeTextSections` 透传 `request.TargetLanguage`。探测结果决定「纳入哪些规约」，目标语言决定「剔除哪些规约」，职责分离。
3. **视觉路径共用互斥：** 新增 `VisionBothLanguages` 探测替身（令日英插件双双入围成候选），`DescribeVisionSections(targetLanguage, prompts)` 复用同一 `AppendLanguagePlugins`；中文目标下与旧版「恒定同拼」等价，日/英目标剔除同语种，且目标语言是全局静态项、视觉 prompt 仍为唯一单变体，前缀缓存命中不受损。
4. **文案精调：**
   - `JapanesePlugin`：示例换成真 N3+ 的 `挨拶[あいさつ]`、`躊躇[ちゅうちょ]`，补「日常基础汉字（如 私、学生、今日）严禁标注」。
   - `EnglishPlugin`：示例换成 `ubiquitous[juːˈbɪkwɪtəs]`，补「常见基础词、专有名词与技术缩写（如 API、GitHub、dog）不标注」。
   - `DefaultTranslationBasePrompt`：去绝对化措辞，补「混入目标语言忠实保留/自然融入」「整段已是目标语言忠实呈现」「无注音规范时第一部分直出原文」「严禁【原文】/【译文】前缀或 Markdown 代码围栏」。
5. **自测防护：** `SelfTest.Translation.cs` 新增 `TestTargetLanguagePluginMutex`（日译英 / 英译日 / 引述假名 / 中文维持 / 视觉互斥 / 视觉单变体等 7 组）与 `TestPluginRuleExampleConsistency`（2 组），接入 `RunTranslationTests`。

---

## 验收标准

- [x] `IsEnglishTarget` / `IsJapaneseTarget` 覆盖中英日三形态与语言代码，中文目标不误剪、空值不剪。
- [x] 文本路径：目标英语剔除英语插件、保留日语插件（日译英）；目标日语剔除日语插件（含引述假名场景）；中文目标维持现状、日英混排照旧同享两套规约。
- [x] 视觉路径同样施加互斥；目标固定为英语时视觉 prompt 仍为唯一变体（缓存友好）。
- [x] `JapanesePlugin` / `EnglishPlugin` 规则与示例自洽，且保留既有契约串 `漢字[かな]`、`国际音标（IPA）`、`拉丁字母拼写的少见、困难单词`。
- [x] `DefaultTranslationBasePrompt` 保留开场句 `你当前工作在一个翻译应用中`、唯一哨兵与 `{{target_language}}` 占位符；排版契约（两空格缩进、无 CR、全中文、单一哨位）不破。
- [x] `DescribeSections` 段描述反漂移闸门保持（预览 / CLI 与下发逐字一致）。
- [x] `./scripts/dev.ps1 test` 无新增失败：新增 13 项断言全 PASS，基线既有失败数与集合不变。

## 执行记录

- **探测层：** `LanguageDetector` 增补 `IsEnglishTarget` / `IsJapaneseTarget`；注释点明闭集内仅英语名含 `en`、日语名含 `ja` 子串，与既有 `IsJapaneseToChinesePreference` 宽松口径一致。
- **组装层：** `PromptTemplates.AppendLanguagePlugins` 引入 `targetLanguage` 互斥剪枝；文本 / 视觉两路共用同一方法。视觉经 `VisionBothLanguages` 替身入围两插件后再剪枝，中文目标与旧版「恒定同拼」等价。
- **文案：** `JapanesePlugin` → `挨拶[あいさつ]` / `躊躇[ちゅうちょ]` 正例 + 入门字严禁；`EnglishPlugin` → `ubiquitous[juːˈbɪkwɪtəs]` 正例 + `API`/`GitHub`/`dog` 排除；`DefaultTranslationBasePrompt` 去绝对化并补混排保留、无注音兜底、前缀负向抑制三条指引。
- **文档同步：** 更新 `PromptTemplates` 类级 XML 注释与 `PromptPath.Vision` 枚举说明，反映互斥与「中文目标两者俱全、日英目标单变体」。
- **自测：** 基线为 3 个既有失败；改动后 `./scripts/dev.ps1 test` 仍为**同样 3 个既有失败、零新增**，新增 13 项断言（互斥 11 + 规则/示例 2）全 PASS。
- **缓存影响：** 文本路径在目标英语/日语下，注入变体由 ≤3 收敛为 ≤2（Base、Base+另一语言插件）；视觉路径目标固定时保持唯一单变体——前缀缓存命中率不降反得益。

## 暂不纳入 / 已知遗留（非本次范围）

- **精讲 Prompt 深度重构（talk 003 第 5 节）：** 既有两条 explain 自测（`SelfTest.Translation`、`SelfTest.Explanation`）因提示词 `教育语法` 与断言 `教育语法术语` 的漂移而红，与本次目标语言优化无关，留待精讲排版专项统一处理。
- **视觉 Base 措辞漂移：** `TestVisionPromptComposition` 仍断言旧串 `所有输入图片都是需翻译文本的截图`，与 issue 009/013 改写过的新视觉 Base 措辞不符，属另一处既有红，留待视觉专项校准。
- **`scripts/prompt-preview.html` 的 `EMBEDDED` 离线镜像整体滞后：** 其 `prompts.*` 与 `samples[].dump` 仍含 `译文/分析`、旧视觉措辞、旧插件文案与 `教育语法术语`。运行时预览以 `serve-preview.ps1` 代理的 `--dump-prompt` 真值为准、不受影响；建议后续单独做一次离线快照再生成，使其与现行 `PromptTemplates` 对齐。
