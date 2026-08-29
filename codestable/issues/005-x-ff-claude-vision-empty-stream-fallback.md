---
kind: issue
title: "[视觉结果分栏与错误详情]"
type: ff
status: closed
created: 2026-08-29
---

- 做了什么：移除针对上游排查临时加入的诊断日志和 Claude 非流式回退；视觉输出只使用 `\n---\n` 五字符分隔原文与译文，不再兼容旧哨兵；无分隔符时保留完整输出作为译文；结果面板可展开完整服务端错误。
- 改了哪些：`src/NaniKore/Translation/PromptTemplates.cs`、`ProviderErrors.cs` 和各 provider；`src/NaniKore/App/ResultPanelWindow.xaml(.cs)`、`NaniKoreApp.cs`；`src/NaniKore/Diagnostics/SelfTest.cs`。
- 怎么验证：`./scripts/dev.ps1 test` 通过；覆盖跨帧五字符分隔符和多模态能力错误的中文映射。
- 对 `codestable/` 的影响：无影响。
- 说明：不再生成翻译诊断日志；服务端错误的原始文本仅在结果面板的“查看完整错误”中按需显示。
