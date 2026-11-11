---
kind: issue
title: ".NET 11 / C# 15 工具链升级"
type: ff
status: closed
created: 2026-11-11
---

# .NET 11 / C# 15 工具链升级

将 SDK 最低版本提升至 11.0.100、目标框架提升至 net11.0，沿用正式版 latest 语言设置（C# 15），不启用预览特性；共享 DOTNET_ROOT 并存保留 SDK 10.0.302。

- 改动：`global.json`、`src/NaniKore/NaniKore.csproj`；Avalonia 三个直接引用统一为 12.1.6，ProtectedData / SystemEvents 为 11.0.0，其余业务依赖保持原版本。
- 改动：`scripts/dev.ps1` 与 `scripts/serve-preview.ps1` 对齐 net11.0；预览只定位当前框架，避免启动旧残留产物；更新 README 与原生接口注释。Debug 构建按约定递增模板版本至 `0.6.3+2`。
- 验证：bootstrap 安装和还原成功；`dev.ps1 build` 零警告、零错误；`dev.ps1 test` 1641 项通过；`dev.ps1 build -Release` 与 `dev.ps1 publish` 成功；AOT exe 自测 1641 项通过。
- 验证：AOT 识别生成的 `Hello .NET 11 中文识别测试` 图片成功，JSON 输出正确；桌面启动、热键消息触发截图浮层及取消后存活检查通过。轻量 ZIP 为 `artifacts/NaniKore-v0.6.3+2-win_amd64.zip`。
- 已知风险：ILC 报告 `ConsoleCtrlHandlerRoutine` 两个反向委托存根、Avalonia DComposition 两个 `CreateTargetForHwnd` 存根包含非 blittable 布尔签名，若调用会抛异常。更新依赖后仍存在；上述自测、OCR 和桌面冒烟未触发，尚未覆盖所有回调路径。未屏蔽诊断、未全局禁用运行时封送；升级完成不表示该上游问题已修复。
- codestable：已同步 `codestable/spec/index.md` 的宿主、UI 依赖、工具链与验证边界；历史 .NET 10 迁移记录保留。
