---
kind: issue
type: feature
title: "P2 中文多音提示色与 hover 候选菜单"
status: open
created: 2026-11-15
---

# P2 中文多音提示色与 hover 候选菜单

> **读者：** 负责中文注音展示层与结果区交互的人——多音字在界面上怎么被看见、怎么查看备选，以及它和现有复制/选区/收藏如何共存。
> **归属：** 本文件路径即归属 Epic 003。
> **来源：** Epic 003 活规格 P2；`codestable/talks/004-本地三语注音优化与后续能力规划.md` P2「橙色表示存在其他读音」段；P1 Issue 005 已把候选携带到 `AnnotationSyllable.Candidates`。

## 做成以后是什么样

在 P1 已建立的结构化候选数据之上，让中文多音字在结果区**可被看见并查看备选**，且不破坏现有交互：

- **多音提示色：** 该字在词典里有默认以外的读音时，其默认注音用主题警示色（橙/琥珀，`Brush.Warning`）显示，表示「存在其他读音」，**不表示本句读音不确定**——即使「银行」的「行」已正确选 `háng` 也照常标记。单音字保持中性灰。
- **hover 候选查看：** 鼠标悬停注音弹出候选面板，逐条列出该字词典收录的全部读音；本句默认读音用 **文字标记 `✓ 本句默认读音`** 标出，不以颜色作为唯一信息。面板半透明淡绿、紧凑现代；出现时不推动正文布局、不被滚动区域裁切，并与右键复制、拖动选区、左键复制读音、划词收藏气泡协调。移出后恢复多音提示色，而不是统一抹成灰。
- **只做查看：** 不改写原文、不手动纠音、不持久化个人读音偏好。「完整读音」= 所用离线词典收录的读音，不凭空补数据。

**范围：** `RubyInlineControl` 的逐字列多音提示色、悬停恢复、候选查看面板，消费 `AnnotationSyllable.Candidates`。
**不包含：** 手动纠音 / 改全文 / 个人读音偏好持久化；英语橙色标记与右键异读列表；日语异读交互；新增或更换选音引擎。

## 现状与依据

P1 Issue 005 的自研词组匹配引擎已为每个 `AnnotationSyllable` 产出「默认音 + 候选集合」（`Candidates[0]` 即本句默认），并在 Debug/AOT 通过。P2 只消费这份结构化候选，不再制造候选、不从 `底字[读音]` 反解析、不改变哪些词需要显示注音（沿用过滤/去重/短文本口径）。

## 实现

- `RubyInlineControl` 逐字列构建时，按 `syllable.Candidates is { Count: > 1 }` 判定多音：是则读注音用 `GetHintBrush`（回退橙），否则灰；把底字/候选/默认项收进 `CandidateEntries` 供面板消费。
- 悬停高亮后由 `RestoreReadingForeground` 恢复每列**各自底色**（多音回提示色，其余回灰），杜绝原先统一抹灰。
- 多音字把默认 ToolTip 换成 `CreateCandidateToolTip`：`Border`（半透明淡绿 `Brush.Accent.Subtle`、圆角、紧凑内距）+ 每行「底字 读音（默认项 SemiBold）」+ `✓ 本句默认读音` + 底部「左键复制读音 · 右键复制/更多」提示。复用主题 `Brush.Warning / Brush.Accent.Subtle / Brush.Text.Primary`，`ResolveBrush` 安全回退，杜绝 UnsetValue 强转崩溃。
- 复制、选区、收藏、编辑再注音仍走原结构化路径（`AnnotationSpan`/`Syllables`），候选只影响显示，不进入导出/复制事实源。

## 验证

- `./scripts/dev.ps1 test`（等价 `dotnet run --project src/NaniKore -- --self-test`）：**1807 checks 通过**。新增覆盖：单音列中性色、多音列警示色、候选条目只来自多音字且默认唯一、悬停高亮/移出恢复提示色、候选面板列出全部读音并含 `✓ 本句默认读音`、单音词无候选面板；`SelfTest.Phonetics` 增加真实词典端到端断言「银行」的「行」携带默认 `háng` + 备选 `xíng`；候选面板 PNG 冒烟。
- NativeAOT：`./scripts/dev.ps1 build -Release` 生成成功，直接运行 `artifacts/win-x64/NaniKore.exe --self-test` → **1807 checks 通过**，Debug/AOT 一致。
- 视觉证据：`artifacts/p2-chinese-multi-pronunciation.png`（候选面板渲染，不入版本库）。

## 未覆盖

- 当前模型不能读图，**人工视觉与真实鼠标 hover 冒烟未完成**；hover 用非交互 ToolTip 呈现（复制动作仍在右键菜单），未做可点击的常驻弹出菜单。
- 英语异读/日语异读交互、手动纠音、持久化偏好均为后续，不在本切片。

## 关闭时

- **回写 epic spec 候选：** 多音提示色口径（橙色=存在其他读音、非不确定）、候选查看面板结构（默认项文字标注、半透明淡绿、悬停恢复）、以及「只查看不纠音」的边界。
- **关闭判断：** 人工视觉/鼠标交互补验 + 质量目标逐项有证据后，经主人授权再关闭。完成不等于关闭。
