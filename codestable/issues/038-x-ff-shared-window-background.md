---
kind: issue
title: "设置与结果面板共用本地背景图片"
type: ff
status: closed
created: 2026-07-17
---

在「通用 → 外观」增加共用背景图片路径、图片模糊和不透明度，默认不设置。当前局部布局：

```text
通用 → 外观
  界面字体 / 提示词字体
  背景图片       [本地绝对路径或相对程序目录路径]
  图片模糊       [0–100]
  图片不透明度   [0–100%]
保存 → 设置面板与结果面板同步刷新；清空路径 → 恢复默认背景
```

- 改动：`WindowConfig`、克隆、归一化与模板接入三个 `windows.background_*` 字段；`WindowBackgroundResources` 共用已解码位图，两个窗口增加不拦截输入的独立图片层，模糊与透明度不影响文字控件。
- 校验：保存时真实解码图片并提示读取错误；运行期图片不可读仅关闭图片层，保留路径。相对路径始终按 `AppContext.BaseDirectory` 解析，背景等比例填满。
- 验证：默认构建受正在运行的 NaniKore 文件锁阻挡；以临时 `OutputPath` 执行 `./scripts/dev.ps1 test` 编译成功，隔离产物自测全部通过（725 checks，退出码 0），覆盖配置往返、空值/非法数值、相对/绝对路径、损坏图片与两个既有窗口资源热刷新。尚未人工验收视觉效果。
- codestable：已同步 `codestable/spec/index.md` 的当前外观契约。
