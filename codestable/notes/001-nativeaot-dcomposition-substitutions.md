# Native AOT：DComposition 精确裁剪的维护边界

这份配置是针对 Avalonia.Win32 12.1.6 的有界处理：NaniKore 只调用 Windows 提供的 DComposition 设备，不实现对应 COM 服务端，因此可以移除两个未使用的服务端虚表注册。它依赖第三方内部生成代码，升级 Avalonia / .NET 或改变 COM 使用方式时必须复核；上游修复后应验证能否停止引用，而不是永久照搬。

## 为什么它能消除诊断

.NET 11 Native AOT 不允许启用运行时封送的 `UnmanagedCallersOnly` 入口使用 `bool` 等非 blittable 签名。这类入口不会自动执行普通 P/Invoke 的参数转换；Win32 `BOOL` 的 ABI 是 32 位整数，不能简单按单字节布尔值处理。

Avalonia.Win32 为 DComposition 生成了两种方向的代码：

| 方向 | 用途 | 本项目是否需要 |
| --- | --- | --- |
| 客户端代理（Proxy） | 托管程序调用 Windows 原生设备，例如创建窗口合成目标 | 需要，必须保留 |
| COM 服务端虚表（VTable / CCW） | 原生代码回调托管程序实现的设备接口 | 当前不需要 |

12.1.6 的模块初始化会无条件注册服务端虚表，导致两个带 `bool topmost` 的 `CreateTargetForHwnd` 回调进入原生产物，ILC 报告 `will always throw`。反编译该版本确认，这两个 internal 接口的实现均为原生客户端代理；NaniKore 没有托管设备实现。

`NativeAot.Substitutions.xml` 将以下两个 `System.Void __MicroComModuleInit()` 方法替换为空方法，使无用虚表及错误回调失去保留入口：

- `Avalonia.Win32.DComposition.Impl.__MicroComIDCompositionDeviceVTable`
- `Avalonia.Win32.DComposition.Impl.__MicroComIDCompositionDesktopDeviceVTable`

这是 ILC 支持的方法体替换，**不是仅修改裁剪元数据或隐藏诊断**。它没有修正第三方原始 DLL 的 ABI，也没有删除客户端代理注册。不能扩大为清空整个模块初始化、删除所有 DComposition 代码或全局禁用运行时封送。

另外两个 `ConsoleCtrlHandlerRoutine` 反向委托诊断来自原 SystemEvents 依赖；主题监听已经改用 Avalonia `IPlatformSettings.ColorValuesChanged` 并移除该依赖，和这份 XML 属于两项独立处理。

## 何时必须重新判断

- **升级 Avalonia**：内部类型名、初始化内容、接口实现及生成方式都可能改变。旧规则可能不再匹配，也可能开始移除必要能力；不能只看编译是否成功。
- **升级 .NET / ILC**：签名检查、替换文件支持和优化行为可能改变，需要重验原生产物。
- **开始实现托管 DComposition 设备接口**：当前“不需要服务端虚表”的前提失效，必须调整方案，不能继续裁掉对应注册。

复核时先查看新版包中两个初始化方法的实际内容，并区分 Proxy 和 VTable；确认是否存在需要导出给原生代码的托管实现。如果上游已修复签名，先在停止引用替换配置的情况下重新验证，通过后移除项目中的构建引用。

## 如何证明处理仍然正确

1. 使用项目脚本构建 Debug 并运行自测，再构建 Native AOT。构建日志不得重新出现这些 `non-blittable` / `will always throw` 诊断；Debug 成功不能替代 AOT 验证。
2. 运行 AOT exe 自测、真实 OCR，以及桌面截图浮层显示与取消，确认窗口和渲染路径可用。
3. 检查原生代码映射，确认问题回调不在产物中，实际使用的客户端 `CreateTargetForHwnd` 仍在。
4. 只更新替换 XML 的时间戳后再次构建，确认 ILC 重新执行。项目通过 `CustomAdditionalCompileInputs` 追踪该文件；缺少这一项可能使增量构建继续复用旧产物。

生成原生代码映射：

```powershell
$env:IlcGenerateMapFile = 'true'
./scripts/dev.ps1 build -Release
$env:IlcGenerateMapFile = $null
```

映射默认位于 `src/NaniKore/obj/Release/net11.0/win-x64/native/NaniKore.map.xml`；目标框架升级后相应调整路径。搜索时：

- `ConsoleCtrlHandlerRoutine` 和 `__MicroComIDComposition(Device|DesktopDevice)VTable.*CreateTargetForHwnd` 应无匹配。
- `__MicroComIDComposition(Device|DesktopDevice)Proxy.*CreateTargetForHwnd` 应能找到实际使用的客户端代码；原生符号可能以内联或不同命名形式出现，需要结合新版实现核实，不能机械依赖旧符号名。

## 当前落点与证据

- [裁剪配置](../../src/NaniKore/Interop/NativeAot.Substitutions.xml)：精确方法列表与适用版本。
- [项目配置](../../src/NaniKore/NaniKore.csproj)：仅在 `PublishAot=true` 时传入 `--substitution`，并声明增量编译输入。
- [修复记录](../issues/046-x-ff-aot-callback-signatures.md)：2026-11-11，Debug / AOT 各 1641 项自测、真实 OCR、桌面浮层及增量构建通过；原生映射中四个问题回调均不存在，DComposition 客户端代码保留。
