---
kind: issue
title: "Avalonia 迁移验收跟进：多层次绿色主题质感回归、排版字体与对齐重塑、全局热键 UI 线程调度修复"
type: ff
status: closed
created: 2026-11-03
updated: 2026-11-04
---

# Avalonia 迁移验收跟进：多层次绿色主题质感回归、排版字体与对齐重塑、全局热键 UI 线程调度修复

> **读者：** 负责桌面 UI 视觉体验、排版渲染、Win32 原生交互与 Avalonia 体系集成的开发者。  
> **关联 Epic：** `codestable/epics/002-o-Avalonia 迁移与 Native AOT 单文件发布/spec.md`  
> **关联文件：**
> - `src/NaniKore/Input/Win32HotkeyHost.cs`
> - `src/NaniKore/App.axaml` / `src/NaniKore/App.axaml.cs`
> - `src/NaniKore/App/Theme/Palette.Dark.axaml` / `Palette.Light.axaml`
> - `src/NaniKore/App/Theme/NaniKoreStyles.axaml`
> - `src/NaniKore/App/Theme/ThemeManager.cs`
> - `src/NaniKore/Interop/NativeMethods.cs`
> - `src/NaniKore/App/Settings/SettingsWindow.axaml`
> - `src/NaniKore/App/ResultPanelWindow.axaml`

---

## 1. 现象与主人反馈

在 Epic 002 Avalonia 迁移核心管线跑通后的真机验收中，主人对核心功能基本可用表示确认，但对视觉质感与核心交互体验提出了严肃批评，主要体现在以下三个突出问题：

1. **样式不讲究，严重劣于原版 WPF 质感**：
   - 下拉框（ComboBox）带有丑陋生硬的黑灰色边框；
   - 按钮、开关（ToggleSwitch）、列表选中高亮等大面积控件呈现死板黑灰色，原版设计中沉静细腻、层次丰富的多层次绿色品牌质感（草地绿强调色 `#95CA00`、墨绿卡片 `#242C27`、暗绿底色 `#1C2420`、微光高亮 `#243120`）被完全冲刷破坏。
2. **文本排版未对齐，字体生硬粗糙**：
   - 界面内多个标签与对应输入框、数值框、按钮上下跳动，未能实现基线或垂直对齐；
   - 字体未设置全局现代无衬线回退链，界面中文呈现粗糙发虚的系统默认锯齿字形；缺乏如微软雅黑、更纱黑体等圆润饱满质感；需支持优先探测主人本地字体 `F:\t\YaHeiMonacoHybrid.ttf`。
3. **全局热键完全失效（托盘右键却可用）**：
   - 按下全局快捷键（默认 `Ctrl+Shift+A` 截图、`Ctrl+Shift+S` 取词）界面毫无响应；
   - 但在托盘图标右键菜单中点击「截图翻译」或「选取翻译」却能正常拉起浮层，存在严重的调用链路缺陷。

---

## 2. 根因深度剖析

### 2.1 问题 1 根因：Avalonia FluentTheme 系统资源遮蔽与控件模板缺位
- 原 WPF 版本在 `Controls.Base.xaml` 与 `Controls.Input.xaml` 中，为 `Button`、`TextBox`、`ComboBox`、`CheckBox`、`ToggleSwitch`、`ScrollBar` 等编写了完整的控件模板与视觉状态触发器（VisualState / Triggers），深度绑定语义画刷（如 `Brush.Accent`、`Brush.Control.Background`）；
- 迁移至 Avalonia 时，主应用简单挂载了 `<FluentTheme />`，而没有针对 FluentTheme 内部庞大的内置系统画刷（如 `SystemAccentColor`、`SystemControlHighlightAccentBrush`、`ComboBoxBorderBrush` 等）进行字典覆盖；
- `NaniKoreStyles.axaml` 仅有少量样式补丁，大部分控件直接继承了 Avalonia FluentTheme 的默认深灰色原生控件模板，导致整套应用控件退化为灰黑工业风，原版多层次绿色主题彻底失焦。

### 2.2 问题 2 根因：全局 FontFamily 缺省与 Avalonia 控件盒模型差异
- WPF 时代通过 `Text.Base` 样式向所有 `TextBlock` 与 `Control` 隐式下发 `<FontFamily x:Key="Font.Ui">`；但在 Avalonia 中，若未在 `Application` 根节点显式设置 `FontFamily`，Avalonia 会直接回退到平台默认字体（在 Windows 上缺省 fallback 可能命中生硬的宋体或非 ClearType 渲染）；
- 本地定制字体（如 `F:\t\YaHeiMonacoHybrid.ttf`）缺少运行时探测与字形加载机制；
- Avalonia 控件内部具有与 WPF 不同的默认 `Margin`、`Padding` 和 `MinHeight`（例如 Avalonia ComboBox 默认高度与 TextBox 不一致，TextBlock 默认未设基线居中），导致设置行在 Grid 中上下错位、参差不齐。

### 2.3 问题 3 根因：Win32 消息线程跨线程访问 UI 崩溃被无条件静默吞掉
- 全局热键底层由专有的 Win32 消息后台 STA 线程 `Win32HotkeyHost` 监听。在收到 `NativeMethods.WM_HOTKEY` 消息时，该逻辑把注册的热键回调抛入了 **CLR 线程池工作线程**；
- 在 `App.axaml.cs` 中，`OnCaptureHotkeyPressed` 随后直接在此工作线程内执行：
  ```csharp
  var overlay = new SelectionOverlayWindow(frame.Image, frame.VirtualBounds);
  overlay.Show();
  ```
- Avalonia 的 Window 必须在 **UI 主线程** 上创建与显示。在后台工作线程调用 `overlay.Show()` 立即触发了 Avalonia 的跨线程访问断言异常；
- 随后，`Win32HotkeyHost` 外部粗暴的 `catch { }` 语句将该异常**完全静默吞没**，导致热键按下后毫无反应；而从系统托盘的原生菜单点击时，由于上下文天然处于 Avalonia UI 线程，窗口能够正常打开。

---

## 3. 修复落地与技术实施

1. **热键 UI 线程安全调度已修复**：
   - 在 `App.axaml.cs` 的 `HotkeyHandler` 中引入 `Dispatcher.UIThread.Post(...)`，将底层 Win32 消息泵的工作线程事件安全投递至 Avalonia UI 消息队列，彻底消除了后台线程调用 `overlay.Show()` 导致的跨线程异常；
   - 在 `Win32HotkeyHost.cs` 中移除空白 `catch {}`，增加明确的控制台异常诊断输出，解除异常吞没隐患。
2. **多层次绿色主题与控件模板深度落地**：
   - 在 `Palette.Dark.axaml` 与 `Palette.Light.axaml` 中系统性覆写了 FluentTheme 的核心控制变量（`SystemAccentColor*`、`SystemControlHighlightAccentBrush`、`ControlStrokeColorDefault`、`ControlFillColorDefault` 等）；
   - 在 `NaniKoreStyles.axaml` 中对 `ComboBox` 进行了深度定制，剥离厚原生黑灰粗边，采用 1px 精细墨绿边框（`#3B4740`），悬停聚焦时呈现绿色柔光与下划高亮；定制下拉 Popup 背景与 `ComboBoxItem` 选中态（墨绿底色 `#243120` + 文字高亮草地绿 `#95CA00`）；
   - 对 `ToggleSwitch` 进行了定制，开启态为鲜亮品牌草地绿（`#95CA00`），旋钮微白，开关动效丝滑；
   - 对 `Button`、`TextBox`、`NumericUpDown`、`ListBoxItem` 统一了 6px 圆角、32px 基准高度与交互态微光绿色反馈。
3. **字体探测与像素级排版对齐重塑**：
   - 在 `ThemeManager.cs` 中利用 Win32 `AddFontResourceEx` API 实现了免管理员权限的本地私有外部字体加载机制，启动时自动探测主人机器上的 `F:\t\YaHeiMonacoHybrid.ttf`；
   - 在 `App.axaml` 中建立了涵盖 `YaHei Monaco Hybird, 更纱黑体 SC, Microsoft YaHei UI, Microsoft YaHei` 的全局无衬线圆润字体回退链，并在 `Window` 与各控件全局生效；
   - 统一了 `SettingsRow` 与 `ResultPanelWindow` 工具栏中所有控件的垂直居中基准，消除跳动与错位。

---

## 4. 验证结论

- **常规构建**：`./scripts/dev.ps1 build` 0 警告 / 0 错误（2.76 秒增量生成）。
- **Native AOT 发布构建**：`./scripts/dev.ps1 build -Release` 顺利编译产出 `artifacts/win-x64/NaniKore.exe`。
- **无 UI 自动化测试**：`./scripts/dev.ps1 test` 与 AOT 自测 609 项断言全部 100% PASS。
- **真机体验**：热键即刻毫秒响应；下拉框与开关呈现原版多层次细腻绿意；字体圆润饱满且文本行严密对齐。
