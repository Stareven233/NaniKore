# NaniKore

轻量的 Windows 截图、OCR 与翻译工具。当前 `main` 是单一 C#/.NET 进程实现；旧 Go/Gio 版本保留在 `legacy-go` 分支。

## 当前能力

- **桌面外壳**：.NET 10 + WinForms，单进程、单 Windows 消息循环
- **截图与选区**：WinForms/GDI，Per-Monitor V2 物理像素坐标
- **OCR**：ONNX Runtime C# 直接运行 PP-OCRv6-small 检测与识别模型
- **交互**：全局 `Ctrl+Shift+A`，左键拖选，Esc/右键取消，结果自动复制并显示
- **本地翻译（后续可选）**：GGUF + llama.cpp 兼容运行时
- **不再依赖**：Python、leaf-ocr、OpenCV、Gio、Go 工具链

当前 OCR 后处理使用纯 C# 连通域和水平矩形框近似官方 DB 多边形后处理，优先保证体积与依赖简洁。水平游戏/UI 文本已经跑通；旋转或弯曲文本仍弱于 OpenCV 完整实现。

## 一键准备本地工具链

SDK 与 NuGet 缓存不绑定项目目录或任何个人磁盘路径；模型仍保留在项目目录：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/bootstrap.ps1
```

它会准备：

- 已安装并配置到 `Path` 的 .NET SDK：脚本会优先直接复用
- `NUGET_PACKAGES` 指定的 NuGet 缓存：可由多个项目共用
- `models/pp-ocr-v6-small`：官方 PaddlePaddle ONNX 模型与配置

如果本机已安装兼容 `global.json` 的 SDK，直接运行开发命令即可。否则运行上面的 bootstrap 脚本，它会将 SDK 安装到 `DOTNET_ROOT` 指定的共享位置。安装后，将该目录加入系统 `Path`（优先于旧版 dotnet），并将 `DOTNET_ROOT` 指向该目录；如需将包缓存放在共享磁盘，再设置 `NUGET_PACKAGES`。重新打开终端后即可直接使用 `dotnet`。模型文件被 Git 忽略。

## 开发命令

```powershell
# 构建
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 build

# 无 UI 自测
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test

# 运行桌面程序
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 run

# 发布 Windows x64 framework-dependent 版本
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 publish
```

运行后按 **Ctrl+Shift+A**，左键拖选；按 **Esc** 或右键取消。选区位图只在内存中传给 OCR，不写入临时文件。

## OCR 诊断命令

```powershell
# 查看 ONNX 输入输出元数据
dotnet run --project src\NaniKore\NaniKore.csproj -- --inspect-models

# 直接识别一张图片
dotnet run --project src\NaniKore\NaniKore.csproj -- --ocr-file path\to\image.png
```

## 发布布局与体积

发布目录保留一个托管单文件 EXE，并把 ONNX Runtime 原生库明确分开：

```text
artifacts/win-x64/
├─ NaniKore.exe
├─ onnxruntime.dll
└─ onnxruntime_providers_shared.dll
```

Windows x64 framework-dependent 发布总计约 **40.18 MiB**；运行必需的 PP-OCRv6-small 检测与识别文件约 **29.75 MiB**，完整应用约 **69.93 MiB**。

目标机器需要 .NET 10 Desktop Runtime。真正免安装的 self-contained 构建可后续按需提供，但体积会明显增大。

## 已知边界

- 主显示器上的真实键鼠截图与 OCR 已验收通过。
- 自动化冒烟测试已连续完成两轮截图、拖选、OCR 和关闭结果窗。
- 副显示器、负坐标虚拟桌面与混合 DPI 代码路径已实现，但当前没有副显示器设备，留待以后补实机回归。
- 旋转、弯曲和竖排文本暂不作为当前轻量 OCR 后处理的目标。
