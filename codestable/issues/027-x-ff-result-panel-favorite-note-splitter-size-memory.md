---
kind: issue
title: "结果面板三连修：原文区注音单词收藏补备注、原文/译文区缩放修复、窗口尺寸跨重启记忆"
type: ff
status: closed
created: 2026-10-25
---

# 结果面板三连修：原文区注音单词收藏补备注、原文/译文区缩放修复、窗口尺寸跨重启记忆

> **读者：** 关注结果面板交互与收藏数据正确性的开发者。  
> **关联文件：**  
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `.xaml.cs`  
> - `src/NaniKore/App/Controls/SelectionBubblePopup.xaml.cs`  
> - `src/NaniKore/App/NaniKoreApp.cs`  
> - `src/NaniKore/Config/AppConfig.cs` / `ConfigStore.cs` / `config.template.yaml`  
> - `src/NaniKore/Diagnostics/SelfTest.Config.cs` / `SelfTest.Ui.ResultPanel.cs` / `SelfTest.Ui.cs`

---

## 做了什么

主人实测三个结果面板 bug，一次性修复：

1. **原文区注音单词收藏丢注音**：`OnSourceViewerMouseUp` 原先只按 `RubyCopyKind.Plain` 取底字传给气泡，气泡 `OnFavoriteClick` 固定 `Note = null`，读音被丢弃。改为对选区额外按 `RubyCopyKind.ReadingOnly` 取读音，当选区确实含注音（读音≠底字）时生成备注 `读音: {reading}` 透传气泡；`SelectionBubblePopup.ShowAt(target, rect, text)` 增加可选备注参数暂存为 `_pendingNote`，收藏时随条目落库，其余入口与关闭时重置防串味。备注格式沿用精讲卡片 `读音: xxx` 口径。
2. **原文/译文区缩放无效 + 顶掉中间「译文」标题**：根因是 GridSplitter（`ResizeBehavior="PreviousAndNext"`）的 Next 目标是第 3 行「译文」标题（`Height="Auto"`）——拖拽去改一个 Auto 标题行既无意义又顶占标题，真正的译文内容在第 4 行（Star）却不是分割器直接目标。把「译文」标题与译文文本框合并为一个星号块（新第 3 行 `Height="*" MinHeight="80"`，内部 Auto 标题行 + Star 文本框行），删除原第 4 行、后续行上移；分割器 Previous=原文内容(Star)、Next=译文块(Star)，两侧皆可缩放，标题随内容整体移动、永不被顶。译文框自带 `VerticalScrollBarVisibility="Auto"`，行高变小时文本内部滚动。
3. **窗口大小跨重启不记忆**：`NaniKoreApp` 原只有会话级 `_lastPanelWidth/_lastPanelHeight`（issue 007），重启即丢、退回 XAML 默认 700×520。复用 `WindowConfig` 尺寸持久化范式（对照 `SettingsWidth/SettingsHeight`）：新增 `ResultPanelWidth=700`/`ResultPanelHeight=520`，`ConfigStore.Normalize` 增校验（宽 [440,3840]、高 [300,2160]，对齐面板 MinWidth/MinHeight），`AppConfigCloner.Clone` 同步拷贝；`EnsureResultPanel` 建窗时优先会话值、其次落盘值应用，`Deactivated`/`Closed` 时经 `PersistResultPanelSize` 写回（尺寸未变不写盘、try/catch 尽力而为）；`config.template.yaml` 的 `windows:` 段补两字段文档化。

## 怎样验证

- `./scripts/dev.ps1 test` 全绿：631 PASS / 0 FAIL（含新增 5 条断言）。
- 新增锁定自测：
  - `SelfTest.Config :: TestResultPanelSizeConfigRoundTrip`：合法值读盘恢复 + AtomicWrite 往返 + 克隆保留；越界值归一化回退 700×520。
  - `SelfTest.Ui.ResultPanel :: TestSelectionBubbleFavoriteNote`：带备注收藏落库携带读音；无备注入口 note 为空、不串味。
- 回归未破坏 issue 015 的视口契约：`TestResultPanelViewportLayout`（700×443 / 1600×860 无滚动条、统计条可见；长精讲出外层滚动条）仍全 PASS。

## 对 codestable/ 的影响

- 无既有 spec 结论失效；三处均为行为修复，不改变收藏数据模型与注音管线契约。
- 待主人实机验收：① 原文区选中注音词收藏后「生词本」备注显示读音；② 拖拽原文/译文分隔条顺畅且「译文」标题不被顶；③ 缩放后重启应用，结果面板恢复上次大小。
- 未触碰 `_todo.md`；不代为提交。
