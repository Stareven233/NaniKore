---
kind: issue
title: "提示词系统重构与清理：SectionChannelRouter 解耦与精讲契约收敛"
type: ff
status: closed
priority: normal
created: 2026-09-23
---

# 提示词系统重构与清理：SectionChannelRouter 解耦与精讲契约收敛

> **读者：** 负责翻译链路与大模型工程的开发者。
> **背景：** 提示词体系的核心场景极为清晰——**文本输入翻译**、**图片输入翻译**与**两阶段共用的精讲追问**。其核心理念为：固定必需的 Base 提示词，语种独有的规范按需注入，两阶段解耦以保障极致的首字延迟（TTFT）与大模型前缀缓存（Prompt Cache）命中率。
> 然而对 `PromptTemplates.cs` 的全面代码走查发现，当前实现存在显著的物理膨胀、职责混杂与历史残留逻辑，亟需做一次彻底的重构清理。

---

## 现状剖析与问题清单

### 问题一：`SectionChannelRouter` 严重违背单一职责（SRP）导致文件物理膨胀
- **现象：** `PromptTemplates.cs` 总共 968 行，但提示词定义与组装逻辑仅占前 270 行。剩余近 700 行（298~968 行）全部是 `SectionChannelRouter` 及其流式标签剥离、降噪、滑动窗口截断状态机。
- **本质：** `PromptTemplates` 属于**上游生产者**（负责构建下发给大模型的 System/User Prompt），而 `SectionChannelRouter` 属于**下游消费者**（负责接收和消费模型原始流式响应并分发到 `SourceText` / `Translation` 通道）。两者职责截然不同，硬塞在同一个文件严重影响代码可读性与维护性。
- **方案：** 将 `SectionChannelRouter` 独立抽取到 `src/NaniKore/Translation/SectionChannelRouter.cs`，彻底还 `PromptTemplates.cs` 一个纯粹清晰的定位（预期代码量降至 200 行左右）。

---

### 问题二：历史过渡方法沦为 100% 死代码
- **现象：** `PromptTemplates.cs` 中存在三个历史遗留方法：
  - `BuildModularSystemPrompt(UnderstandRequest request, PromptConfig? prompts)`
  - `BuildVisionSystemPrompt(UnderstandRequest request, PromptConfig? prompts)`
  - `BuildExplainSystemPrompt(UnderstandRequest request, PromptConfig? prompts)`
- **现状：** 全工程所有 Provider（Gemini / OpenAI / Claude）、SelfTest 测试套件以及 CLI `--dump-prompt`，已全部统一收敛到：
  ```csharp
  PromptTemplates.GetSystemPrompt(request, prompts);
  ```
  这三个旧方法在全工程没有任何引用，纯属重构过渡期的残留死代码。
- **方案：** 直接删除这三个无引用的包装方法。

---

### 问题三：精讲路径中的 `AppendLanguagePlugins` 画蛇添足与认知割裂
- **主人的正确理念：** 语种插件（日语振假名 `漢字[かな]`、英语音标 `apple[ˈæpl]`）只服务于翻译阶段的原文注音标注；精讲阶段的核心职责是词汇释义、语法功能与翻译原理解析，精讲提示词按设计理念应该只有 `DefaultExplainUserPrompt`，不应有额外的语种插件注入。
- **现状代码中的缺陷实现：**
  ```csharp
  private static List<PromptSection> DescribeExplainSections(UnderstandRequest request, PromptConfig? prompts)
  {
      var sections = new List<PromptSection> { Choose("explain", prompts?.Explain, DefaultExplainUserPrompt) };

      if (string.IsNullOrWhiteSpace(prompts?.Explain))
      {
          // 缺陷点：当用户未自定义精讲提示词（留空）时，居然悄悄去追加了日语/英语注音插件！
          AppendLanguagePlugins(sections, LanguageDetector.Detect(request.SourceText, request.TargetLanguage), prompts);
      }

      return sections;
  }
  ```
- **矛盾与危害：**
  1. **规则不对称与分裂：** 若用户填写了自定义精讲词，则不注入插件；若留空使用内置精讲词，居然会给 System Prompt 额外拼上振假名和音标规范。
  2. **与真实两阶段链路脱节：** 真实的翻译应用走的是多轮会话（`TranslationSession`），第二阶段精讲直接沿用锁定的阶段一 System Prompt，并将精讲模板作为 User 追问消息下发，**根本不执行**上述代码。上述代码仅在不带 Session 的单轮 CLI / 测试兜底中被调用，导致行为模式不一致。
- **方案：**
  - 精讲场景下彻底移除 `AppendLanguagePlugins`，使其保持纯粹——精讲就只下发精讲提示词（`explain` 段）。
  - 使单轮精讲契约与两阶段设计理念完全对齐，彻底消除该认知混淆。

---

### 问题四：提示词文案的细微优化建议（仅供参考，不擅自修改）

1. **`DefaultTranslationBasePrompt` 移除 `/分析` 词眼：**
   - 现行文案：`译文/分析全部使用{{target_language}}撰写`
   - 原因：既然翻译阶段已经严格剥离精讲、坚决不输出分析，保留“/分析”一词可能对发散能力较强的模型（或开源小模型）产生诱导，增加其偷跑输出分析或精讲标签的概率（这也是 `SectionChannelRouter` 需要维护数十行截断前缀的原因之一）。
   - 建议：精简为 `译文全部使用{{target_language}}撰写`。

2. **`DefaultExplainUserPrompt` 中的 JLPT 术语通用化考量：**
   - 现行文案：`注：全部使用教育语法术语，仅列出 JLPT N3 及以上难度的重点词汇、短语与语法句型`
   - 原因：在日译中场景下极其合身，但在纯英语或多语种翻译后点击精讲时，让模型按“JLPT N3”评估英语词汇难度稍显生硬。
   - 建议：未来可微调为更通用的表述，如“仅列出中高级及以上难度的重点词汇（如 JLPT N3+ / 进阶词汇）”。

---

## 改造与落地步骤规划

1. **文件拆分解耦：**
   - 新建 `src/NaniKore/Translation/SectionChannelRouter.cs`，将 `SectionChannelRouter` 及其标签清单、滑动窗口流式截断算法完整迁移入新文件。
   - 保留原命名空间 `NaniKore.Translation`，保证调用方零感知。
2. **清理死代码与多余逻辑：**
   - 从 `PromptTemplates.cs` 移除 `BuildModularSystemPrompt`、`BuildVisionSystemPrompt`、`BuildExplainSystemPrompt`。
   - 修正 `DescribeExplainSections`，移除无意义的 `AppendLanguagePlugins` 注入，收敛为单一的 `explain` 段。
3. **验证与测试防护：**
   - 运行 `./scripts/dev.ps1 test`，确保 `SelfTest` 中的 Prompt 契约测试、段描述反漂移测试与流式路由测试 100% PASS。
   - 使用 `scripts/serve-preview.ps1` 校验三种模式的真值渲染是否符合预期。

---

## 验收标准

- [x] `PromptTemplates.cs` 代码行数缩减至 250 行以内，只保留提示词定义、段组合真值与路径分派（重构后为 247 行）。
- [x] `SectionChannelRouter.cs` 作为独立流式通道解析器存在于 `Translation/` 目录下。
- [x] 彻底清理 3 个无引用的 `Build*` 历史死代码方法。
- [x] 精讲单轮组装与多轮设计理念一致，不再附带翻译注音插件。
- [x] `./scripts/dev.ps1 test` 无 UI 自测全部通过。

## 执行记录

- **流式通道路由器解耦**：创建 `src/NaniKore/Translation/SectionChannelRouter.cs`，将 `SectionChannelRouter` 及其噪声标签定义、滑动窗口截断状态机从 `PromptTemplates.cs` 完整迁出。保持命名空间为 `NaniKore.Translation`，上游调用方与自测零感知。
- **死代码与过渡方法清理**：删除 `PromptTemplates.cs` 中全工程无引用的 `BuildModularSystemPrompt`、`BuildVisionSystemPrompt`、`BuildExplainSystemPrompt`。
- **精讲契约收敛**：修正 `DescribeExplainSections`，移除 `AppendLanguagePlugins` 调用，单轮精讲统一只下发 `explain` 段，与两阶段阶段二追问行为完全对齐；同步校准 `SelfTest.Translation.cs` 中的精讲测试断言与 `scripts/prompt-preview.html` 离线快照。
- **精简与自测防护**：`PromptTemplates.cs` 从 968 行瘦身至 247 行；运行 `./scripts/dev.ps1 test` 全部无 UI 自测 100% 通过。

