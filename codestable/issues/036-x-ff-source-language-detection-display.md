---
kind: issue
title: "源语言自动探测结果显示"
type: ff
status: closed
created: 2026-11-08
---

自动模式在原文载入、更新和流式转录时显示「语言（自动）」，空原文恢复「自动探测」。保留自动模式语义与手选语言的粘性，显示更新不依赖注音开关或词典可用性。

- 改动：`src/NaniKore/App/ResultPanelWindow.axaml.cs` 刷新自动选项标签，屏蔽标签刷新引起的选择事件。
- 验证：`src/NaniKore/Diagnostics/SelfTest.Ui.ResultPanel.cs` 增加注音关闭、跨原文重探测、手选保持、切回自动、空原文和流式转录的回归检查；`./scripts/dev.ps1 test` 返回退出码 0（当前终端未捕获测试输出）；本次代码 `git diff --check` 通过。
- codestable：无影响，保留默认自动检测与手选后锁定的既有契约。
