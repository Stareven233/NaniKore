# Project Spec

## 这个项目是什么

NaniKore 是一个轻量的 Windows 截图 OCR 翻译工具。用户按全局快捷键框选屏幕文字，程序在本地完成 OCR；后续阶段再接入云端或本地翻译，并提供按需精讲。

## 当前已实现真相

### 捕获与选区

- 单一 C#/.NET 10 Avalonia 12 宿主进程（`ClassicDesktopStyleApplicationLifetime`）。
- `Ctrl+Shift+A` 与 `Ctrl+Shift+S` 分别注册为框选与剪贴板全局热键（基于独立 Win32 消息窗口与 P/Invoke，脱离 WinForms/WPF）。
- 使用 Win32 `BitBlt` 捕获完整虚拟桌面（含多屏负坐标与混合 DPI），直灌 `SKBitmap`，全面移除 GDI+ / `System.Drawing`。
- 选区浮层采用 Avalonia 无边框全屏置顶窗口（`SelectionOverlayWindow`），自绘压暗与尺寸标签，结合纯几何状态机（`SelectionGeometry`）实现物理像素 1:1 对齐。
- 左键拖选，Esc/右键取消，重复热键不会重入第二条选区流水线。
- 裁剪位图以 `SKBitmap` / `SKRectI` 只在内存中向下游传递，不落临时截图。

### OCR

- C# 通过 `Microsoft.ML.OnnxRuntime` 直接运行 PP-OCRv6-small 检测与识别 ONNX。
- 不依赖 Python、leaf-ocr、OpenCV 或 System.Drawing，图像数据流全程基于 `SKBitmap`。
- 模型配置、归一化参数和识别字典来自官方 `inference.yml`。
- 检测后处理为纯 C# 连通域与水平矩形近似；识别包含动态宽度和 CTC 解码。
- 模型会话惰性加载与复用，推理在后台工作线程执行；动态截图推理禁用 ONNX Runtime CPU arena 与 memory pattern，避免缓存大图高水位缓冲。检测和识别后处理直接读取原生输出，完成后立即释放，不复制整块浮点概率矩阵。
- 结果窗口关闭时解除全局注音通知与收藏服务订阅，并取消未完成的注音回写，避免长期事件源保留已关闭窗口。
- 结果自动复制到剪贴板并流式显示在结果窗口。

### 翻译、结果与配置

- 已有 OpenAI（completions/responses）、Claude、Gemini 的流式 provider 契约，支持 `local_ocr` 与 `vision_oneshot` 两条输入路径。
- 提示词为模块化组装（`PromptTemplates`）：设置页对应 `base` / `vision_base` / `explain` 三个 Base 键与 `japanese_plugin` / `english_plugin` / `chinese_plugin` 三个语言插件键，留空即回落到内置常量。Base 与插件支持 `{{target_language}}`；视觉一条龙由视觉 Base + 语言插件恒定同拼，与输入无关，前缀缓存 100% 命中。语言插件经「探测结果纳入 + 目标语言互斥剔除」双重门控：某语言本身就是目标语言时对应插件整条不注入（日译英不标 IPA、外译中不标拼音等）。
- 结果窗口采用 Avalonia 原生实现，`ShowActivated=false` 与 Win32 `SetWindowPos(SWP_NOACTIVATE)` 严格保护前台焦点不抢夺；设置页包含通用、翻译、热键、收藏和关于页。
- 原文区基于 Avalonia `SelectableTextBlock` + `Inlines` + `InlineUIContainer` 承载振假名与双模就地编辑（光标定位不全选、未修改失焦复原）；翻译采用两阶段流式协议（阶段 1 假名原句 + `---TRANSLATION---` + 译文，阶段 2 精讲完整回放阶段 1 图文历史以命中前缀缓存）；精讲微结构化卡片与划词微型气泡完全由 Avalonia 承载。
- 本地知识库（生词本）：收藏持久化到数据目录 `程序目录\.data\collections.db`（SQLite，由随包旁置的 `e_sqlite3.dll` 驱动，首次收藏惰性建库），设置面板 `[收藏]` Tab 支持类别筛选、关键词搜索、单条复制/删除与复制全部；收藏不写入 `config.yaml`。
- 配置与密钥管理：`config.yaml` 基于 YamlDotNet 18.1.0 静态上下文（`ConfigYamlStaticContext`，0 反射）读写；API Key 保存在 `程序目录\.data\keys.bin`，带方案版本头（NKKEY + Scheme 1 + Version 1），向后透明兼容旧 DPAPI 密文；全部 JSON 序列化（SecretStore、OcrHttpServer、LLM 请求体 JsonNode 结构化组装）均通过 AOT 安全静态化。
- 结果卡底部状态栏呈现本次调用的 Token 消耗与首字延迟（TTFT）、TPS指标。

### UI 引擎与平台边界

- UI 引擎：统一为 Avalonia 12.1.4，以 FluentTheme 为基底配合草地绿品牌色语义覆写与 Win11 Fluent 2 微圆角（Route A 路线），彻底消解旧 WPF 模板维护负担。
- 平台能力接缝（`Platform.Windows`）：
  - 屏幕捕获：`IScreenCapture`（Win32 `BitBlt` → `SKBitmap`）
  - 全局热键：`IGlobalHotkey`（独立 Win32 消息窗口，STA 线程 P/Invoke）
  - 剪贴板网关：`IClipboardGateway`（Win32 P/Invoke 与 Avalonia `TopLevel.Clipboard` 异步通道）
  - 托盘：`ITrayHost`（Avalonia `TrayIcon` 原生系统菜单 + 自绘无焦点通知小窗）
  - 密钥存储：`ISecretStore`（Windows DPAPI，预留跨平台版本头）
- 平台边界已彻底切开，业务核心（翻译、注音、OCR 算法、配置、收藏）均为纯托管平台无关代码。
- 字体族为运行期配置：`config.yaml` 的 `fonts.ui` / `fonts.prompt` 经 `FontResources` 在启动与保存设置时注入应用资源（`Font.Ui` / `Font.Prompt`），代码内不保留任何字体族常量；键留空等价于 Avalonia 平台默认字体（不回落内置值），可填英文逗号分隔的回退链。随包模板默认 `ui: Microsoft YaHei UI`、`prompt: 'YaHei Monaco Hybird,Consolas'`，链尾均为所有 Windows 语言版自带的字体，首项未命中时仍有确定兜底。设置页「通用 → 外观」用内置 `AutoCompleteBox`（输入框即搜索框，候选来自本机已安装字体）直接手填或下拉点选，输入框以当前字体自渲染作预览，无独立弹窗、无新窗口；未安装的族名不阻止保存，只点名告警——保存后由 Avalonia 平台字体回退渲染，外观可能仍是其它可用字体。

- 设置面板与结果面板共用可选本地背景图，在「通用 → 外观」填写路径、模糊半径（0–100）与图片不透明度（0–100%）；对应 `windows.background_image` / `background_blur` / `background_opacity`（0–1）。路径支持绝对路径和相对可执行程序目录的路径，也可点击路径旁的「浏览…」打开系统图片文件选择框，选中后填入本地绝对路径，取消时保留原输入，统一保存后生效。图片按窗口宽高直接拉伸填满，完整显示且不裁切（图片比例与窗口不一致时会变形）；模糊与图片不透明度仅作用于图片层。新增「面板底色不透明度」（0–100%，`windows.panel_background_opacity` 为 0–1，默认 1），两个窗口的侧栏、上下栏和内容卡片/输入底色共用，仅在图片成功加载时生效，文字、图标和边框不随之变淡，深浅主题保留原有颜色。默认路径为空，保持原外观；清空路径并保存立即恢复。保存前校验文件可解码，启动时图片不可读则停用图片层并保留路径；两个窗口共用解码位图，保存后立即刷新。

### 工具链与发布

- .NET 10 SDK、Native AOT（ILC 编译器）与 MSBuild。
- 交付形态：**免安装便携目录 = 精简 Native AOT exe + 随包原生 DLL 旁置**，目标机器零 .NET 运行时依赖，解压即用。
- 交付物目录契约：
  - 根目录恰好 7 项：精简可执行文件 `NaniKore.exe` + 4 个随包原生 DLL（`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll`、`e_sqlite3.dll`）+ 只读配置模板 `config.template.yaml` + 许可声明 `NOTICE`。
  - 仅有两个子目录：按需下载的 `deps/`（本地 OCR 与三语注音词典）与运行期生成的 `.data/`（用户配置与数据库）。
  - 运行期数据全部收拢在 `.data\`，发布 ZIP 绝不包含 `.data/`。
- 发布通道：
  - `scripts/dev.ps1 publish`：使用 `-p:PublishAot=true -r win-x64` 构建，自动摘除根目录 `onnxruntime.dll`（防止遮蔽 `deps/`）并清理 `.lib` 与 `.pdb`，生成免安装包 `NaniKore-v{version}-win_amd64.zip`（未压缩交付物 ~40.69 MB，ZIP ~18.66 MB，exe ~20.83 MB）。
  - `scripts/dev.ps1 publish -Full`：从真实源 `deps/` 复制完整依赖，生成解压即可离线使用本地 OCR 的 `NaniKore-v{version}-win_amd64-full.zip`（ZIP ~67.24 MB）。
  - 版本号读取自根目录 `config.template.yaml` 的 `version`；dev 构建只递增 build 段。
- 提示词开发期工具链：`NaniKore.exe --dump-prompt [--text/--image/--kind/--target/--json]` 是「最终下发什么」的真值源；`scripts/prompt-preview.html` 托管预览。

## 已验证边界

- 构建、自测、真实中英混排 OCR、自动化键鼠冒烟和主显示器人工交互均通过。
- 副显示器、负坐标虚拟桌面和混合 DPI 代码路径已实现，且全量自测在 Native AOT 干净环境下通过。
- 水平游戏/UI 文本是当前目标；旋转、弯曲和竖排文本不是当前轻量后处理的保证范围。

## 尚未实现

- 可选 GGUF 本地翻译运行时。
- Anki 导出（`.apkg`/TSV）与本地词典分词假名快关（MeCab 等），均记录在 Epic 001 悬挂项。
- 跨平台（Linux/macOS）平台层接口具体实现（另开事项）。
