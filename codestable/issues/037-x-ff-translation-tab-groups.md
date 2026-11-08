---
kind: issue
title: "翻译与识别流程设置移入翻译页"
type: ff
status: closed
created: 2026-07-17
---

将通用页的「翻译」「识别流程」两组整体移至翻译页顶部，并迁移配置读取、校验和保存逻辑。当前布局：

```text
通用：外观 → 原文注音 → 本地 OCR 依赖（仅本地流程）
翻译：翻译 → 识别流程 → 配置档 → 服务商 → 凭据 → 提示词
```

- 改动：`GeneralPage.axaml[.cs]`、`TranslationPage.axaml[.cs]`；同步提示词中目标语言所在页面的说明。
- 联动：`SettingsWindow.axaml.cs` 在返回通用页时同步尚未保存的流程选择，保留其它字段的编辑状态。
- 验证：`./scripts/dev.ps1 test` 通过（退出码 0）；既有设置测试已迁移，并覆盖跨页切换时依赖组可见性与字体编辑保留。
- codestable：无现有规格漂移；本记录保留本次局部布局关系。
