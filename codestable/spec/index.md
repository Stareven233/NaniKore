# Project Spec

## 这个项目是什么

NaniKore 是一个轻量的 Windows 截图 OCR 翻译工具。用户按全局快捷键框选屏幕文字，程序在本地完成 OCR；后续阶段再接入云端或本地翻译，并提供按需精讲。

## 当前已实现真相

### 捕获与选区

- 单一 C#/.NET 10 WinForms 进程和 Windows 消息循环。
- `Ctrl+Shift+A` 注册为全局热键。
- 使用 GDI 捕获完整虚拟桌面。
- 进程在创建 HWND 前启用 Per-Monitor V2，内部使用物理像素坐标。
- 左键拖选，Esc/右键取消，重复热键不会重入第二条选区流水线。
- 裁剪位图只在内存中向下游传递，不写临时截图。

### OCR

- C# 通过 `Microsoft.ML.OnnxRuntime` 直接运行 PP-OCRv6-small 检测与识别 ONNX。
- 不依赖 Python、leaf-ocr 或 OpenCV。
- 模型配置、归一化参数和识别字典来自官方 `inference.yml`。
- 检测后处理为纯 C# 连通域与水平矩形近似；识别包含动态宽度和 CTC 解码。
- 模型会话复用，推理在工作线程执行。
- 结果自动复制到剪贴板并显示在结果窗口。

### 翻译、结果与配置（初版，待真机整改）

- 已有 OpenAI（completions/responses）、Claude、Gemini 的流式 provider 契约，支持 `local_ocr` 与 `vision_oneshot` 两条输入路径。
- 提示词为模块化组装（`PromptTemplates`）：设置页对应 `base` / `vision_base` / `explain` 三个 Base 键与 `japanese_plugin` / `english_plugin` / `chinese_plugin` 三个语言插件键，留空即回落到内置常量。Base 与插件支持 `{{target_language}}`；视觉一条龙由视觉 Base + 语言插件恒定同拼，与输入无关，前缀缓存 100% 命中。语言插件经「探测结果纳入 + 目标语言互斥剔除」双重门控：某语言本身就是目标语言时对应插件整条不注入（日译英不标 IPA、外译中不标拼音等）。
- 结果窗口可显示 OCR 原文和流式译文，设置页包含通用、翻译、热键、收藏和关于页；API Key 以当前 Windows 用户的 DPAPI 密文整体保存在数据目录 `程序目录\.data\keys.bin`（内层是 `{配置档名: API Key}` 的 JSON，位置唯一、不可改）。
- 2026-08-09 真机验收发现结果卡层级/编辑、翻译目标可靠性、配置档切换、配置版本、设置生命周期和热键录制等问题，尚不能视为稳定体验。下一轮实施与验收以 `codestable/roadmap/002-csharp-single-process-migration/phase-3-post-test-remediation.md` 为准。
- 学习型演进（Epic 001，已全部切片交付）：原文区支持振假名（Ruby）FlowDocument 只读排版与双击原地纯文本编辑双模（光标定位不全选、未修改失焦复原）；翻译采用两阶段流式协议（阶段 1 假名原句 + `---TRANSLATION---` + 译文，阶段 2 精讲完整回放阶段 1 图文历史以命中前缀缓存）；精讲以微结构化 Markdown 渲染为词汇/语法/语境卡片并逐卡 `[★ 收藏]`，正文与译文划词浮出微型气泡（分类/微调/收藏，失焦自动关闭），大模型格式异常时降级为原始 Markdown 排版。
- 本地知识库（生词本）：收藏持久化到数据目录 `程序目录\.data\collections.db`（SQLite，首次收藏惰性建库，通用 `favorites` 表：`category/content/note/created_at`，note 为纯文本分层标注），设置面板 `[收藏]` Tab 支持类别筛选、关键词搜索、单条复制/删除与复制全部；收藏不写入 `config.yaml`。
- 配置模板化与 name 唯一索引（issue 017）：程序目录根只放交付物，本机数据全部收拢在 `程序目录\.data\`（`config.yaml`、`keys.bin`、`collections.db`）；根目录 `config.template.yaml` 随包发布且程序永不写它，首启按字节原样复制到 `.data/config.yaml`（注释保留），已有时原样不动；每个配置档落盘四行（`name` / `kind` / `base_url` / `model`，其余字段等于缺省即省略），配置档唯一身份是 `name`（活动档、下拉、报错、密钥索引全部用它，旧 `id` 键反序列化即忽略）；模板与配置版本 major/minor 不一致时托盘与「关于」页各给一行提示（仅提示，无迁移机制）。
- 结果卡底部状态栏呈现本次调用的 Token 消耗与首字延迟（TTFT）、生成速度指标，兼容 OpenAI/Claude/Gemini 流式 Usage 与非标端点启发式估算。
### 工具链与发布

- .NET SDK、NuGet 缓存和可选的本地运行时依赖按各自入口准备。
- Windows x64 framework-dependent 发布包含 `NaniKore.exe` 与托管依赖；ONNX Runtime native DLL 和 OCR 权重位于 `deps/`。
- 发布包结构 = `NaniKore.exe` + `config.template.yaml` + （完整包）`deps/`：程序目录根只放交付物，运行期数据全部收拢在 `程序目录\.data\`，发布 zip 不含 `.data`，解压覆盖在物理上碰不到用户数据；`scripts/dev.ps1 publish` 的打包步骤排除 `.data`。
- `scripts/dev.ps1 publish` 生成不含固定 OCR 权重和 native DLL 的 `NaniKore-v{version}-win_amd64.zip`；`publish -full` 从唯一真实源 `deps/` 复制完整依赖，生成 `NaniKore-v{version}-win_amd64-full.zip`。版本号读取自根目录 `config.template.yaml` 的 `version`（`Get-NaniKoreProductVersion` / `scripts/update_version.py` 均以模板为源），dev 构建只递增 build 段。
- 应用设置页的“下载依赖”操作在用户确认后将依赖保存到当前可执行程序目录的 `deps/`；ModelScope `master` 为默认源，OCR 文件失败时逐项回退 Hugging Face。
- 应用不会静默联网，不支持 `NANIKORE_MODEL_DIR`；模型和 DLL 不得维护在其他目录。
- 目标机器需要 .NET 10 Desktop Runtime。
- 提示词开发期工具链：`NaniKore.exe --dump-prompt [--text/--image/--kind/--target/--json]` 是「最终下发什么」的真值源（经 `ConfigStore` 读真实配置，输出 path、detection、带来源标注的 sections、完整 system prompt 与 token 估算）；`scripts/prompt-preview.html` 为单文件可视化预览，由 `scripts/serve-preview.ps1` 托管并代理 `/api/dump` 实时取真值——页面**零内嵌镜像**，不复制任何提示词字面量或探测器区间，改动 `PromptTemplates.cs` / `LanguageDetector.cs` 后刷新即得最新真值，无快照漂移可言；未启动中转服务时页面明确提示降级。

## 已验证边界

- 构建、自测、真实中英混排 OCR、自动化键鼠冒烟和主显示器人工交互均通过。
- 副显示器、负坐标虚拟桌面和混合 DPI 代码路径已实现，但因当前没有副显示器设备，尚未完成实机回归。
- 水平游戏/UI 文本是当前目标；旋转、弯曲和竖排文本不是当前轻量后处理的保证范围。

## 尚未实现

- 可选 GGUF 本地翻译运行时。
- 阶段 3 初版体验的真机整改，见上述整改规格。
- Anki 导出（`.apkg`/TSV）与本地词典分词假名快关（MeCab 等），均记录在 Epic 001 悬挂项。
下一阶段规划见 `codestable/roadmap/002-csharp-single-process-migration/spec.md`。
