---
kind: issue
title: "背景图拉伸与共用面板底色不透明度"
type: ff
status: closed
created: 2026-07-17
---

在「通用 → 外观」增加一个共用参数，局部布局如下：

```text
背景图片            [路径][浏览…]
图片模糊            [0–100]
图片不透明度        [0–100%]
面板底色不透明度    [0–100%]  默认 100%
保存 → 两个窗口同步刷新底色；无有效背景图 → 原有不透明外观
```

- 做了什么：背景改为按窗口宽高直接拉伸，完整显示、不裁切，比例不同时允许变形；设置与结果面板的侧栏、上下栏、卡片及输入框底色共用不透明度，仅图片成功加载时启用。仅调整画刷，不影响文字、图标与边框。
- 改了哪些：`WindowConfig.PanelBackgroundOpacity`（YAML `windows.panel_background_opacity`）、克隆/归一化/模板与通用页；`WindowBackgroundResources` 发布有效透明度，深浅色板增加专用画刷，目标窗口样式接入，其他窗口和菜单原画刷保留。
- 怎样验证：临时 OutputPath 下 `./scripts/dev.ps1 test` 编译成功，隔离产物 1178 项自测全部通过（退出码 0）；覆盖配置往返、百分比转换与边界、实际侧栏/标题栏/SettingsGroup 模板卡片/文本框、深浅主题往返、保存热刷新与清空/缺失图片后恢复。人工视觉验收待用户使用确认。忽略既有 CRLF 行尾的差异空白检查通过。
- codestable 影响：已同步 `codestable/spec/index.md` 的图片填充和面板底色契约。
