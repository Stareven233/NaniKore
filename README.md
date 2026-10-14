# NaniKore

> 轻量的 Windows 截图 OCR 与翻译工具。

按下全局快捷键后框选屏幕文字，NaniKore 会在本地完成 OCR，并可调用已配置的翻译服务显示流式结果。当前 `main` 使用单一 C#/.NET 进程；旧版 Go/Gio 实现保留在 `legacy-go` 分支。

## 功能

- 全局快捷键 `Ctrl+Shift+A`，支持拖选、Esc 或右键取消。
- 第二把全局快捷键 `Ctrl+Shift+S`（选取/剪贴板翻译）：模拟 Ctrl+C 抓选区文本，取不到时依次退回剪贴板文本、剪贴板图像，复用截图流程的同一套翻译服务、提示词与结果面板，面板锚在鼠标所在显示器。
- 热键可留空即停用；截图与选取两把热键各自独立注册、录制与冲突提示（录制时支持 F1–F12 单键，Esc 可解除绑定）。
- 基于 GDI 的虚拟桌面截图与 Per-Monitor V2 物理像素坐标处理。
- 通过 ONNX Runtime 直接运行 PP-OCRv6-small，不依赖 Python、OpenCV 或外部 OCR 服务。
- 支持 OpenAI、Claude、Gemini 协议的流式翻译配置，以及 `local_ocr`、`vision_oneshot` 两条输入管线。
- 结果自动复制到剪贴板；设置、热键和 API Key 可在应用内管理。
- 结果卡精讲与正文划词均可一键收藏进本地生词本：SQLite 持久化（`.data/collections.db`），支持类别/语言/关键词筛选与就地编辑。
- API Key 仅使用当前 Windows 用户的 DPAPI 保存，不会写入仓库、配置模板或 `.data/config.yaml`。
- 程序目录根只放交付物（exe、`deps/`、`config.template.yaml`）；配置、密钥与收藏库全部收拢在 `程序目录\.data\`，解压覆盖升级碰不到它们。

## 系统要求

- Windows 10 version 2004（build 19041）或更高版本。
- 开发：.NET 10 SDK（`global.json` 当前要求 `10.0.302` 或兼容的更高 feature band）。
- 运行发布包：.NET 10 Desktop Runtime；发布包是 framework-dependent，不包含运行时。
- 本地 OCR 依赖：PP-OCRv6-small 模型和 ONNX Runtime native DLL。

## 快速开始

```powershell
# 准备 .NET SDK 与 NuGet 缓存
./scripts/bootstrap.ps1

# 构建并运行
./scripts/dev.ps1 build
./scripts/dev.ps1 run
```

首次使用本地 OCR 时，打开设置页，在“通用”中点击“下载依赖”，确认后应用会从 ModelScope 下载所需模型和 DLL 到当前可执行程序目录的 `deps/`。应用不会静默联网；OCR 缺少依赖时只会提示执行这个设置操作。

`bootstrap.ps1` 优先使用 `DOTNET_ROOT` 中的 SDK，并使用 `NUGET_PACKAGES` 指定的缓存；两个环境变量都未配置时才回退到 `Path`。运行时依赖的唯一真实源是当前可执行程序目录下的 `deps/`。

## 常用命令

| 命令 | 说明 |
| --- | --- |
| `./scripts/dev.ps1 build` | 构建 Debug 版本。 |
| `./scripts/dev.ps1 test` | 运行无 UI 自测。 |
| `./scripts/dev.ps1 run` | 启动桌面应用。 |
| `./scripts/dev.ps1 publish` | 发布不含固定 OCR 权重和 native DLL 的 Windows x64 轻量 ZIP。 |
| `./scripts/dev.ps1 publish -full` | 发布包含完整 `deps/` 的 Windows x64 ZIP，解压后可直接进行本地 OCR。 |
| `./scripts/dev.ps1 -Launch build` | 启动已有的 Debug EXE，不重复构建。 |
| `./scripts/dev.ps1 -Launch publish` | 启动已有的发布 EXE，不重复发布。 |

## 发布包

发布文件保存在 `artifacts/`。版本号读取自根目录 `config.template.yaml` 的 `version` 字段：

- `./scripts/dev.ps1 publish`：`NaniKore-v{版本号}-win_amd64.zip`
- `./scripts/dev.ps1 publish -full`：`NaniKore-v{版本号}-win_amd64-full.zip`

两种 ZIP 都将发布内容直接置于压缩包根目录。完整包额外携带与仓库一致的 `deps/`：

```text
NaniKore.exe
config.template.yaml
deps/
├─ ocr-onnx/pp-ocr-v6-small/
│  ├─ det/inference.onnx
│  ├─ det/inference.yml
│  ├─ rec/inference.onnx
│  └─ rec/inference.yml
└─ onnxruntime/
   ├─ onnxruntime.dll
   └─ onnxruntime_providers_shared.dll
```

发布包不含 `.data/`：首次启动时程序把 `config.template.yaml` 按字节原样复制到 `程序目录\.data\config.yaml` 作为用户配置，密钥落在 `.data/keys.bin`（整块 DPAPI 密文），收藏库是 `.data/collections.db`。因此解压覆盖升级不会冲掉任何本机数据；备份整个软件只需复制 `.data/`。

轻量包需要用户在设置页确认下载依赖后才能进行本地 OCR；完整包解压后无需额外下载。

## 配置与翻译服务

根目录 `config.template.yaml` 是不含密钥、可审阅的配置模板，随包发布且程序永不写它。首次启动时它被字节级复制到 `程序目录\.data\config.yaml`（模板注释原样保留），之后只读写 `.data` 里的副本——已有配置时模板怎么改都不影响运行。

每个配置档只需 `name` / `kind` / `base_url` / `model` 四行；`api_type` / `supports_vision` / `timeout_seconds` / `enable_thinking` 只在偏离缺省（`completions` / `true` / `30` 秒 / `false`）时才需要写。配置档的唯一身份是 `name`：`pipeline.active_profile`、翻译页下拉、错误提示与密钥索引全部用它。

翻译服务、模型、目标语言和快捷键均可通过设置页调整。请只在自己信任的服务商端点中填写 API Key。翻译页「凭据」组可以一键打开 `.data` 目录、清除当前配置档的密钥。

「热键」页分别录制两把全局快捷键：`hotkeys.capture`（截图 OCR，默认 `Ctrl+Shift+A`）与 `hotkeys.paste`（选取/剪贴板翻译，默认 `Ctrl+Shift+S`）。任一把留空即不注册该热键；录制框按 Esc 可解除绑定。选取翻译的输入优先级是「选区文本（模拟 Ctrl+C）→ 剪贴板文本 → 剪贴板图像」：图像按当前配置档走视觉直喂，`vision_oneshot` 管线或缺少多模态能力时改用本地 PP-OCRv6 识别。选取翻译默认在读到内容后立即翻译，可在 设置-通用 关闭「选区/剪贴板后自动翻译」改为只呈现原文（含注音）、由面板「翻译」按钮手动触发；视觉直喂不受该开关约束，仍一次完成识别与翻译。程序只在「前台程序响应了 Ctrl+C」与「本地 OCR 识别成功」两种情况下改写剪贴板，其余情况（含前台程序按 Ctrl+C 写空串）都会在轮询超时后把快照原样还原；还原失败时结果面板状态栏会给出说明。

「关于」页版本号形如 `v{A}({B})`：A 是模板版本（最新），B 是当前配置版本；两者 major/minor 不一致时启动后托盘会提示一次，关于页给一行常驻说明（仅提示，没有自动动作）。

## 收藏与生词本

收藏保存在数据目录 `程序目录\.data\collections.db`（SQLite 表 `favorites`，列为 `category`、`content`、`note`、`created_at`、`language`；绿色便携、覆盖升级不丢数据，删除该文件即回到空态。设置页「收藏」可按类别、语言、关键词组合筛选，支持复制全部、单条删除与失焦即保存的就地编辑。

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
deps/           唯一的本地 OCR 与 ONNX Runtime 依赖源
.data/          本机运行期数据：配置、密钥密文、收藏库（Git 忽略）
artifacts/      发布产物（Git 忽略）
```

## 已知边界

- 已完成主显示器的真实键鼠截图与 OCR 验收，以及自动化冒烟自测。
- 副显示器、负坐标虚拟桌面与混合 DPI 已有代码支持，但尚未完成实机回归。
- 当前轻量 OCR 后处理面向水平游戏/UI 文本；旋转、弯曲和竖排文字不在保证范围内。

## 许可证

仓库暂未声明开源许可证。在许可证明确前，请勿假定可再分发或用于其他用途。
