---
kind: issue
title: "vision_oneshot 提示词可配置化"
type: ff
status: closed
created: 2026-08-30
---

# vision_oneshot 提示词可配置化

`vision_oneshot` 现在拥有独立于 `local_ocr` 的可配置提示词，用户可在设置页直接编辑、保存或恢复内置默认值；默认视觉模板直接展示由空行包围的 `---` 分隔符，设置页的恢复按钮与各自标题/描述同一行并靠右。

- 改动：`src/NaniKore/Config/AppConfig.cs`、`src/NaniKore/Translation/` — 增加 `prompts.vision_oneshot`，按图像请求选择并展开独立模板，三家 provider 沿用统一分栏路由。
- 改动：`src/NaniKore/App/Controls/SettingsGroup.cs`、`src/NaniKore/App/Theme/Controls.Base.xaml`、`src/NaniKore/App/Settings/TranslationPage.xaml(.cs)`、`src/NaniKore/Diagnostics/SelfTest.cs` — 为堆叠设置行增加可选标题操作槽，将视觉提示词编辑与恢复入口放在同一标题行，并覆盖 YAML 往返、模板展开、分隔格式和设置保存。
- 验证：Release 配置运行 `--self-test`，全部无 UI 自测通过；`git diff --check` 通过。Debug 测试入口因运行中的 NaniKore 实例锁定输出 EXE 未重复执行。
- codestable：已同步 `codestable/spec/index.md` 的当前配置事实。
