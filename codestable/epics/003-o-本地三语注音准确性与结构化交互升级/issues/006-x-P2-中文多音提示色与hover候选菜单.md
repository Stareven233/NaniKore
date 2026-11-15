---
kind: issue
type: feature
title: "P2 中文多音提示色与 hover 候选菜单"
status: closed
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

- **布局踩坑（真实视觉回归）：** 逐字列的 `reading`/`bottom` TextBlock 未显式设 `LineHeight`，会继承外层 `SelectableTextBlock` 的固定 `LineHeight=36`，致使每个注音控件被撑到 ~71px、溢出原文区并把后续文字挤走。修复：两列显式 `LineHeight=double.NaN`，控件高度回到 ~34px 内，注音回到汉字上方且留在原文区内。
- 逐字列按 `syllable.Candidates is { Count: > 1 }` 判定多音：是则默认注音用 `GetHintBrush`（`Brush.Warning`，回退橙），否则灰；底字/候选/默认项收进 `CandidateEntries` 供 hover 面板消费。
- 悬停高亮后由 `RestoreReadingForeground` 恢复每列**各自底色**（多音回提示色，其余回灰），杜绝原先统一抹灰。
- **统一悬浮面板：** 不再用 `ToolTip`（自带不透明外壳），改用 `Popup` 承载单个 `Border`（半透明卡片色、圆角、无边框、无淡绿/橙框），锚定注音行、层级在滚动区之上不被裁切。多音/单音共用同一容器与样式。
- **面板内容模板（普通字与多音字一致）：** 逐字分组，每行「底字 读音」；**同一个字的不同读音单换行紧邻，不同字之间留一空行**，字后空一行再接「点击复制注音 / 右键查看更多选项」；多音字的当前默认读音用与原文注音相同的橙色高亮（并列 SemiBold），单音字保持常规色。日/英整词无逐字映射时用一行「底字 读音」代替。
- `Popup` 打开失败有防御兜底，绝不打崩 UI 线程；`DetachedFromVisualTree` 时关闭面板。
- **修复原文区框选/右键回归：** 中文逐字列的底字 TextBlock 原为 `IsHitTestVisible=false`，导致渲染出的汉字不能拖拽框选、右键也不出菜单。改为底字可命中：左键拖拽冒泡到 `SelectableTextBlock` 正常框选，右键冒泡到词容器上下文菜单（与其注音右键一致）；同时挂在底字上的 hover 也可高亮并弹出候选面板。
- 复制、选区、收藏、编辑再注音仍走原结构化路径（`AnnotationSpan`/`Syllables`），候选只影响显示，不进入导出/复制事实源。

## 验证

- `./scripts/dev.ps1 test`（等价 `dotnet run --project src/NaniKore -- --self-test`）：**1809 checks 通过**。新增覆盖：单音列中性色、多音列警示色、候选条目只来自多音字且默认唯一、悬停高亮/移出恢复提示色、候选面板按模板列出「底字 读音」（多音列全部读音、普通字列唯一读音）、默认读音橙色而普通读音非橙色、单/多音面板样式一致、**固定 `LineHeight=36` 下注音控件不被撑高且留在原文区内的布局回归**；`SelfTest.Phonetics` 增加真实词典端到端断言「银行」的「行」携带默认 `háng` + 备选 `xíng`。
- 真机冒烟：主人反馈布局与样式问题后，本轮已修复（逐字列行高、Popup 样式、底字命中）；实际框选/右键需主人再次目视确认（AI 不能操作真实鼠标）。
- NativeAOT：`./scripts/dev.ps1 build -Release` 生成成功，直接运行 `artifacts/win-x64/NaniKore.exe --self-test` → **1809 checks 通过**，Debug/AOT 一致。\n- 真实可视树冒烟：把注音控件挂进真实 `Window` 后 `Popup` 能真正打开（`toplevel=True opened=True`），确认 hover 面板不会被静默吞掉（该临时诊断已移除）。

## 未覆盖

- 当前模型不能读图，**人工视觉与真实鼠标 hover 冒烟仍未由 AI 复核**；主人已做真实视觉测试并报告了本次布局与样式问题，本轮据此修复但需主人再次目视确认。
- hover 用 `Popup` 呈现，不作为可点击常驻菜单；面板内不含可交互编辑。
- 英语异读/日语异读交互、手动纠音、持久化偏好均为后续，不在本切片。

## 关闭时

- **回写 epic spec 候选：** 多音提示色口径（橙色=存在其他读音、非不确定）、hover 面板结构（Popup 半透明、默认读音响橙、悬停恢复、不裁切）、逐字列与面板文本须显式行高避免继承外层 LineHeight 的布局约束、以及「只查看不纠音」的边界。
- **关闭判断：** 主人真实视觉/鼠标复核通过 + 质量目标逐项有证据后，经主人授权再关闭。完成不等于关闭。

## 2026-11-15 真机二次反馈修复

1. **面板每行多出空行：** 根因是 Popup 内容也继承了外层 `SelectableTextBlock` 的固定 `LineHeight=36`，每行被撑成 36px。改为面板所有文本显式 `LineHeight=double.NaN`，同字单行紧邻、异字空一行、字与页脚空一行。补断言：面板全部 TextBlock 行高为 NaN。
2. **「点击复制注音」偏大偏粗：** 统一为与读音行同字号、`FontWeight.Normal`（行高 NaN 后视觉也归一）。补断言：页脚与默认读音同 FontSize 且 Normal。
3. **（附带、非注音范畴）结果面板置顶保持激活却失效：** 根因 `PositionNearSelection` 定位时用 `HWND_NOTOPMOST` 把置顶剥掉；改为按 `Topmost` 选择 `HWND_TOPMOST`/`HWND_NOTOPMOST`，并在 `OnOpened` 用 `PinButton.IsChecked` 重建一次 `Topmost`。涉及 `ResultPanelWindow.axaml.cs`。

4. **悬浮面板底色用错了主题键（主人追问）：** 原用 `Brush.Card.Background`，但深色主题下它是亮绿 `#77EE66`（非中性），且旧 `ResolveBrush` 用了不带 `ThemeVariant` 的 `TryGetResource` 重载、不展开 ThemeDictionaries，实际一直落到兜底 `#202020`；面板文字色也没随主题。改为：`ResolveBrush` 用 `Application.Current.ActualThemeVariant` 查询主题字典；面板底色改用中性 `Brush.Control.Background`（浅 `#F8FBF9`／深 `#2A342E`）加 240 alpha，随系统深浅色自动切换。补断言：面板底色等于当前主题 `Brush.Control.Background` 且非亮绿。**说明：应用本身没有手动主题开关，但会跟随 Windows 系统深浅色热切换（`ThemeManager` 监听 `PlatformSettings.ColorValuesChanged`），故需要 Light/Dark 两套色板。**

验证：Debug（独立输出目录，避开运行中进程锁）与 NativeAOT 均 **1812 checks 通过**。

## 2026-11-15 关闭

- **判断：** P2 多音提示色 + hover 候选查看达成目标；主人已真机目视确认多音提示色与 hover 候选「没问题」。
- **验证摘要：** Debug 与 NativeAOT 均 1812 checks 通过；真实词典「银行」的「行」端到端携带默认 háng + 备选 xíng；修复逐字列 LineHeight 继承溢出、底字不可框选/右键、面板行高、主题色解析等回归。
- **回写位置：** 稳定结论并入 Epic spec「P1/P2 结论」。
- **遗留事项：** 可点击常驻菜单、英语橙色标记/右键列表、日语异读交互、手动纠音/偏好持久化为后续。
