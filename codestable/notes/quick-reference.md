# NaniKore 开发速查

## 项目本地环境

```powershell
# 首次准备 .NET SDK、NuGet 包和 PP-OCRv6-small 模型
powershell -ExecutionPolicy Bypass -File scripts/bootstrap.ps1

# 只准备或校验模型
powershell -ExecutionPolicy Bypass -File scripts/bootstrap-models.ps1
```

The shared .NET SDK is resolved from `Path` or `DOTNET_ROOT`; configure the global NuGet cache with `NUGET_PACKAGES`. Models remain in project-local `models/`.

## 常用命令

```powershell
# 构建
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 build

# 10 项无 UI 自测
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test

# 运行 GUI
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 run

# 发布 EXE + ONNX Runtime 原生 DLL
powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 publish
```

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
scripts/           本地工具链、模型、构建与发布脚本
models/            本地模型（仅 README 被 Git 跟踪）
artifacts/win-x64/ 发布产物（Git 忽略）
Shared .NET SDK: resolved from `Path` / `DOTNET_ROOT`; NuGet cache: configured by `NUGET_PACKAGES`.
```

## 分支

- `main`：当前 C#/.NET 实现。
- `legacy-go`：已停止开发的 Go/Gio 历史实现。

## 已知边界

- 副显示器、负坐标虚拟桌面与混合 DPI 等待有设备时补实机回归。
- 当前 OCR 检测后处理偏向水平文本，不优先处理旋转、弯曲和竖排文本。
