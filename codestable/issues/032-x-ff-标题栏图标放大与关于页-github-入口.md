---
kind: issue
title: "[快改] 标题栏图标放大到 20px，关于页加 GitHub 图标跳转"
type: ff
status: closed
created: 2026-11-07
updated: 2026-11-07
---

<!-- 快改痕迹：轻。读者只要 30 秒扫完。禁止迷你 Design。 -->

# [快改] 标题栏图标放大到 20px，关于页加 GitHub 图标跳转

> **读者：** 以后搜到这条时——「改了啥、怎么信、动没动制度记忆」。  
> **自检（四答即可，可合成短段，勿空标题凑节）：** 做了什么 · 改了哪些文件 · 怎么验证 · 对 `codestable/` 有无影响。

---

主人提了两件小事：结果面板与设置窗标题栏左上角的应用图标偏小（原 16px，比 13px 标题文字几乎一样高，立不住）；关于页缺一个去 GitHub 仓库的入口。方案由主人拍板：图标取 20px（不取 22px，避免挤压拖动空区呼吸感）；GitHub 入口取「品牌卡标题行右上角图标按钮」而不是新增一行设置行，紧凑且不打断关于页现有的品牌→隐私叙事。

- 改动：`src/NaniKore/App/ResultPanelWindow.axaml` / `src/NaniKore/App/Settings/SettingsWindow.axaml` — `TitleBarIcon` 的 `Width/Height` 16 → 20，两个窗口保持同款；标题栏高 34 不变，垂直仍居中。ico 内含 256px 帧，放大无插值糊化。
- 改动：`src/NaniKore/App/Settings/AboutPage.axaml` — 品牌卡标题行由横向 `StackPanel` 改为 `Auto,*,Auto` 的 `Grid`：左 40px 应用图标、中间名称与版本、右侧 `RepositoryButton`（`Classes="icon"` + GitHub 官方 mark 的 `PathIcon`，16px，`VerticalAlignment="Top"` 与图标顶端对齐）。图标沿用 `Button.icon` 既有的透明底、悬停微光与手型光标，不新增样式。
- 改动：`src/NaniKore/App/Settings/AboutPage.axaml.cs` — 新增 `public const string RepositoryUrl = "https://github.com/Stareven233/NaniKore"` 作为唯一地址源；构造函数把 ToolTip 写成「在浏览器打开 GitHub 仓库 + 裸地址」（Avalonia 里 `Button.ToolTip` 属性不存在，用 `ToolTip.SetTip`）；`OnRepositoryButtonClick` 走 `Process.Start` + `UseShellExecute` 拉起默认浏览器，失败时 `MessageDialog` 弹错，与 `TranslationPage.OpenDataDirectory` 同一范式。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Ui.Settings.cs` — 新增 `TestAboutPageRepositoryEntry`：断言按钮可见可点、ToolTip 内含仓库地址、`RepositoryUrl` 常量与期望地址一致；不真的拉起浏览器（自测无 UI 且不应联网）。
- 验证：`scripts/dev.ps1 build` 0 警告 0 错误（版本 0.6.0+56 → +57）；`NaniKore.exe --self-test` 全绿 664 checks / 0 FAIL，其中 `about page exposes a clickable GitHub repository button`、`about page repository button targets the project GitHub repository` 两条新断言 PASS，既有 `TestSettingsPagesUseAdaptiveLayout` 对关于页卡片布局的约束未破。
- 注：本次 `dev.ps1 build` 收尾的 native DLL 同步步骤报 `onnxruntime.dll` 被占用（有 NaniKore 实例在跑），编译与自测均不受影响；关掉实例后重跑即恢复。
- codestable：无影响（spec 只记载「设置页包含关于页」，页面内多一个外链入口不改变该真相；无既定取舍被推翻）。
