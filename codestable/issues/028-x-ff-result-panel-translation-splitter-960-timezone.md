---
kind: issue
title: "结果面板再修：译文区独立可拖、默认尺寸 960×660、收藏时间时区修正"
type: ff
status: closed
created: 2026-10-25
---

# 结果面板再修：译文区独立可拖、默认尺寸 960×660、收藏时间时区修正

> **读者：** 关注结果面板布局与收藏数据正确性的开发者。
> **关联文件：**
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `.xaml.cs`
> - `src/NaniKore/App/Theme/Controls.Base.xaml`
> - `src/NaniKore/Favorites/SqliteFavoriteService.cs`
> - `src/NaniKore/Config/AppConfig.cs` / `ConfigStore.cs` / `config.template.yaml`
> - `src/NaniKore/Diagnostics/SelfTest.Config.cs` / `SelfTest.Ui.ResultPanel.cs`
> - 承接并改写 issue 027 的分隔条/尺寸方案

---

## 做了什么

承接 issue 027 后主人再报三个问题，一次性修复：

1. **译文区要能独立拖动放大缩小、且原文拖动不牵连译文**：主人澄清此前理解有偏——要的不是「一条共用分隔条此消彼长」，而是原文框、译文框**各自一把独立把手，只改自己高度**；两框都被拖大导致溢出时用**整页滚动**解决、不挤压彼此。实现：整个面板是一个**内容定高的 ScrollViewer**（外边框内缩由其 `Padding` 承担，页面 Grid 不把 `MinHeight` 绑到视口，滚动 extent 干净）；页面 Grid 里原文框、译文框各占一行**像素高度**（`Height="220" MinHeight="80"`），各自底边放一个 **Thumb 把手**（主题新增显式 `x:Key="ResizeGripThumb"` 细线样式，悬停/拖动高亮 + `SizeNS` 光标，带 Key 以免波及 ScrollBar 内部同名 Thumb）。Thumb 的 `DragDelta` 由代码后置 `OnSourceResizeDragDelta` / `OnTranslationResizeDragDelta` 处理，只把对应那一行的像素高度按增量改写（下限该行 `MinHeight`）——原文把手只改 Row1、译文把手只改 Row4，二者彻底独立、无零和交换。选用 Thumb + 代码后置而非 GridSplitter：独立改行高的行为确定、可无头自测锁定（GridSplitter 内置改布局在无头抛 `DragDelta` 时不可靠、测不出）。任意一框被拖高后页面变高，超出视口即整页滚动；原文/译文各自内部本就有滚动（FlowDocumentScrollViewer / 多行 TextBox），行高变小时文本内部滚。
2. **默认尺寸改为 960×660**：`AppConfig` 属性默认值、`ConfigStore.Normalize` 归一化兜底值、`config.template.yaml` 模板值、`SelfTest.Config` 断言、`ResultPanelWindow.xaml` 的 `Width`/`Height` 五处由 700/520 一并改到 960/660（持久化与钳制区间沿用 issue 027 的 `WindowConfig` 范式，下界对齐面板 `MinWidth/MinHeight`）。
3. **单词收藏时间比真实早 8 小时**：SQLite 读取 `created_at` 时 `DateTimeOffset.FromUnixTimeMilliseconds(...)` 得到 Offset=0 的 UTC 墙钟，展示端按 UTC 显示，UTC+8 环境下早 8 小时。读取处补 `.ToLocalTime()`；写路径 `ToUnixTimeMilliseconds()` 本就把本地/UTC 正确转成 Unix ms，往返一致，展示即本地时间。

## 怎样验证

- 正常构建（NaniKore 未占用 bin/）：0 警告 / 0 错误。
- `--self-test` 全绿：632 PASS / 0 FAIL（ExitCode 0，`All self-tests passed`）。
- 改写锁定自测 `SelfTest.Ui.ResultPanel :: TestResultPanelViewportLayout`：
  - 结构：页面挂两个独立 Thumb 把手、原文/译文两框为像素高度行且位于滚动器内；
  - 行为：对 `SourceResizeThumb` 抛 `DragDelta(+60)` 断言只 Row1 增高、Row4（译文）不动；对 `TranslationResizeThumb` 抛 `DragDelta(+60)` 断言只 Row4 增高、Row1 保持；
  - 溢出：两者都拖高后页面超出视口、整页滚动条出现。
  - 过程记录：曾试 GridSplitter（Absolute 行 + ScrollViewer），无头抛 `DragDelta` 不能触发其改布局（两「resizes only」断言 FAIL），故改 Thumb + 代码后置。
- 尺寸 round-trip 断言同步到 960×660：`TestResultPanelSizeConfigRoundTrip` 仍 PASS。

## 对 codestable/ 的影响

- 无既有 spec 结论失效；三处均为行为修复，收藏数据模型与时序口径不变（仅展示归位到本地时间）。
- issue 027 的「整页滚动 + 星号行 + GridSplitter 零和分隔」方案被本 issue 的「内容定高滚动页 + 每框独立 Thumb 把手」取代。
- 待主人实机验收：① 原文/译文各自底边把手能单独拖动放大缩小，拖一边不影响另一边；② 两框都拖大后整页出现滚动条、不挤压彼此；③ 面板默认 960×660 打开；④ 生词本收藏时间显示为本地时间。
- 可选后续：两框的把控高度目前随会话重置为 220，未跨重启持久化；若主人需要可仿 `WindowConfig` 增补两个字段。
