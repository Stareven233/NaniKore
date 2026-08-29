# NaniKore 开发速查

## 项目本地环境

```powershell
# 首次准备 .NET SDK 和 NuGet 包
./scripts/bootstrap.ps1
```

The shared .NET SDK is resolved from `DOTNET_ROOT` first, then falls back to `Path`; configure the global NuGet cache with `NUGET_PACKAGES`.

## 常用命令

```powershell
# 构建
./scripts/dev.ps1 build

# 无 UI 自测
./scripts/dev.ps1 test

# 运行 GUI
./scripts/dev.ps1 run

# 发布轻量 ZIP：不包含固定模型和 native DLL
./scripts/dev.ps1 publish

# 发布完整 ZIP：包含 deps/ 下的模型和 DLL
./scripts/dev.ps1 publish -full

# 仅启动已有的 Debug / 发布版 EXE（不重复构建或发布）
./scripts/dev.ps1 -Launch build
./scripts/dev.ps1 -Launch publish
```

首次使用本地 OCR 时，从设置页的“通用”进入“本地 OCR 依赖”，点击“下载依赖”并确认。文件会保存到当前可执行程序目录下的 `deps/`，应用不会静默自动下载。

## 诊断命令

```powershell
# 查看 ONNX 输入输出
dotnet run --project src\NaniKore\NaniKore.csproj -- --inspect-models

# 识别指定图片
dotnet run --project src\NaniKore\NaniKore.csproj -- --ocr-file path\to\image.png

# 直接运行发布版自测
artifacts\win-x64\NaniKore.exe --self-test
```

## 当前交互

- `Ctrl+Shift+A`：截图并进入框选。
- 左键拖选：确认区域并开始 OCR。
- `Esc` 或右键：取消框选。
- OCR 结果：自动复制到剪贴板并显示结果窗口。

## 目录

```text
src/NaniKore/      C# 应用源码
deps/              唯一的本地 OCR 与 ONNX Runtime 依赖源
scripts/           本地工具链、模型、构建与发布脚本
artifacts/win-x64/ 发布产物（Git 忽略）
```

## 分支

- `main`：当前 C#/.NET 实现。
- `legacy-go`：已停止开发的 Go/Gio 历史实现。

## 已知边界

- 副显示器、负坐标虚拟桌面与混合 DPI 等待有设备时补实机回归。
- 当前 OCR 检测后处理偏向水平文本，不优先处理旋转、弯曲和竖排文本。
