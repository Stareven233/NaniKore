# NaniKore

> 轻量的 Windows 截图 OCR 与翻译工具。

按下全局快捷键后框选屏幕文字，NaniKore 会在本地完成 OCR，并可调用已配置的翻译服务显示流式结果。基于 .NET 11 / C# 15 与 Avalonia 12 UI 框架，采用 Native AOT 编译为精简原生机器码

## 功能

- 全局快捷键 `Ctrl+Shift+A`，支持拖选、Esc 或右键取消。
- 第二把全局快捷键 `Ctrl+Shift+S`（选取/剪贴板翻译）：模拟 Ctrl+C 抓选区文本，取不到时依次退回剪贴板文本、剪贴板图像，复用截图流程的同一套翻译服务、提示词与结果面板，面板锚在鼠标所在显示器。
- 热键可留空即停用；截图与选取两把热键各自独立注册、录制与冲突提示（支持修饰键加标点、导航键、小键盘、多媒体键等；F1–F11 可单独绑定，F12 为系统保留键；裸 Esc 取消录制或解除选取热键绑定）。
- 基于 Win32 BitBlt 与 SkiaSharp 物理像素直接处理全虚拟桌面截图（支持多屏负坐标与混合 DPI），选区浮层基于 Avalonia 自绘。
- 通过 ONNX Runtime 直接运行 PP-OCRv6-small，不依赖 Python、OpenCV 或外部 OCR 服务。
- 支持 OpenAI、Claude、Gemini 协议的流式翻译配置，以及 `local_ocr`、`vision_oneshot` 两条输入管线。
- 结果自动复制到剪贴板；设置、热键和 API Key 可在应用内管理。
- 结果卡精讲与正文划词均可一键收藏进本地生词本：SQLite 持久化（`.data/collections.db`），支持类别/语言/关键词筛选与就地编辑。
- 原文区本地注音：日语标振假名、英语标国际音标、简体中文标拼音，由本地引擎叠加（词典按语言按需下载）；面板「注音」按钮可随时对当前原文重新标注并查看读音，无需先点「翻译」。短原文（可注音单词不足 10 个）视为短语，全部单词都注音，不再按难词过滤。
- API Key 仅使用当前 Windows 用户的 DPAPI 保存，不会写入仓库、配置模板或 `.data/config.yaml`。
- 程序目录根只放交付物（精简 exe、随包原生 DLL、`config.template.yaml`、`NOTICE`）；配置、密钥与收藏库全部收拢在 `程序目录\.data\`，解压覆盖升级碰不到它们。

## 系统要求

- Windows 10 version 2004（build 19041）或更高版本（x64）。
- **运行发布包**：**解压即用，目标机器零 .NET 运行时依赖**（Native AOT 原生机器码编译）。
- **源码开发**：.NET 11 SDK（`global.json` 当前要求 `11.0.100` 或同一 11.0 通道兼容的更高 feature band），默认使用正式版 C# 15。
- 本地 OCR 依赖：PP-OCRv6-small 模型和 ONNX Runtime native DLL（设置页一键按需下载至 `deps/`，或直接使用 `-full` 完整离线包）。

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
| `./scripts/dev.ps1 build` | 快速构建 Debug 版本（递增配置版本号，供断点调试）。 |
| `./scripts/dev.ps1 build -Release` | 编译 Native AOT Release 版本（生成至 `artifacts/win-x64`，不打包）。 |
| `./scripts/dev.ps1 run` | 启动本地 Debug 桌面应用（后台脱手运行，不占用终端）。 |
| `./scripts/dev.ps1 run -Release` | 启动本地已构建的 Native AOT Release 桌面应用。 |
| `./scripts/dev.ps1 test` | 运行无 UI 全量自动化自测。 |
| `./scripts/dev.ps1 publish` | 编译 Native AOT 并发布免安装轻量 ZIP（~18 MB，根目录 7 项，解压即用）。 |
| `./scripts/dev.ps1 publish -full` | 编译 Native AOT 并发布包含完整 `deps/` 离线模型与词典的 Windows x64 ZIP（~67 MB）。 |

## 发布包

发布文件保存在 `artifacts/`。版本号读取自根目录 `config.template.yaml` 的 `version` 字段：

- `./scripts/dev.ps1 publish`：`NaniKore-v{版本号}-win_amd64.zip`
- `./scripts/dev.ps1 publish -full`：`NaniKore-v{版本号}-win_amd64-full.zip`

两种 ZIP 都将发布内容直接置于压缩包根目录。根目录严格遵守 7 文件契约，完整包额外携带与仓库一致的 `deps/`：

```text
NaniKore.exe                 # Native AOT 精简可执行文件（~20.8 MB，零 .NET 运行时依赖）
libSkiaSharp.dll             # 随包原生图形渲染底座
libHarfBuzzSharp.dll         # 随包原生文字排版与整形底座
av_libglesv2.dll             # 随包原生 OpenGL ES 渲染底座
e_sqlite3.dll                # 随包原生 SQLite 嵌入式数据库底座
config.template.yaml         # 只读配置模板（首启按字节复制到 .data/config.yaml）
NOTICE                       # 第三方组件与数据许可声明
deps/                        # （仅 -full 完整包携带，或设置页按需下载）
├─ ocr-onnx/pp-ocr-v6-small/
│  ├─ det/inference.onnx
│  ├─ det/inference.yml
│  ├─ rec/inference.onnx
│  └─ rec/inference.yml
├─ onnxruntime/
│  ├─ onnxruntime.dll
│  └─ onnxruntime_providers_shared.dll
└─ phonetics/                # 中/日/英三语形态素与注音词典
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

# 直接识别图片（加 --json 输出与 HTTP 接口一致的 JSON）
dotnet run --project src\NaniKore\NaniKore.csproj -- --ocr-file path\to\image.png [--json]
```

## 本地 OCR HTTP 服务（开发者选项）

本地 OCR 引擎可以通过一个极简 HTTP 接口开放给同机其他程序复用，默认关闭。在 `.data/config.yaml` 填 `general.ocr_listen`（如 `127.0.0.1:23335`）即可开启，改完保存设置立即生效，清空即停止。

```http
GET  /health          → {"status":"ok"}
POST /ocr             → {"text":"...","elapsed_ms":42.7,"lines":[{...}]}
```

`POST /ocr` 接受原始图片二进制，或 `?path=<本机路径>`，或 JSON body `{"path":"..."}`。接口只做识别，不翻译、不鉴权、不写任何数据。完整协议、错误码、各语言调用示例与安全提醒见 **[codestable/notes/ocr-http-service.md](codestable/notes/ocr-http-service.md)**。

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

## 许可证与第三方声明

仓库自身代码暂未声明开源许可证。所引用的 Avalonia、SkiaSharp、LibNMeCab、SQLite、ONNX Runtime 等第三方库与词典许可声明请参阅 [NOTICE](NOTICE)。在许可证明确前，请勿假定可再分发或用于其他商业用途。
