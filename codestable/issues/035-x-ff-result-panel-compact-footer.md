---
kind: issue
title: "结果面板紧凑正文、词汇收藏图标与底部文字栏"
type: ff
status: closed
created: 2026-11-08
---

# 结果面板紧凑正文、词汇收藏图标与底部文字栏

- 做了什么：原文、译文字号降到 15，默认高度分别为 96 / 72px，支持两行注音原文 / 译文；词汇收藏图标增至 24px，并清除按钮内边距。指标与状态合为窗口底部无边框文字行，左指标、右状态，列宽按 2:1 分配以优先容纳较长指标，空文案隐藏，错误保留强调色与详情入口。
- 改动：`ResultPanelWindow.axaml` / `.axaml.cs`、`ExplanationCardView.axaml`、`SelfTest.Ui.ResultPanel.cs`。
- 验证：通过 `./scripts/dev.ps1 test`（设置临时 OutputPath 绕开正在运行应用对 Debug exe 的锁定），退出码 0；覆盖底栏左右布局、错误提示与详情清理、指标显隐及原有精讲高度回归。尚未人工视觉验收。
- codestable：无稳定 spec 变动；延续 034 的精讲剩余空间策略，状态与指标迁至独立底部行，折叠精讲时也保持贴底。
