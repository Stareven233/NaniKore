---
kind: issue
title: "结果面板再修：译文区可拖动、默认尺寸 960×660、收藏时间时区修正"
type: ff
status: closed
created: 2026-10-25
---

# 结果面板再修：译文区可拖动、默认尺寸 960×660、收藏时间时区修正

> **读者：** 关注结果面板布局与收藏数据正确性的开发者。
> **关联文件：**
> - `src/NaniKore/App/ResultPanelWindow.xaml`
> - `src/NaniKore/Favorites/SqliteFavoriteService.cs`
> - `src/NaniKore/Config/AppConfig.cs` / `ConfigStore.cs` / `config.template.yaml`
> - `src/NaniKore/Diagnostics/SelfTest.Config.cs` / `SelfTest.Ui.ResultPanel.cs`
> - 承接并改写 issue 027 的分隔条/尺寸方案

---

## 做了什么

承接 issue 027 后主人再报三个问题，一次性修复：

1. **译文区不能拖动**：issue 027 已把「译文」标题并进星号块，但 GridSplitter 仍留在 issue 015 引入的整页 ScrollViewer 内容里——分隔条随滚动 extent 抖动、单向失效，原文侧能拖、译文侧拖不动。根因是「可滚动内容里的 GridSplitter 不可靠」。重构：把「原文块 + 分隔条 + 译文块」整体移出滚动容器，放进由窗口客户区限定的有界根 Grid（`Margin` 承担外边距内缩），原文块与译文块各为一个 `Height="*" MinHeight="80"` 星号块，分隔条 `ResizeBehavior="PreviousAndNext"` 位于两条星号行之间；仅下部「精讲 + 状态 + 错误」独立进一个 Auto 行的 ScrollViewer（内容超高时它自己滚，承接 issue 015 / 1.2 的可达性诉求）。分隔条脱离滚动容器后两个方向都能顺滑重分配空间，标题随所属块整体移动、永不被顶占；原文/译文各自内部本就有滚动（FlowDocumentScrollViewer / 多行 TextBox）。主题已有隐式 `GridSplitter` 样式（细线 + 悬停高亮），分隔条可见好抓。
2. **默认尺寸改为 960×660**：`AppConfig` 属性默认值、`ConfigStore.Normalize` 归一化兜底值、`config.template.yaml` 模板值、`SelfTest.Config` 断言、`ResultPanelWindow.xaml` 的 `Width`/`Height` 五处由 700/520 一并改到 960/660（持久化与钳制区间沿用 issue 027 的 `WindowConfig` 范式，下界对齐面板 `MinWidth/MinHeight`）。
3. **单词收藏时间比真实早 8 小时**：SQLite 读取 `created_at` 时 `DateTimeOffset.FromUnixTimeMilliseconds(...)` 得到 Offset=0 的 UTC 墙钟，展示端按 UTC 显示，UTC+8 环境下早 8 小时。读取处补 `.ToLocalTime()`；写路径 `ToUnixTimeMilliseconds()` 本就把本地/UTC 正确转成 Unix ms，往返一致，展示即本地时间。

## 怎样验证

- 独立临时输出目录构建（不碰被运行中 app 锁定的 bin/）：0 警告 / 0 错误。
- `--self-test` 全绿：602 PASS / 0 FAIL（ExitCode 0，`All self-tests passed`）。
- 改写锁定自测 `SelfTest.Ui.ResultPanel :: TestResultPanelViewportLayout`：断言分隔条 `PreviousAndNext` 且不在任何 ScrollViewer 之下（新增 `HasScrollViewerAncestor` 沿逻辑树上溯），下部 ScrollViewer 竖向 Auto/横向 Disabled；反向契约——下部内容真高时该 ScrollViewer 出滚动条、精讲底部与统计条可达。随之删除不再使用的 `ElementBottom`。
- 尺寸 round-trip 断言同步到 960×660：`TestResultPanelSizeConfigRoundTrip` 仍 PASS。

## 对 codestable/ 的影响

- 无既有 spec 结论失效；三处均为行为修复，收藏数据模型与时序口径不变（仅展示归位到本地时间）。
- issue 027 的「整页滚动 + 星号行 MinHeight 绑视口」方案被本 issue 的「有界根 Grid + 下部局部滚动」取代。
- 待主人实机验收：① 译文区分隔条可双向拖动且「译文」标题不被顶；② 结果面板默认以 960×660 打开；③ 生词本收藏时间显示为本地时间。
