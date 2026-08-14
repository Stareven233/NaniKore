# NaniKore

> 轻量的 Windows 截图 OCR 与翻译工具。

按下全局快捷键后框选屏幕文字，NaniKore 会在本地完成 OCR，并可调用已配置的翻译服务显示流式结果。当前 `main` 使用单一 C#/.NET 进程；旧版 Go/Gio 实现保留在 `legacy-go` 分支。

## 功能

- 全局快捷键 `Ctrl+Shift+A`，支持拖选、Esc 或右键取消。
- 基于 GDI 的虚拟桌面截图与 Per-Monitor V2 物理像素坐标处理。
- 通过 ONNX Runtime 直接运行 PP-OCRv6-small，不依赖 Python、OpenCV 或外部 OCR 服务。
- 支持 OpenAI、Claude、Gemini 协议的流式翻译配置，以及 `local_ocr`、`vision_oneshot` 两条输入管线。
- 结果自动复制到剪贴板；设置、热键和 API Key 可在应用内管理。
- API Key 仅使用当前 Windows 用户的 DPAPI 保存，不会写入仓库或 `config.yaml`。

## 系统要求

- Windows 10 version 2004（build 19041）或更高版本。
- 开发：.NET 10 SDK（`global.json` 当前要求 `10.0.302` 或兼容的更高 feature band）。
- 运行发布包：.NET 10 Desktop Runtime；发布包是 framework-dependent，不包含运行时。
- 本地 OCR：PP-OCRv6-small 模型文件。

## 快速开始

```powershell
# 准备 .NET SDK、NuGet 缓存和 OCR 模型
./scripts/bootstrap.ps1

# 构建并运行
./scripts/dev.ps1 build
./scripts/dev.ps1 run
```

运行后按 `Ctrl+Shift+A` 框选文字。选区图像仅在内存中传递，不会生成临时截图文件。

`bootstrap.ps1` 优先使用 `DOTNET_ROOT` 中的 SDK，并使用 `NUGET_PACKAGES` 指定的缓存；两个环境变量都未配置时才回退到 `Path`。模型下载到项目内的 `models/`，但模型权重不会提交到 Git。

## 常用命令

| 命令 | 说明 |
| --- | --- |
| `./scripts/dev.ps1 build` | 构建 Debug 版本。 |
| `./scripts/dev.ps1 test` | 运行无 UI 自测。 |
| `./scripts/dev.ps1 run` | 启动桌面应用。 |
| `./scripts/dev.ps1 publish` | 发布 Windows x64 并生成不含 OCR 模型的 ZIP。 |
| `./scripts/dev.ps1 publish -OcrModels` | 发布 Windows x64，并将 OCR 模型一并打入 ZIP。 |
| `./scripts/dev.ps1 -Launch build` | 启动已有的 Debug EXE，不重复构建。 |
| `./scripts/dev.ps1 -Launch publish` | 启动已有的发布 EXE，不重复发布。 |

`-ocr-models` 是 `-OcrModels` 的等价别名。启用该参数前，请先运行 `./scripts/bootstrap-models.ps1`；模型缺失时，发布会明确失败而不会产出不完整的模型包。

## 发布包

发布文件保存在 `artifacts/`。版本号读取自根目录 `config.yaml` 的 `version` 字段：

- `./scripts/dev.ps1 publish`：`NaniKore-v{版本号}-win_amd64-without-ocr-models.zip`
- `./scripts/dev.ps1 publish -OcrModels`：`NaniKore-v{版本号}-win_amd64.zip`

两种 ZIP 都将发布内容直接置于压缩包根目录。包含模型的包具有以下布局，应用无需额外设置即可查找模型：

```text
NaniKore.exe
onnxruntime.dll
onnxruntime_providers_shared.dll
config.yaml
models/
└─ pp-ocr-v6-small/
```

未包含模型的包适合与模型单独分发；运行前请将 `models/` 放到 EXE 同级目录，或设置 `NANIKORE_MODEL_DIR` 指向完整的 `pp-ocr-v6-small` 目录。

## 配置与翻译服务

根目录 `config.yaml` 是不含密钥、可审阅的默认模板。构建和发布时会复制到各自运行目录，因此应用设置页保存的配置不会回写仓库模板。要修改新实例的默认配置，请编辑根目录模板后重新构建或发布。

翻译服务、模型、目标语言和快捷键均可通过设置页调整。请只在自己信任的服务商端点中填写 API Key。

## OCR 诊断

```powershell
# 查看 ONNX 模型输入输出元数据
dotnet run --project src\NaniKore\NaniKore.csproj -- --inspect-models

# 直接识别图片
dotnet run --project src\NaniKore\NaniKore.csproj -- --ocr-file path\to\image.png
```

## 项目结构

```text
src/NaniKore/  应用、OCR、翻译与桌面 UI
scripts/        工具链、模型准备和开发/发布命令
models/         本地 OCR 模型目录（仅说明文件纳入 Git）
artifacts/      发布产物（Git 忽略）
```

## 已知边界

- 已完成主显示器的真实键鼠截图与 OCR 验收，以及自动化冒烟自测。
- 副显示器、负坐标虚拟桌面与混合 DPI 已有代码支持，但尚未完成实机回归。
- 当前轻量 OCR 后处理面向水平游戏/UI 文本；旋转、弯曲和竖排文字不在保证范围内。

## 许可证

仓库暂未声明开源许可证。在许可证明确前，请勿假定可再分发或用于其他用途。
