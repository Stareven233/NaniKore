---
kind: issue
title: "vision_oneshot 提示词可配置化"
type: ff
status: closed
created: 2026-08-30
---

# vision_oneshot 提示词可配置化

`vision_oneshot` 现在拥有独立于 `local_ocr` 的可配置提示词，用户可在设置页直接编辑、保存或恢复内置默认值；视觉模板可用分隔符占位符控制原文与译文分栏。

- 改动：`src/NaniKore/Config/AppConfig.cs`、`src/NaniKore/Translation/` — 增加 `prompts.vision_oneshot`，按图像请求选择并展开独立模板，三家 provider 沿用统一分栏路由。
- 改动：`src/NaniKore/App/Settings/TranslationPage.xaml(.cs)`、`src/NaniKore/Diagnostics/SelfTest.cs` — 增加视觉提示词编辑与恢复入口，并覆盖 YAML 往返、克隆、模板展开和设置保存。
- 验证：`./scripts/dev.ps1 test`，全部无 UI 自测通过；`git diff --check` 通过。
- codestable：已同步 `codestable/spec/index.md` 的当前配置事实。
