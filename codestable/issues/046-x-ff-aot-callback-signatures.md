---
kind: issue
title: "移除 .NET 11 AOT 的非 blittable 回调"
type: ff
status: closed
created: 2026-11-11
---

# 移除 .NET 11 AOT 的非 blittable 回调

修复升级遗留的四条 ILC `will always throw` 诊断：问题回调不再进入原生产物，未屏蔽诊断、未禁用运行时封送、未改写 NuGet DLL。

- 根因与改动：`ThemeManager.cs` 直接使用 Avalonia `IPlatformSettings.GetColorValues` / `ColorValuesChanged` 跟随系统主题，移除注册表读取与 SystemEvents 监听；`NaniKore.csproj` 移除 SystemEvents 包，消除两个 `ConsoleCtrlHandlerRoutine` 反向委托存根。关闭时解除订阅，丢弃已排队的旧宿主通知。
- 根因与改动：反编译 Avalonia.Win32 12.1.6，确认两个 DComposition internal 接口仅由原生客户端代理实现；库的模块初始化却无条件注册 COM 服务端虚表，导致两个含 bool 的 `UnmanagedCallersOnly` 入口被保留。`Interop/NativeAot.Substitutions.xml` 通过 ILC 支持的精确方法替换移除这两个虚表注册，客户端代理注册不变；配置文件作为 `CustomAdditionalCompileInputs`，更新时重跑编译及 ILC。
- 维护边界：NaniKore 不实现 `IDCompositionDevice` / `IDCompositionDesktopDevice` COM 服务端。升级 Avalonia 或改变该边界时必须复核裁剪配置；这不是对第三方原始生成代码的修复。
- 验证：Debug 构建零警告、零错误，Debug 与 AOT 各通过 1641 项自测；AOT 真实中英混排 OCR、桌面启动、截图消息触发/取消浮层均通过。
- 验证：AOT 原生代码映射中四个问题回调匹配数为 0，`__MicroComIDCompositionDesktopDeviceProxy.CreateTargetForHwnd` 客户端代码仍存在；改变裁剪 XML 时间戳后 `dev.ps1 publish` 确实重新生成原生代码，且四条诊断没有复现。
- 发布：`artifacts/NaniKore-v0.6.3+3-win_amd64.zip`，模板版本由 Debug 构建自动递增；包内继续遵守根目录 7 文件契约。
- codestable：已同步 `spec/index.md` 的验证边界，并在 `045-x-ff-dotnet11-upgrade.md` 回链修复结果。
