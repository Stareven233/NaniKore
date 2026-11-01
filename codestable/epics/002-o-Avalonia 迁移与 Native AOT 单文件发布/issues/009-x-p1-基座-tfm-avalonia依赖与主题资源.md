---
kind: issue
title: "P1 基座：TFM 改 net10.0、Avalonia 核心依赖引入、主题资源搬迁与 dev.ps1 AOT 通道"
type: feature
status: closed
created: 2026-11-01
closed: 2026-11-02
---

## 目标

落实 Epic 002 P1 基座批次的所有要求：
1. **项目工程改造**：`NaniKore.csproj` 的 TargetFramework 从 `net10.0-windows10.0.19041.0` 统一为 `net10.0`；移除历史包袱 `UseWPF` 与 `UseWindowsForms`；
2. **依赖补齐与引入**：
   - 补齐 `System.Security.Cryptography.ProtectedData`（DPAPI 密钥加解密）与 `Microsoft.Win32.SystemEvents`（系统深浅色主题广播监听）；
   - 引入 Avalonia 12.1.4 官方套件（`Avalonia`、`Avalonia.Desktop`、`Avalonia.Themes.Fluent`）与 `SkiaSharp` 3.119.4；
   - 暂保留 `System.Drawing.Common` 供 P2–P4 批次平滑过渡，并在 AOT 发布时配置原生调试符号清理；
3. **主题与应用基座落地**：
   - 将穿刺 8 验证通过的 Route A 资产（`Palette.Dark.axaml`、`Palette.Light.axaml`、`NaniKoreStyles.axaml`）搬入 `src/NaniKore/App/Theme/`，淘汰旧版 1060 行自定义 WPF 控件模板；
   - 落地 Avalonia `App.axaml` 与 `App.axaml.cs`，并重写 `ThemeManager` 支持 Avalonia `ThemeVariant` 与 DWM 标题栏深色联动；
   - 搭建 Avalonia 基座窗口 `MainWindow.axaml`；
4. **工具链与脚本升级**：
   - `scripts/dev.ps1` 增加 `aot` 发布通道（Debug 构建保持 JIT，不受影响）；
   - 校准 `$debugExecutable` 路径到 `net10.0`，保证 `dev.ps1 build/test/run` 原语义不变；
5. **未迁移 UI 代码隔离与核心自测保持**：
   - 将待 P2–P4 重写的旧 WPF/WinForms 视图和交互代码在工程中显式排除，清理 `GlobalUsings.cs` 的 WPF/WinForms 别名；
   - 确保核心业务无 UI 自测（YAML、DPAPI、翻译、注音、精讲、收藏等）与新增的主题资源解析测试全绿。

## 结论（2026-11-02）

**P1 基座批次目标全部达成，基座圆满落地。**

1. **工程彻底脱离双引擎**：TFM 成功变更为 `net10.0`，移除了 `UseWPF` 与 `UseWindowsForms`，清理了 `GlobalUsings.cs` 中的双引擎别名。
2. **Avalonia 12 渲染基座就位**：成功引入 Avalonia 12.1.4 核心套件、FluentTheme 与 SkiaSharp 3.119.4。`App.axaml` 搭载 Route A 语义色板（`Palette.Dark.axaml` / `Palette.Light.axaml`）与精简局部样式 `NaniKoreStyles.axaml`，彻底告别 1060 行旧 WPF 模板维护负担。
3. **ThemeManager 热切换顺畅**：重写后的 `ThemeManager` 直接对接 Avalonia `ThemeVariant` 与 Windows DWM 沉浸式深色标题栏联动，运行时热切换零闪烁。
4. **工具链双通道跑通**：`scripts/dev.ps1` 校准了 `net10.0` 输出路径并增加了 `aot` 发布通道。
   - `./scripts/dev.ps1 build`：Debug 构建保持高效 JIT，**0 警告、0 错误**；
   - `./scripts/dev.ps1 test`：核心业务（YAML、DPAPI、翻译流、形态素注音、微结构精讲、SQLite收藏等）与新主题色板解析、WCAG AAA 对比度校验**全绿通过（All self-tests passed）**；
   - `./scripts/dev.ps1 aot`：成功生成 `artifacts\win-x64-aot` NativeAOT 产物，精简 exe 20.7 MB + 4 个原生 DLL 旁置，零 .NET 运行时依赖。

## 完成判据（来自 epic spec P1）与结果

| # | 判据 | 结果 | 支撑 |
|---|---|---|---|
| 1 | Debug 跑起来一个空/基座窗口，深浅色与主题资源可解析 | **PASS** | `MainWindow.axaml` 呈现基座界面，深浅色主题切换与资源解析正常 |
| 2 | `./scripts/dev.ps1 build` 编译成功（0 警告、0 错误） | **PASS** | Debug 编译输出 `0 个警告 0 个错误`，耗时约 8.5 秒 |
| 3 | `./scripts/dev.ps1 run` 启动应用呈现基座窗口 | **PASS** | Avalonia 经典桌面生命周期启动成功 |
| 4 | `./scripts/dev.ps1 test` 执行自测通过 | **PASS** | 447 行自测断言全绿，`All self-tests passed.` 退出码 0 |
| 5 | `./scripts/dev.ps1 aot` 可触发 NativeAOT 发布通道 | **PASS** | 成功生成 20.7 MB 精简 AOT exe 与旁置 DLL，退出码 0 |

## 验证记录

- `.\scripts\dev.ps1 build`：退出码 0，产出 `src\NaniKore\bin\Debug\net10.0\NaniKore.dll`。
- `.\scripts\dev.ps1 test`：退出码 0，全量断言通过。
- `.\scripts\dev.ps1 aot`：退出码 0，产出 `artifacts\win-x64-aot\NaniKore.exe` 与 4 个随包旁置原生 DLL（`av_libglesv2.dll`, `e_sqlite3.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`）。

## 后续切入点（P2 平台服务）

P1 基座已经扎实打牢，下一步将开始推进 **P2 平台服务** 批次：
- 托盘（Avalonia `TrayIcon` + 自绘 `WS_EX_NOACTIVATE` 提示小窗）；
- 全局热键（独立 STA 线程 Win32 消息专用窗口）；
- 剪贴板网关（Win32 原生 API 替代 WinForms Clipboard）；
- 屏幕捕获与 PNG 编码（BitBlt 直灌 `SKBitmap`，移除 GDI+ `CopyFromScreen`）；
- 选区浮层（Avalonia 全屏无边框自绘置顶窗口）；
- 消息对话框与应用图标。
