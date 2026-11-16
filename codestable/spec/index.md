# Project Spec

## 这个项目是什么

NaniKore 是一个轻量的 Windows 截图 OCR 翻译工具。用户按全局快捷键框选屏幕文字，程序在本地完成 OCR；后续阶段再接入云端或本地翻译，并提供按需精讲。

## 当前已实现真相

### 捕获与选区

- 单一 C# 15/.NET 11 Avalonia 12 宿主进程（`ClassicDesktopStyleApplicationLifetime`）。
- `Ctrl+Shift+A` 与 `Ctrl+Shift+S` 分别注册为框选与剪贴板全局热键（基于独立 Win32 消息窗口与 P/Invoke，脱离 WinForms/WPF）。
- 热键的普通键支持 ASCII 字母数字、功能键、OEM 标点、方向/导航键、空格/Tab/Enter、Backspace/Delete/Escape、锁定键、小键盘、浏览器及多媒体键；除 F1–F11 可裸键绑定外，其余均要求 Ctrl/Shift/Alt/Win 修饰键，F12 因 Windows 调试器保留而拒绝。标点写入稳定 OEM 键名，小键盘与主键盘数字/运算键独立。裸 Esc 在录制时取消截图热键修改或解绑选取热键，带修饰键的 Esc 可正常录制。
- 任一热键录制时暂停两个全局槽位，录制全部结束后按已提交配置分别恢复；保存前拒绝两个槽位使用相同组合（按解析后的键码比较）。变更多个槽位时先统一释放再注册，支持交换与迁移；注册失败独立提示，不回退旧绑定。宿主拒绝重复注册 ID，并在停止接收请求后完成或终结待处理请求，避免并发关闭永久等待。
- 设置保存提交独立配置快照，连续保存不会提前修改宿主持有的配置；关闭时的尺寸记忆始终叠加到最新已提交配置，保留外部更新并丢弃未提交或被拒绝的编辑。
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
- 「设置 → 通用 → 本地资源」提供闲置回收时长，对应 `general.local_resource_idle_timeout`，默认字符串 `0`（永不回收），支持非负整数秒或整数加 `s/m/h`（不区分大小写），预设 `0 / 30s / 10m / 24h`。OCR 会话与各语言注音词典各自从最后一次使用结束后计时，超时释放、下次惰性重建；保存策略后按已有空闲时间重新判断。截图、剪贴板和 HTTP OCR 共用受保护的入口，资源使用中不会被回收；词典首次加载和超时后重载均在注音后台任务执行。只释放资源实例，不主动卸载原生 DLL，不强制 GC 或裁剪工作集。
- 结果窗口关闭时解除全局注音通知与收藏服务订阅，并取消未完成的注音回写，避免长期事件源保留已关闭窗口；同时取消翻译/精讲并清空本次截图与会话。接纳新输入时先使旧请求失效，即使本次未自动翻译也不能让旧回复写入新内容。
- 纯视觉请求在网络序列化前释放截图原始像素；Provider 直接写 UTF-8 JSON，不生成完整 UTF-16 JSON 副本，上传完成或失败后解除请求体引用，不将上传缓冲保留到流式回复结束。面板打开期间保留原图与历史，供重译和精讲使用。
- 结果自动复制到剪贴板并流式显示在结果窗口。

### 翻译、结果与配置

- 已有 OpenAI（completions/responses）、Claude、Gemini 的流式 provider 契约，支持 `local_ocr` 与 `vision_oneshot` 两条输入路径。
- 翻译请求传输边界：生产 HttpClient 关闭自动重定向，仅自动跟随同源（协议 + 主机 + 端口一致）重定向并保留密钥头与 POST 正文，303 转 GET 丢正文，跨源或 https→http 降级抛出 `ProviderTransportPolicyException` 中文错误；远程端点强制 HTTPS，仅 localhost/127.0.0.1/::1 允许明文 HTTP；连接测试与真实翻译共用同一传输入口，策略一致。
- 流式超时与热应用：`timeout_seconds`（默认 30）为空闲超时而非总时长——连接与等待响应头不超过该时长，响应头到达后正文读取若任意两次数据之间连续该时长无新字节即抛 `ProviderTimeoutException`，持续有输出不会被累计时长截断；设置热应用/关闭改过尺寸的设置窗口重建 `ProviderFactory` 时按流租约延迟释放旧 provider，不中断进行中的请求，新请求改用新工厂。
- 提示词为模块化组装（`PromptTemplates`）：设置页对应 `base` / `vision_base` / `explain` 三个 Base 键与 `japanese_plugin` / `english_plugin` / `chinese_plugin` 三个语言插件键，留空即回落到内置常量。Base 与插件支持 `{{target_language}}`；视觉一条龙由视觉 Base + 语言插件恒定同拼，与输入无关，前缀缓存 100% 命中。语言插件经「探测结果纳入 + 目标语言互斥剔除」双重门控：某语言本身就是目标语言时对应插件整条不注入（日译英不标 IPA、外译中不标拼音等）。
- 结果窗口采用 Avalonia 原生实现，`ShowActivated=false` 与 Win32 `SetWindowPos(SWP_NOACTIVATE)` 严格保护前台焦点不抢夺；设置页包含通用、翻译、热键、收藏和关于页。
- 原文区基于 Avalonia `SelectableTextBlock` + `Inlines` + `InlineUIContainer` 承载振假名与双模就地编辑（光标定位不全选、未修改失焦复原）；翻译采用两阶段流式协议（阶段 1 假名原句 + `---TRANSLATION---` + 译文，阶段 2 精讲完整回放阶段 1 图文历史以命中前缀缓存）；精讲微结构化卡片与划词微型气泡完全由 Avalonia 承载。
- 本地知识库（生词本）：收藏持久化到数据目录 `程序目录\.data\collections.db`（SQLite，由随包旁置的 `e_sqlite3.dll` 驱动，首次收藏惰性建库），设置面板 `[收藏]` Tab 支持类别筛选、关键词搜索、单条复制/删除与复制全部；收藏不写入 `config.yaml`。收藏库不可用（只读、被独占、文件损坏）时卡片星标、划词气泡与收藏页统一给出中文原因与恢复建议并保持可用：写失败不改星标、不报成功，列表读取失败保留上一次快照，条件恢复后同一入口可直接重试。
- 配置与密钥管理：`config.yaml` 基于 YamlDotNet 18.1.0 静态上下文（`ConfigYamlStaticContext`，0 反射）读写；API Key 保存在 `程序目录\.data\keys.bin`，带方案版本头（NKKEY + Scheme 1 + Version 1），向后透明兼容旧 DPAPI 密文；全部 JSON 序列化（SecretStore、OcrHttpServer、LLM 请求体 JsonNode 结构化组装）均通过 AOT 安全静态化。读取失败不视为首次使用：`config.yaml` 或 `keys.bin` 格式错误、无权限、被占用或无法解密时，内存虽退回默认仅供启动，但保存会被拒绝且原文件/整份旧密文保持不动，同时给出点名出错配置行的中文说明，需手动修正或删除后重启（`ConfigWriteRefusedException` / `SecretStoreWriteRefusedException`）。DPAPI 绑定当前 Windows 用户，换用户/机器后密钥无法解密、需在目标机重填，不承诺跨机恢复。
- 注音设置分为「通用 → 允许本地注音」（`phonetics.enabled` 总开关，关闭则自动/手动均不加载本地词典）和「翻译 → 识别后自动注音」（`general.auto_annotate`，关闭后只允许面板手动触发）。自动策略同时覆盖 OCR、视觉原文到达、翻译完成与编辑后的自动调用，不因翻译完成而绕过关闭设置。
- 原生注音语言包骨架（issue 009）：`src/NaniKore/Phonetics/Native/` 提供 ABI v1 客户端（`nk_phonetics_*`）、`deps/phonetics/misaki/<lang>/` 包身份清单（库 SHA-256 即版本，与 `pack.json` 及许可哈希双向核对）、PE win-x64 与四个导出/ABI 比对、同 DLL 释放；状态机区分缺失/需更新/下载中/失败/就绪/重启生效，新版本先落 `.staging-<lang>` 校验后替换，已加载语言只暂存、下次启动提升；原生词表挂在现有闲置策略上（只解除引用，不卸载 DLL、不强制 GC），失败一律退化为纯文本并在设置页写明原因。
- **日语 provider 已切换（issue 010）与英语 provider 已切换（issue 011）**：`TextAnnotatorRegistry.CreateDefault()` 的日语槽位取自 `NativePhoneticPackHost` 的原生包，书面假名来自同版 UniDic（東京→とうきょう），宿主把词首词尾送假名切回底字之外（食べた→食[た]べた、買い出し→買い出[かいだ]し），底字只允许汉字与内部假名；数字、拉丁、纯假名与读音为空的词元不注音。英语槽位同一套宿主，直接采用 Misaki 本句选音，转换为可读美音 IPA（允许 `ɾ/ʔ`，`water[ˈwɔːɾ.ər]`、`better[ˈbeɾ.ər]`、`city[ˈsɪɾ.iː]`），候选以 `AnnotationSpan.WholeWord` 整词形状传给界面：hover 列出本句读音与词典已收录读音，居中句重音伪多读已折叠，record 这类词义重音差别保留；read 现在时误选与 lead 金属义缺项为已知限制。旧 IPAdic 标注器（LibNMeCab）与旧 CMUdict 标注器（`EnglishIpaAnnotator`）均已下线，包引用、旧词典卡片、旧 `ipa_dict.db` 与 NOTICE 条目随旧 provider 在 014 清理。结果面板、复制、框选与收藏已按新区间运行。**中文拼音数据与日英一致按需下载（issue 012）**：`ChinesePinyinEngine` 从 `deps/phonetics/zh/zh_pinyin.txt.gz` 读盘（读完即关，哈希即版本 `2B5C6867…A981`），缺文件/版本不符/解压失败时纯文本降级、不回退内嵌或旧 MeCab；设置页中文与日英同为一套下载/更新/就绪卡片；选音、繁简查询、逐字候选、默认首项、UTF-16 区间、过滤、hover、复制与收藏全部保留，闲置释放后下一次注音重新读盘重建。
- 结果卡底部状态栏呈现本次调用的 Token 消耗与首字延迟（TTFT）、TPS指标。

### UI 引擎与平台边界

- UI 引擎：统一为 Avalonia 12.1.6，以 FluentTheme 为基底配合草地绿品牌色语义覆写与 Win11 Fluent 2 微圆角（Route A 路线），彻底消解旧 WPF 模板维护负担。
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

- .NET 11 SDK（`global.json` 最低 `11.0.100`，同一 11.0 通道 `latestFeature`）、正式版 C# 15、Native AOT（ILC 编译器）与 MSBuild；不启用预览语言或内存安全规则。
- 交付形态：**免安装便携目录 = 精简 Native AOT exe + 随包原生 DLL 旁置**，目标机器零 .NET 运行时依赖，解压即用。
- 授权与分发口径（issue 047 R06）：仓库自身代码以 GPL-3.0-or-later 授权（根 `LICENSE`）；第三方组件与数据许可见根 `NOTICE`。LibNMeCab（SPDX `GPL-2.0-or-later OR LGPL-2.1-or-later`）在 AOT 下静态链接进 exe、不存在可单独替换的 DLL，按其 GPL-2.0-or-later 条款按 GPL-3.0 选用，随公开仓库提供对应源码；其余托管/原生依赖均为 MIT/Apache/BSD/公共领域。IPAdic 的 NAIST 许可要求版权与条款随每一份拷贝附带：完整包经 `deps/phonetics/ja/IPAdic-COPYING.txt` 随行，按需下载路径经随包 `NOTICE` 第 3.1 节内联全文随行；zh 拼音数据由 pypinyin（MIT）+ CC-CEDICT（CC BY-SA 4.0）+ Unihan（Unicode License v3）合并派生，与日英一样放在 `deps/phonetics/` 按需下载，归属与派生说明见 NOTICE 第 3.3 节。OCR 权重为 PaddlePaddle PP-OCRv6-small（Apache-2.0）原样再分发，ONNX Runtime 原生 DLL 为 MIT。
- 交付物目录契约：
  - 根目录恰好 8 项：精简可执行文件 `NaniKore.exe` + 4 个随包原生 DLL（`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll`、`e_sqlite3.dll`）+ 只读配置模板 `config.template.yaml` + 许可声明 `NOTICE` + 仓库许可证全文 `LICENSE`。
  - 仅有两个子目录：按需下载的 `deps/`（本地 OCR 权重与中/日/英三语注音数据）与运行期生成的 `.data/`（用户配置与数据库）。
  - 运行期数据全部收拢在 `.data\`，发布 ZIP 绝不包含 `.data/`。
- 发布通道：
  - `scripts/dev.ps1 publish`：使用 `-p:PublishAot=true -r win-x64` 构建，自动摘除根目录 `onnxruntime.dll`（防止遮蔽 `deps/`）并清理 `.lib` 与 `.pdb`，生成免安装包 `NaniKore-v{version}-win_amd64.zip`（未压缩交付物 ~40.69 MB，ZIP ~18.66 MB，exe ~20.83 MB）。
  - `scripts/dev.ps1 publish -Full`：从真实源 `deps/` 复制完整依赖，生成解压即可离线使用本地 OCR 的 `NaniKore-v{version}-win_amd64-full.zip`（ZIP ~67.24 MB）。
  - 版本号读取自根目录 `config.template.yaml` 的 `version`；dev 构建只递增 build 段。
- 提示词开发期工具链：`NaniKore.exe --dump-prompt [--text/--image/--kind/--target/--json]` 是「最终下发什么」的真值源；`scripts/prompt-preview.html` 托管预览。

## 已验证边界

- .NET 11 升级验证：Debug 与 Native AOT 各通过 1641 项自测；AOT 真实中英混排 OCR、桌面启动及截图消息触发/取消选区浮层通过。
- .NET 11 AOT 非 blittable 回调已从产物移除：主题监听使用 Avalonia `IPlatformSettings.ColorValuesChanged`，不再依赖 SystemEvents；`Interop/NativeAot.Substitutions.xml` 仅移除 Avalonia 12.1.6 两个未使用的 DComposition COM 服务端虚表注册，保留 Windows 原生设备的客户端代理与 GPU 合成。更新 Avalonia 或引入托管 DComposition 服务端实现时须复核该裁剪边界。原生代码映射确认四个问题回调均不存在、客户端 `CreateTargetForHwnd` 仍存在；未屏蔽诊断或禁用运行时封送，详见 `issues/046-x-ff-aot-callback-signatures.md`。
- 构建、自测、真实中英混排 OCR、自动化键鼠冒烟和主显示器人工交互均通过。
- 副显示器、负坐标虚拟桌面和混合 DPI 代码路径已实现，且全量自测在 Native AOT 干净环境下通过。
- 水平游戏/UI 文本是当前目标；旋转、弯曲和竖排文本不是当前轻量后处理的保证范围。

## 尚未实现

- 可选 GGUF 本地翻译运行时。
- Anki 导出（`.apkg`/TSV）与本地词典分词假名快关（MeCab 等），均记录在 Epic 001 悬挂项。
- 跨平台（Linux/macOS）平台层接口具体实现（另开事项）。
