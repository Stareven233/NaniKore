---
kind: issue
title: "背景图片支持系统文件浏览选择"
type: ff
status: closed
created: 2026-07-17
---

- 做了什么：「通用 → 外观 → 背景图片」调整为 `[路径输入框][浏览…]`，点击按钮通过 Avalonia StorageProvider 打开系统文件选择框，筛选图片、单选并填入本地绝对路径；取消保留原输入，保存时沿用现有解码校验和应用逻辑。
- 改了哪些：`GeneralPage.axaml` 与 `GeneralPage.axaml.cs`，保持原有 320 宽度内并排布局；选择器异常显示错误对话框。
- 怎样验证：临时 OutputPath 下 `./scripts/dev.ps1 test` 编译通过，隔离产物全量自测 725 项通过（退出码 0）；`git diff --check` 通过。系统文件选择框交互尚未人工验收。
- codestable 影响：已同步 `codestable/spec/index.md` 的浏览选择入口与保存语义。
