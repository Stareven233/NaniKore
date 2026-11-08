---
kind: issue
title: "精讲区随结果窗口分配剩余高度"
type: ff
status: closed
created: 2026-11-08
---

# 精讲区随结果窗口分配剩余高度

精讲展开时占据窗口剩余高度，原文与译文继续通过各自把手独立调整。精讲内容超出时内部滚动；窗口过小时保留整页滚动。

- 改动：续完 `ResultPanelWindow.axaml` / `.axaml.cs` 的星号行与视口最小高度逻辑；折叠时释放页面最小高度；`NaniKoreStyles.axaml` 补齐折叠控件内容拉伸；移除自测遗留必失败 DEBUG 断言。
- 验证：`./scripts/dev.ps1 test` 退出码 0；覆盖高度变化、长结构化精讲、小视口整页滚动、前两区独立行高与折叠后释放页面下限。尚未人工拖窗验收。
- codestable：无稳定 spec 变动；补充历史 issue 028 的布局策略，原文/译文独立像素行与整页滚动约束继续成立，精讲展开后独占剩余空间。
