# Native AOT 的 DComposition 裁剪边界

`src/NaniKore/Interop/NativeAot.Substitutions.xml` 只针对 Avalonia Win32 生成的两个 DComposition COM 服务端虚表注册。NaniKore 使用 Windows 提供的 DComposition 设备，不实现这些 COM 服务端，因此 Native AOT 不需要保留对应回调。

替换必须保持精确：只移除 `IDCompositionDevice` 与 `IDCompositionDesktopDevice` 的服务端虚表注册。创建窗口合成目标所需的客户端代理必须保留；不得清空整个模块初始化，也不得全局关闭运行时封送。

这依赖 Avalonia 的生成实现和 Native AOT 的裁剪行为。升级 Avalonia 或 .NET、开始实现托管 DComposition 接口、或修改窗口合成路径时，先核对替换目标及客户端代理仍然有效，再运行 Debug 自测、Native AOT 自测和桌面窗口回归。上游修复后应验证能否移除替换配置。

项目文件通过 `CustomAdditionalCompileInputs` 跟踪替换 XML，确保该配置变化会触发 AOT 重新编译。
