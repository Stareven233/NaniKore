---
kind: issue
title: "提示词预览 HTML 与 --dump-prompt CLI 真值源"
type: ff
status: open
priority: normal
created: 2026-09-24
---

# 提示词预览 HTML 与 --dump-prompt CLI 真值源

> **读者：** 负责开发期工具链的开发者。
> **背景：** `scripts/update-prompt-preview.ps1` 已写入暂存区（未提交），设计意图是"所见即所发"——从 C# 源码抽取提示词字面量与探测器区间，内嵌进 `scripts/prompt-preview.html`。但两个缺口：(1) HTML 本体在仓库中不存在（git 全历史均无），脚本当前直接 `throw "缺少文件"`；(2) 生成器只抽"零件"（九个字面量 + 字符区间），没有复刻 `BuildModularSystemPrompt` 的组装逻辑，看不到"这次请求最终下发的整句"。
> **关联：** `009-x-ff-vision-prompt-plugin-composition.md`（视觉组装，已于 2026-09-24 落地）、`010-x-ff-mixed-language-independent-plugin-gating.md`（混排门控）落地后，"最终给 llm 的提示词究竟是什么样子"成为高频开发问题；两份 issue 的防漂移闸门都依赖本文的"重跑生成器"。
> **前置进度（2026-09-24 随 009 完成）：** `update-prompt-preview.ps1` 的 required 清单与数据键已按 009 改名/合并同步（6 个字面量 / `visionBase` / `explainBase`），并已用临时桩 HTML 实跑验证；本文待办仅剩 HTML 本体、`--dump-prompt` CLI、样本矩阵内嵌与文档同步。

## 决议（2026-09-24 讨论结论）

- **HTML 可视化 = 开发期主视图**：回答"提示词现在长什么样、改完什么样"。
- **CLI `--dump-prompt` = 真值源**：HTML 的内嵌组装数据由它产出，消灭 PowerShell 复刻漂移；同时是命令行人工排查工具。
- **不做应用内调试日志开关**：运行时取证是低频需求，被上述两层覆盖；若未来真出现"输出不符预期但不知道发了什么"的 case，`--dump-prompt` 的 JSON 字段即日志格式的现成基础，届时再议。

## 方案

### 一、`--dump-prompt` CLI（`Program.cs`，照 `--dump-config` / `--translate-text` 先例）

```
NaniKore.exe --dump-prompt [--text "…"] [--image <path>] [--kind translate|explain] [--target 中文（简体）]
```

- 经 `ConfigStore` 读取**真实配置**（自定义 `prompts.base/japanese_plugin/vision_base/…` 随之体现），与 `--dump-config` 同源；target 缺省取 `general.target_language`。
- 路径判定：`--image` 优先（有图走视觉路径，即使 text 为空）；`--kind explain` 且无 session 时走精讲组装。
- **JSON 输出**（供生成器消费）与人读渲染并存：

```json
{
  "kind": "translate",
  "path": "text | vision | explain",
  "target_language": "中文（简体）",
  "source_preview": "原文（图片路径场景为空）",
  "detection": {
    "features": ["Japanese", "English"],
    "needs_japanese_ruby": true,
    "needs_english_ipa": false,
    "is_pure_hanzi_japanese_candidate": false
  },
  "sections": [{ "name": "base", "source": "builtin | custom", "text": "…" }],
  "system_prompt": "最终下发的完整 system prompt（占位符已展开）",
  "estimated_tokens": 279
}
```

- 用法错误 exit 2、成功 exit 0，与现有 CLI 分支一致。**不打印任何 API Key。**

### 二、`prompt-preview.html`（单文件、零外部依赖、file:// 直开）

- **交互区**：输入原句 + kind + 目标语言 + 图片开关 → 实时展示：探测位图（哪些特征命中）、命中的段（标注 `builtin`/`custom` 来源）、**最终 system prompt 全文**（按段高亮拼装顺序）、一键复制。
- **样本矩阵**（生成器预生成内嵌）：覆盖 `こんにちは` / `Appleの新しいiPhone` / `進入禁止` / `今天天气很好…` / `Hello world` / 图片请求 / explain，横跨三条路径——把 009/010 的改动前后对比做成默认视图。
- **零件面板**：九个提示词字面量 + 探测器字符区间（复刻现有抽取逻辑），当前输入命中的字符高亮。
- 页面常驻标注："改动提示词或探测器后必须重跑 `scripts/update-prompt-preview.ps1`，否则预览将与实际下发漂移"。

### 三、`update-prompt-preview.ps1` 扩展

- 保留现有字面量 / 区间抽取逻辑（复刻 C# 原始字符串剥离 + `PromptLiteral` 行尾归一化）。
- required 清单随 009 改名/合并同步：`DefaultVisionOneshotPrompt` → `DefaultVisionBasePrompt`，`ExplainRolePrompt` + `ExplainStructureRule` → 单一的 `DefaultExplainBasePrompt`，`LanguageAlignmentRule` / `ExplainLanguageAlignmentRule` 删除；内嵌数据键 `explainRole` / `explainStructure` 合并为 `explainBase`。
- 新增：对样本矩阵逐个调用**已构建的 `NaniKore.exe --dump-prompt`**，将返回 JSON 内嵌进 `/*__EMBEDDED_DATA__*/` markers 之间；exe 不存在时 WARNING 跳过该步（静态零件照常生成，防呆不中断）。
- HTML 或 markers 缺失时脚本照旧 `throw`（防呆契约保持）。

## 验收标准

- [ ] `--dump-prompt` 三条路径输出正确：path 判定、detection 位、sections 含来源标注、`system_prompt` 与 `GetSystemPrompt` 逐字一致
- [ ] 自定义 `prompts.base` / `japanese_plugin` / `vision_base` 后，`--dump-prompt` 的 sections 正确反映覆盖优先级（custom 优先于 builtin）
- [ ] 重跑 `scripts/update-prompt-preview.ps1` 后，HTML 内嵌的组装数据与 CLI 输出逐字一致（漂移零容忍）
- [ ] HTML 本地直开可用，样本矩阵与交互区均渲染正常
- [ ] 文档（`codestable/spec/index.md` 提示词段落）同步"改动提示词后重跑生成器"的工作流

## 备注

- **已拒绝：JS 重写探测/组装逻辑**（前端模拟 C# 行为必漂移）；一切组装真值来自 C#/CLI，PowerShell 端只做静态字面量搬运。
- **已拒绝：应用内运行时日志开关**（见上文决议）；另注意 `.gitignore` 已有 `*.log`，若未来做日志落盘需规划 `logs/` 目录与脱敏。
- 与 009 的时序：本 issue 可先行（HTML 先展示现状），009 落地后重跑生成器即可展示新组装；`--dump-prompt` 的作用之一就是让这条时序不产生漂移。
