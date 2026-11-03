---
kind: epic
title: "Avalonia 迁移与 Native AOT 单文件发布"
status: closed_candidate
created: 2026-10-14
---

# Avalonia 迁移与 Native AOT 单文件发布

> **读者：** 要在这个 Epic 里对齐的人——「相对现状要改什么、方案现在怎么理解、哪些能推、哪些还悬着、关了合回哪里」。
> **自检：** 要改变什么 · 为何现在做 · 来源 Vision / 关联 project spec · 现状与影响三层 · 质量目标 · 当前方案（选型 / 交付形态与目录契约 / 架构归属 / AOT 阻断项 / 组件映射 / 体积预算 / 跨平台边界）· 有界简化 · 穿刺与分批 · 裁决记录与未确认问题 · 关闭条件与合并候选 · 推进视图。
> **状态说明：** 本文件是**设计与计划**，尚未授权实现。文中不写实现代码，只写归属、边界、顺序与验证。

---

> **命名说明（2026-10-25）：** 目录名与标题中的「单文件」是历史命名。主人当日裁定交付形态为「精简 exe + 随包原生 DLL 旁置」，单文件路线（连同 `StaticLink.Avalonia`）已放弃；保留目录名不变是为了既有引用稳定。

## 结论先行

**能做，且现在做比以后做便宜。** 现状是 WPF（界面与富文本）+ WinForms（托盘、剪贴板、选区浮层）双引擎混用，`net10.0-windows10.0.19041.0` 单 TFM，发布物 framework-dependent。Avalonia 12 已有官方 Native AOT 路径，第三方实测单文件约 18 MB（UPX 后 15 MB）；本 Epic 放弃单文件、改旁置原生 DLL 后 exe 预期 12–20 MB、总体积 20–28 MB 量级，**本机真实数字必须实测**，不能当承诺。

**代价集中在两处，都不在业务逻辑：**
1. **约 9 千行 UI 与平台耦合代码要重写**（WPF 控件树 / FlowDocument 振假名 / WinForms 服务）；
2. **约 10 千行业务核心大部分不用动**，但其中四个依赖的 AOT 可行性原本未验证（YamlDotNet、ONNX Runtime、SQLite native、LibNMeCab）——这是唯一可能让整条线走不通的地方；**YamlDotNet、ONNX Runtime、LibNMeCab 与 SQLite native 已分别由穿刺 2 / 3 / 4 / 7 实测打通（均 0 ILC 警告，e_sqlite3.dll 随包旁置自动探测，51 项断言全绿）**。**例外是 OCR 管线**（审计修订）：`OcrEngine` / `TextDetector` / `TextRecognizer` / `TensorImageConverter` 的公开 API 全部接收 `System.Drawing.Bitmap`，而 System.Drawing 在 NativeAOT 下不可用，需系统性 Bitmap→Skia 迁移（约 266 行 + 测试），边界方案见第 3 节归属判断。

**因此第一件事不是开工，是穿刺。** 下面「穿刺计划」列出 8 个风险点与各自的打通判据；任一条不成立，方案要回炉，而不是硬啃。

**交付形态（主人 2026-10-25 修订，取代 10-14「根目录单 exe」裁决）：免安装便携目录 = 精简 exe + 随包原生 DLL 旁置。** `NaniKore.exe` 是 NativeAOT 编译的**精简**可执行体（预期 12–20 MB，P0 实测入档）；Avalonia 的三个原生库（`libSkiaSharp` / `libHarfBuzzSharp` / `av_libglesv2`）与 SQLite 原生（`e_sqlite3.dll`）**不静态链接、不内嵌**，以 DLL 形式旁置根目录——.NET 原生探测自动加载，零解析代码，原生库可与 exe 独立替换。OCR 原生库与注音词典维持按需下载到 `deps/`。**「单文件」路线正式放弃**（连同 `StaticLink.Avalonia` 依赖与它的版本配对风险）。硬要求不变：免安装、解压即用、目标机器零 .NET 运行时依赖；exe 绝对体积是记录值不是硬指标（主人 2026-10-25 明确：「exe 尽量精简、依赖拆 DLL、更新也方便」）。

**引擎选型已定：Avalonia 12；WinUI 3 明确否决。** WinUI 3 在本项目目标上四项全不合：自包含发布必须捆绑 Windows App SDK 运行时（仅此一项约 30 MB 量级，总产物 100 MB 量级）；其「单文件」是首启解压型而非零解压二进制；NativeAOT 自 Windows App SDK 1.6 起可用但仍是进行时（CsWinRT 3.0 未落地）；且 Windows-only，与「平台边界切开、将来跨平台」直接冲突。Avalonia 12 是唯一同时满足「跨平台 + 自带渲染 + XAML 方言近似 + 官方 Native AOT 路径」的选项（静态链接原生库方案曾是加分项，2026-10-25 起本 Epic 不用，见「交付形态」）。对比详见「技术选型」。

**开发分支已定：** 本 Epic 全程在独立分支（建议 `epic/002-avalonia-aot`）开发，`main` 不动；P0 穿刺脚手架同样只在分支内。全部批次通过并经主人确认后才合并回 `main`（见「分支与合并」）。

**待确认问题的裁决记录见文末「裁决」**：2026-10-14 主人已裁决 10 项；2026-10-25 审计新增 7 项待裁决（裁决 11–17：托盘警告通道 / 主题路线 / ILC 警告预算 / 体积硬指标 / SQLite 归宿 / OCR HTTP 服务 scope / 推进节奏），须在 P0 启动前裁定。

**视觉契约不变。** 这不是一次 UI redesign，是**换承载引擎**：`codestable/epics/001-o-学习型演进-精讲假名与生词库/spec.md` 的界面草图与稳定约束继续有效，只在引擎 API 映射不到的地方显式记取舍。

**2026-10-25 审计修订（对 10-14 之后新增代码的对账）：** 10-14 以来主干新增 7 个提交，其中 10-24 的 OCR HTTP 服务（`App/OcrHttpServer.cs` + `General.OcrListen` 配置 + `--ocr-file --json`）与 10-25 的结果面板分区拖拽不在初版视野内，已补入阻断项 11/12、穿刺 7/8 与组件映射；同时修正了三处判断：OCR 核心不是「预期零改动」区（公开 API 全收 `System.Drawing.Bitmap`，NativeAOT 下不可用）、自测计数与体积叙事按现状校准。各处修订以「（审计修订）」标注。

**同日二次修订（主人 2026-10-25 裁定交付形态）：** 放弃「根目录单 exe + 静态链接」，改为「精简 exe + 随包原生 DLL 旁置」——详见上文「交付形态」与第 2 节目录契约；`StaticLink.Avalonia` 从依赖清单移除，SQLite 从「静态链接优先」改为「随包旁置」。主人同时明确：exe 体积是记录值而非硬指标。

---

## 这条线要改变什么

相对 `codestable/spec/index.md` 的「工具链与发布」现状，本 Epic 要做三件事：

1. **收掉双引擎历史包袱**：移除 `UseWPF` / `UseWindowsForms`，统一到 Avalonia 单一 UI 引擎；`GlobalUsings.cs` 里那套 `Wpf*` / `WinForms` 别名（为消解两套命名空间撞名而存在的补丁）随之删除，`System.Drawing` 全面退场。
2. **发布形态从 framework-dependent 变为 Native AOT 便携目录**（2026-10-25 修订：精简 exe + 旁置原生 DLL，非单文件）：目标机器不再需要 .NET 10 Desktop Runtime；交付物是**解压即用**的目录——根目录放精简 exe 与 4 个随包原生 DLL，运行时数据收拢在 `.data/`，OCR 原生库与词典按需下载到 `deps/`（该下载机制今天已经存在，不是新功能）。
3. **为跨平台留出真实可能**：TFM 去掉 `-windows` 平台后缀，把捕获、热键、托盘、剪贴板、密钥存储这些平台能力从业务核心里切出去。**本期不承诺 Linux/macOS 可跑**（主人已裁决：只留接口，实现另开事项），只承诺「平台边界已切开、Windows 实现就位」。

**为何值得用 Epic 承载：** 跨 UI、平台服务、配置/密钥、OCR/注音运行时、构建发布、自测基座六个区域；含四个未验证的硬风险；规格会在批次边界内持续演化（AOT 阻断项的对策会随实测改变）。

**为何要独立分支：** 本次改动面覆盖六个区域且中间态不可发布（如双引擎并存期、Debug 可跑而 AOT 未通），放在 `main` 上会长期污染主干；分支内允许中间态，合回 `main` 前必须满足文末关闭条件。

**关联 project spec：** 「捕获与选区」「OCR」「翻译、结果与配置」「工具链与发布」四节都会被改写；「尚未实现」不变。

**来源 Vision：** `codestable/vision/index.md` 的「形态」与「依赖姿态」——「单文件可执行，常驻托盘，无主窗口」「运行期不捆绑 .NET / Python / WebView2 运行时」。本 Epic 是这两条从「目标」变成「当前真相」的那一步。**Vision 正文已严重过期**（仍写 Go/Gio/leaf-ocr、「跨平台不是目标」），关闭时必须同步，见文末「Vision 同步检查」。

**被本 Epic 取代的既有取舍：** `codestable/roadmap/002-csharp-single-process-migration/spec.md` 明确记了「不迁移 WinUI 3；将来富文本需求由 WPF 面板承接，理由见设计文档第 12 节」。该判断在「框架自带运行时 + 单文件」这个新目标下不再成立，本 Epic 正式推翻它。**推翻不等于当时的理由错了**——当时 WPF 确实是捕获、透明窗口、富文本三件事上 API 最全的选项；变的是约束（体积与运行时依赖从「可接受」变成「必须」）。

---

## 现状：一次触发如何产生结果

```text
Ctrl+Shift+A
  │
  ├─ WinForms NativeWindow（GlobalHotkeyWindow）收到 WM_HOTKEY
  │
  ├─ SystemInformation.VirtualScreen → GDI BitBlt（DesktopCapturer）冻结整屏
  │
  ├─ WinForms Form（SelectionForm）全屏无边框置顶，GDI+ 自绘压暗/边框/尺寸签
  │     └─ 拖框 → System.Drawing.Rectangle（物理像素）
  │
  ├─ Bitmap.Clone 裁剪 → PNG（PngEncoder，GDI+ 编码）
  │
  ├─ 二选一：
  │     ├─ vision_oneshot：PNG 直喂 provider
  │     └─ local_ocr：Microsoft.ML.OnnxRuntime 跑 PP-OCRv6-small
  │           └─ onnxruntime.dll 经 NativeLibrary.SetDllImportResolver 从 deps/ 加载
  │
  └─ WPF 单实例 ResultPanelWindow（非模态、WS_EX_NOACTIVATE）
        ├─ 原文区：FlowDocumentScrollViewer + RubyInlineControl（振假名两行堆叠）
        │     └─ 双击原地切 TextBox 编辑（不全选、未改失焦复原）
        ├─ 译文区：流式 TextBox
        ├─ 精讲区：Expander + ExplanationCardView（微结构化卡片）
        ├─ 划词气泡：SelectionBubblePopup（Popup + 失焦关闭）
        └─ 状态/指标栏：InfoBar + Token/TTFT
```

**配置与密钥旁路：** `ConfigStore` 用 YamlDotNet 读写 `程序目录\.data\config.yaml`（首启从 `config.template.yaml` 字节复制）；API Key 走 DPAPI 整块加密存 `.data/keys.bin`；收藏走 SQLite（`.data/collections.db`）。OCR 权重与 ONNX Runtime native DLL 按需下载到 `程序目录\deps\`。

**托盘旁路：** WinForms `NotifyIcon` + 自绘 `ToolStripProfessionalRenderer`（`TrayMenuRenderer`，约 200 行）做深色菜单；`Shell_NotifyIconGetRect` 反射取私有字段定位气泡。

### 影响三层

**必须修改（约 9 千行，UI 与平台耦合）**

| 区域 | 现状落点 | 规模 |
|---|---|---|
| 应用宿主 | `App/NaniKoreApp.cs`、`Program.cs` | 约 2100 行 |
| 界面控件 | `App/Controls/*`（含 XAML） | 约 1750 行 |
| 设置界面 | `App/Settings/*`（含 XAML） | 约 1480 行 |
| 主题 | `App/Theme/*`（Palette + Controls.Base/Input 约 1060 行 XAML） | 约 226 行 C# |
| 选区浮层 | `Overlay/SelectionForm.cs` | 约 210 行 |
| 托盘 | `App/TrayIconHost.cs`、`App/Theme/TrayMenuRenderer.cs` | — |
| 剪贴板输入 | `Input/PasteInputAcquirer.cs` | 约 860 行 |
| 屏幕捕获 | `Capture/DesktopCapturer.cs`、`Imaging/PngEncoder.cs` | 约 55 行 |
| 全局热键 | `Input/GlobalHotkeyWindow.cs` | 约 65 行 |
| UI 自测基座 | `Diagnostics/SelfTest.Ui*.cs` | 约 1650 行 |

**需要验证（约 10 千行，业务核心，大部分预期零改动，但 AOT 可行性未证；OCR 管线例外，见「仍待调查」）**

`Translation/*`、`Phonetics/*`、`Ocr/*`（算法部分）、`Config/*`、`Favorites/*`、`Diagnostics/*`（非 UI 部分）。

**仍待调查**

- Avalonia 12 + .NET 10 + NativeAOT 在本机产物的真实体积（exe / DLL / 总量）与启动时间。
- `YamlDotNet` 静态上下文对现有 `AppConfig` 类型图（嵌套类 + `List<T>` + 多行块标量）的覆盖度。
- `Microsoft.ML.OnnxRuntime` 1.28.0 托管侧在 NativeAOT 下的 trim 警告面。
- `LibNMeCab` 0.10.2 是否真的无反射（上游 README 声称新设计「排除了转型与反射（new T）」，但未标注 `IsTrimmable`）。
- `Microsoft.Data.Sqlite` / `SQLitePCLRaw` 在 NativeAOT 下的加载方式（已由穿刺 7 实测打通：2026-11-01，`e_sqlite3.dll` 作为 RID 资产随包旁置根目录，.NET 原生探测自动加载，0 ILC 警告，51 项全生命周期断言全绿）。
- （审计修订）**OCR 核心的 `System.Drawing.Bitmap` 迁移**：`OcrEngine.Recognize` / `TextDetector.Detect` / `TextRecognizer.Recognize` / `TensorImageConverter`（约 266 行）公开 API 全部接收 `System.Drawing.Bitmap`，而 System.Drawing 在 NativeAOT 下不可用（GDI+ 封装依赖 COM `IStream` 编组，NativeAOT 无内建 COM；已查证 dotnet/runtime#115753 与 .NET 设计文档「only in the context of Windows Forms and GDI+」）。这不是零改动区，需系统性 Bitmap→Skia 迁移，边界方案见第 3 节归属判断。
- （审计修订）10-24 新增的 `App/OcrHttpServer.cs`（约 15 KB）与既有但初版映射表漏列的 `OcrProgressWindow` / `OcrResultWindow` 两个 WPF 窗口，补入「应用宿主」行清点。

---

## 质量目标

只选会改变设计与验收判断的项。选中即承诺。

| 特性 | 目标（可观察结果） | 来源 | 证据 |
|---|---|---|---|
| **性能效率**（时间特性 / 资源利用） | 交付物**免安装**：解压即跑，目标机器零 .NET 运行时依赖；**exe 尽量精简**（目标 ≤ 18 MB；主人 2026-10-25 裁定体积为记录值、不设硬指标，硬指标是零运行时依赖 + 免安装 + 解压即用）；原生 DLL 随包旁置不另设限（合计约 8–13 MB）；热键到冻结画面显示的延迟不回退；冷启动时间 P6 实测入档 | 用户要求 + Vision「形态」 | P0 实测体积表；P6 与迁移前同路径打点对比 |
| **兼容性**（互操作性 / 共存性） | 现有 `.data/config.yaml`、`keys.bin`、`collections.db` **零迁移、零重写**即可被新程序读取；`config.template.yaml` 保持字节级模板语义；`deps/` 下载清单与 `publish -full` 契约不变 | project spec「配置模板化」既有约束 | 用迁移前真实 `.data` 副本启动新程序的自测 + 人工确认 |
| **可维护性**（模块化 / 可测试性） | 平台能力与业务核心之间只有一道边界，业务代码不出现 `System.Windows` / Win32 / `System.Drawing`；全量自测在迁移后仍全绿（审计修订后的现状基线：非 UI 88 + UI 23 个测试方法、609 条断言；UI 相关按新引擎重写而非删除） | 本次风险扫描 | `./scripts/dev.ps1 test` 全绿；静态检查无 WPF/WinForms/System.Drawing 残留引用 |
| **信息安全性**（保密性） | API Key 不落明文、不进日志、不进异常信息；Windows 保留 DPAPI（CurrentUser）；`keys.bin` 增加方案版本头，为跨平台方案留格式空间；任何跨平台降级不得静默削弱 | project spec + Vision「密钥与数据」 | `SecretStore` 单测覆盖读/写/损坏/换用户四种路径 + 新旧格式各一轮；人工确认提示文案 |
| **交互能力**（易操作 / 用户差错防御） | 双模原文区（双击编辑不全选、未改失焦复原）、划词气泡失焦关闭、托盘四菜单项行为、结果面板不抢焦点——四项交互契约不回退 | Epic 001 已确立的稳定约束 | 对应自测保留 + 人工走查 |
| **可靠性**（容错性） | OCR 依赖缺失、剪贴板还原失败、热键被占用三条降级路径的提示文案与可达性不回退 | 现状既有行为 | 对应自测保留 + 人工走查 |

**未选不写。** 功能适宜性的大部分由「交互契约不回退」与既有自测覆盖，不单列；安全性（Safety）不适用——本产品不涉及人身、财产与环境危害。

---

## 当前怎么理解（活规格）

### 1. 技术选型

**引擎：Avalonia 12.1.x（`net10.0`，不带 `-windows` 后缀）。**

理由按经济性梯子排：

1. 平台原生能力？不够。WPF/WinForms 就是平台原生，问题正出在它们绑定 .NET Desktop Runtime 与 Windows。
2. 已安装依赖是否够？不够，需要新依赖。
3. 最小新依赖：Avalonia 是唯一同时满足「跨平台 + 自带渲染 + XAML 方言近似 + 有官方 Native AOT 路径」的选项（原生库旁置即可工作，不再需要静态链接方案，2026-10-25 修订）。`Avalonia.XPF` 走 WPF 兼容路线，体积与 AOT 收益都不对；自绘（SkiaSharp 直接写）等于把 Avalonia 已做的事重做一遍。

**WinUI 3 为什么否（已查证，来源见文末）：**

- **体积：** unpackaged 自包含发布必须捆绑 Windows App SDK 运行时（`WindowsAppSDKSelfContained=true`），仅运行时一项约 30 MB 量级，加上 .NET 自包含运行时与 XAML 编译产物，总输出在 100 MB 量级——与 ≤ 25 MB 目标差一个数量级。
- **单文件：** WinUI 3 自 Windows App SDK 1.5 起支持 `PublishSingleFile`，但语义是**首启解压到临时目录**（`IncludeAllContentForSelfExtract`），不是零解压二进制；且 NativeAOT 下 `IncludeNativeLibrariesForSelfExtract` 不被支持（dotnet/sdk#39202），WASDK 原生运行时进不了单文件。
- **AOT 成熟度：** WinUI 3 自 Windows App SDK 1.6 起有 NativeAOT 支持，但官方讨论承认「生成代码仍有未标 AOT 兼容的问题」；CsWinRT 3.0（trim/AOT first-class 重写）尚在推进、未随 .NET 10 落地。Avalonia 的 NativeAOT 是官方文档级现成路径。
- **跨平台：** Windows-only，与「平台边界切开、将来 Linux/macOS」直接冲突——即使本期只做 Windows，选型也要为已裁决的接口预留负责。

**关键事实（已查证，来源见文末）：**

- Avalonia 当前最新稳定版 12.1.x（本 spec 查证日为 12.1.4）；官方 Native AOT 文档要求 `PublishAot` + `IsAotCompatible`（12.0 起不再需要 `BuiltInComInteropSupport=false`）；XAML 走编译期绑定（`x:CompileBindings`），禁运行期动态加载。
- `StaticLink.Avalonia` 持续维护（2026 年中起跟随 Avalonia 稳定版每日 CI 发版）；Avalonia 12 对应 `StaticLink.Avalonia` 4.150.1-7922.1（配 SkiaSharp 4.150.1）。Windows 只需主包，`.Native` 变体仅 macOS 需要。**（2026-10-25 起此路线不再使用，见「交付形态」；备查：上游 prefer 配对为 Avalonia 12 ↔ SkiaSharp 3.119.4 ↔ `StaticLink.Avalonia 3.119.4-7922.1`，若单文件重新成为硬要求从这里捡起。）**
- Windows 上 Avalonia NativeAOT 需三个原生 DLL：`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll`。放弃静态链接后，它们由各自 NuGet 包的 RID 资产自动复制到发布目录，**随包旁置、零加载代码**（见「交付形态」）。
- **NativeAOT 不支持 `IncludeNativeLibrariesForSelfExtract`**（dotnet/sdk#39202，仍未解决）。原生库要么旁置，要么静态链接。
- 第三方报告 Avalonia NativeAOT 单文件约 18 MB、UPX 后约 15 MB；**本机数字必须实测**（P0 穿刺 1）。
- （审计修订）Avalonia 官方讨论区有现成起步模板 `Avalonia-AOT-SingleFile-Starter`（#21660，2026-06）：Avalonia 12 + .NET 10 的 Windows/Linux/macOS NativeAOT 骨架，含 NativeAOT-safe JSON 源生成示例——穿刺 1 参考其工程结构（它的 `StaticLink.Avalonia` 单文件部分本 Epic 不用）。

**捆绑依赖：**

- `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent`（12.1.x，具体补丁版 P0 定）。
- **`StaticLink.Avalonia`（2026-10-25 移除）**：主人改选「精简 exe + 原生 DLL 旁置」后不再需要静态链接包——`dotnet publish -r win-x64 -p:PublishAot=true` 会把三个原生库作为 RID 运行时资产自动复制到输出目录。少一个第三方构建依赖，穿刺 1 与跨平台打包同步简化（版本配对表也一起不再关心）。
- `SkiaSharp` **3.119.x（与 Avalonia 12 同源）**：承接 `System.Drawing` 的位图与 PNG 编解码；其 `runtimes/win-x64/native/` 资产就是旁置的 `libSkiaSharp.dll` 来源。
- `Microsoft.Data.Sqlite`、`LibNMeCab`、`Microsoft.ML.OnnxRuntime` **版本不动**——它们是业务核心，与 UI 引擎无关。
- `YamlDotNet` 保留，配 `StaticContext` 源生成（裁决见「AOT 阻断项」1 与文末裁决记录）。**当前钉 18.1.0 + `Vecc.YamlDotNet.Analyzers.StaticGenerator` 18.1.0（同号不混搭）**——2026-10-25 穿刺 2 实测后主人裁定从 16.3.0 升级（裁决 18）：两版静态路径跨版本产出零差异、AOT 均 0 警告，升级无行为风险，收益是 net10.0 原生 target 与上游 AOT/安全修复（最大递归默认值、struct 支持；StaticGenerator 已收进 YamlDotNet 主仓）。升级不改变「单路线、不并行维护」的裁决。

  **（2026-10-25 穿刺 2 实测结论，issue 002-x）：静态路线成立，升级已执行。** 四个可落地事实：① 上下文类标记用 `[YamlStaticContext]` + 每个类型一条 `[YamlSerializable(typeof(X))]`，**生成器不沿类型图递归发现**，嵌套段与 `List` 元素必须逐个登记（漏登记报 YDNG001 警告，schema 变更时这是唯一同步点）；② 上下文类必须 `public partial`（`internal` 报 CS0262）；③ 18.1.0 与 16.3.0 静态路径在 10 份样本上产出**跨版本零差异**，两者静态专用 AOT 发布均 0 警告——升级无行为风险；④ 含反射基线的构建报 IL3050×2 + YamlDotNet 程序集 IL2104/IL3053，精确证明「反射是阻断点、静态上下文把它整个移出 AOT 图」。

  **（2026-10-25 主人批准升级并落地）：主工程已钉 `YamlDotNet` 18.1.0 + `Vecc.YamlDotNet.Analyzers.StaticGenerator` 18.1.0（同号不混搭）；`src/NaniKore/Config/ConfigYamlStaticContext.cs` 登记 8 个类型；`ConfigStore` 两个 builder 已切 `StaticDeserializerBuilder` / `StaticSerializerBuilder`（三选项与迁移前逐项一致）。主工程 Debug 构建 0 警告（`TreatWarningsAsErrors` 下无 YDNG = 类型图无漏登记），全量自测 630 PASS / 0 FAIL。** 此后 `src/NaniKore` 内不再有 YamlDotNet 反射路径，P5 的 YamlDotNet 对策项提前完成。

### 2. 交付形态与目录契约

（主人 2026-10-25 裁决：免安装是硬要求；exe 尽量精简，原生 DLL 随包旁置，大件按需下载进 `deps/`。）

```text
<程序目录>/                      # 解压即用，可整体拷贝 / U 盘运行，无安装动作
├── NaniKore.exe                 # NativeAOT 精简 exe（预期 12–20 MB，P0 实测入档）
├── libSkiaSharp.dll             # 原生图形（Skia）——渲染底座，随包旁置
├── libHarfBuzzSharp.dll         # 原生文字整形（HarfBuzz）——渲染底座，随包旁置
├── av_libglesv2.dll             # 原生 GLES（ANGLE）——渲染底座，随包旁置
├── e_sqlite3.dll                # SQLite 原生——收藏底座，随包旁置
├── config.template.yaml         # 只读模板，首启按字节复制到 .data/config.yaml（issue 017 契约不变）
├── NOTICE                       # 许可条目
├── deps/                        # 按需下载：onnxruntime*.dll、OCR 权重、注音词典（现状机制原样保留）
└── .data/                       # 运行期生成：config.yaml、keys.bin、collections.db
```

规则：

1. **根目录 = 精简 exe + 随包原生 DLL，旁置标准 = .NET 原生探测（应用目录），零解析代码。** 划分规则：**主旅程依赖的原生库随包旁置**（Skia / HarfBuzz / ANGLE 是渲染底座，`e_sqlite3.dll` 是收藏底座）；**可选功能的大件按需下载到 `deps/`**（ONNX 13 MB + OCR 权重 30 MB + 注音词典 64 MB，主旅程不依赖它们）。这不是「散落 DLL」——每个 DLL 都有明确归属、与 exe 同生命周期，更新时随 ZIP 整目录覆盖。
2. **`deps/` 与 `.data/` 是仅有的两个子目录**，与今天口径一致——`dev.ps1` 的 junction、`publish -full` 契约、设置页「下载依赖」入口全部不变。
3. **不内嵌、首启不解压。** 把原生库压进 exe 会把体积推到 28–32 MB 且首启有解压延迟，已评估并否决；主人 2026-10-25 明确「宁可 DLL 旁置也不要胖 exe」。`OnnxRuntimeNativeLoader` 的解析器从 `deps/` 加载即可，现有机制已在工作。
4. **免安装验收 = 干净环境（无 .NET 运行时）解压 ZIP，直接完成「热键 → 框选 → OCR（deps/ 就位）→ 翻译 → 收藏」全旅程。**

**SQLite 原生库的归属（2026-10-25 修订，穿刺 7 验证）：** 按主人裁定，`e_sqlite3.dll` **不静态链接、不进 `deps/` 下载**，作为 RID 资产随包旁置根目录——收藏是主旅程一环，必须解压即用；穿刺 7 只需验证 AOT 下 SQLitePCLRaw 的 bundle 探测仍自动找到它，不通才退化到显式 provider。

### 3. 目标架构与归属

核心判断：**这次变化的形状是「换承载」，不是「重切业务」**。业务归属（provider 抽象、两阶段会话、注音注册表、收藏服务、配置档唯一身份）全部仍然正确，按 code-design 的尺子是「归属仍对 → 扩展做深」，不是重切。

真正需要新切开的只有一条边界：**平台能力**。今天它散落在 `Overlay/`、`Capture/`、`Input/`、`App/TrayIconHost.cs` 里，直接依赖 WinForms/Win32/GDI。

```text
NaniKore（net10.0，Avalonia 宿主）
│
├── 业务核心（平台无关，本次预期零改动）
│    Translation · Phonetics · Ocr(算法) · Config(模型与存储) · Favorites
│    └── 已有先例：IClipboardGateway 就是为可测性切出的真实接缝，沿用同一手法
│
├── 平台层（Platform.Windows，全部 OperatingSystem.IsWindows() 守卫）
│    IScreenCapture     屏幕捕获（Win32 BitBlt → SKBitmap）
│    IGlobalHotkey      全局热键（独立 Win32 消息窗口，纯 P/Invoke）
│    ISecretStore       密钥加密（Windows DPAPI；keys.bin 带方案版本头，跨平台方案见第 7 节）
│    ITrayHost          托盘（薄封装；Windows 实现二选一，见裁决 11：直连 Shell_NotifyIcon P/Invoke，或 Avalonia TrayIcon + 自绘通知小窗）
│    └── 本期只有 Windows 一个实现；接缝因跨平台目标而真实存在，不为假想扩展预造
│
└── 表现层（Avalonia AXAML + 少量自绘）
     结果面板 · 设置窗 · 选区浮层 · 消息对话框 · 主题与控件模板
```

**归属判断：**

- `IClipboardGateway` **保留原样**，只把运行期实现从 WinForms `Clipboard` 换成 Win32 P/Invoke。这是现成的正确接缝，不新建平行抽象。
- `SelectionForm` 的拖框状态机（`Overlay/SelectionGeometry.cs`，纯几何，约 27 行）**原样保留**，只换渲染宿主——它已经是平台无关的。
- `HotkeyGesture`（手势解析，约 220 行）**原样保留**，与 UI 引擎无关；录制控件换成 Avalonia `Control` 后仍复用它，避免「能录不能注册」的分叉。
- `PasteInputAcquirer` 的状态机**原样保留**，只替换 `IClipboardGateway` 实现与 `SendInput` 的调用点。
- `NaniKoreApp` 从 `System.Windows.Application` 派生改为 Avalonia `Application` + `ClassicDesktopStyleApplicationLifetime`；热键/托盘/面板的生命周期编排逻辑照搬。
- （审计修订）**OCR 核心的 Bitmap 边界**：`OcrEngine.Recognize` / `TextDetector.Detect` / `TextRecognizer.Recognize` / `TensorImageConverter`（约 266 行）公开 API 接收 `System.Drawing.Bitmap`，System.Drawing 在 NativeAOT 下不可用。方案：在成像边界做一次性转换——入口只有屏幕捕获、`OcrHttpServer`、`--ocr-file` 三处；OCR 管线内部统一换为 Skia 表示，不把 `SKBitmap` 泄漏进业务核心深处，也不在核心里留 System.Drawing 适配层。

**不碰的边界：** `Translation/*` 的 provider 契约与两阶段协议、`PromptTemplates`、`LanguageDetector`、`TranslationSession`、注音标注器注册表、`SqliteFavoriteService` 的 schema 与 upsert 语义、配置档 name 唯一索引、`.data` 目录契约、`deps/` 按需下载清单与 SHA-256 校验。

### 4. AOT 阻断项与对策

按「不通就换方案」的严重度排序（11、12 为 2026-10-25 审计修订新增，分别是 10-14 之后新代码与能力盘点暴露的）。

| # | 阻断项 | 现状 | 对策 | 打通判据 |
|---|---|---|---|---|
| 1 | **YamlDotNet 反射反序列化** | （迁移前基线，2026-10-25 已消除）`ConfigStore` 原用 `DeserializerBuilder` / `SerializerBuilder`，运行期反射遍历 `AppConfig` 类型图 | **已裁决：`StaticDeserializerBuilder` + `[YamlStaticContext]`**（`Vecc.YamlDotNet.Analyzers.StaticGenerator`）——源生成、无反射、改动集中在一个上下文类，高性能与好维护双优；类型图不兼容时退手写 YAML 子集解析器（仅需支持嵌套映射、`- ` 列表项、标量、`~`、块标量、注释，约 300–350 行），但**不并行维护两条路** | 用一份真实 `.data/config.yaml`（含中文注释、四行档形态、多行 prompt）反序列化→序列化→再反序列化，结果与迁移前逐字节等价。**已打通并落地（2026-10-25，issue 002-x，结论「成立」）：src 已切静态路线，主工程 0 ILC 警告、全量自测 630 PASS** |
| 2 | **System.Text.Json 反射** | `SecretStore` 用 `JsonSerializer.Deserialize<Dictionary<string,string>>` / `SerializeToUtf8Bytes` | 加 `JsonSerializerContext` 源生成；`Dictionary<string,string>` 在源生成下有内建支持，预期改动极小。`keys.bin` 同时加方案版本头（见第 7 节） | AOT 发布物跑通「保存→读取→换用户→损坏文件」四条路径 + 旧格式零迁移 |
| 3 | **原生 DLL 无法进 NativeAOT 单文件**（2026-10-25 修订：放弃单文件，改随包旁置） | Avalonia 需 `libSkiaSharp` / `libHarfBuzzSharp` / `av_libglesv2`；OCR 需 `onnxruntime.dll` + `onnxruntime_providers_shared.dll`；SQLite 需 `e_sqlite3.dll` | **Avalonia 三个 + SQLite**：不静态链接、不内嵌，作为 RID 运行时资产随包旁置根目录，.NET 原生探测自动加载（零解析代码；SQLite 侧由穿刺 7 验证 AOT 下 bundle 探测仍工作）。<br>**ONNX**：不随包；沿用「设置页按需下载到 `deps/`」+ `NativeLibrary.SetDllImportResolver` 从 `deps/` 加载。（**ONNX 侧已打通**，2026-10-25 issue 003：AOT 发布 0 警告，det/rec 两会话 + 真实推理 + CTC 全绿，JIT 与 AOT 输出一致；注意 NuGet 会把 `onnxruntime.dll` 复制进发布根目录，**必须摘除**，否则根目录拷贝优先于 `deps/`，按需下载机制形同虚设） | 干净环境（无 .NET 运行时）解压即启动；下载 `deps/` 后 OCR 与收藏全绿；根目录 DLL 清单恰为 3 个 Avalonia 原生 + `e_sqlite3.dll` |
| 4 | **LibNMeCab 反射风险** | `MeCabTagger.Create` / `MeCabIpaDicTagger.Create` 驱动日/中注音 | **已打通（2026-10-25 穿刺 4，issue 004-x）**：trim 面 0 警告，上游「排除 `new T` 反射」声称属实（虽未标 `IsTrimmable`），无需 `TrimmerRootAssembly` / `DynamicDependency`；注音器按原样接回，不需惰性降级改造。退路（保留描述符 → 惰性降级纯文本）均不触发，记录备查 | AOT 发布物对固定句子注音结果与迁移前**逐字节等价**（已达成：JIT vs AOT MATCH；`InvariantGlobalization` 对 UTF-8 词典无副作用，见裁决 8） |
| 5 | **振假名富文本无对应物** | `FlowDocumentScrollViewer` + `RubyInlineControl`（Grid 两行堆叠）+ 选区文本提取 | Avalonia 无 FlowDocument。采用**两行堆叠方案**：`SelectableTextBlock` + `Inlines`，每个 ruby 词元用内联容器挂一个两行小面板（沿用现有 `RubyInlineControl` 的视觉与交互契约：悬停变色、单击复制读音、右键三选）。代价：跨词元的原生划选连续性会变差，需实测并可能改为「整段只读 + 右键复制」（是否接受见文末开放项） | 日语句子的振假名排版、三种复制格式、双击编辑双模，四条路径人工确认不回退 |
| 6 | **全局热键无 Avalonia API** | WinForms `NativeWindow` 承载 `RegisterHotKey` | 独立 Win32 消息-only 窗口（纯 `[LibraryImport]`，脱离 WinForms），跑在专用 STA 线程；跨平台时替换实现，接口不变 | 两个热键独立注册/注销/冲突提示、录制期间临时注销，行为与现状一致 |
| 7 | **`InvariantGlobalization` 取舍** | 关掉 ICU 省约 30 MB，但影响 `CultureInfo` | 需审计：`CultureInfo` 出现于 4 个文件 9 处；`NumberBox` 的 `int.Parse` 必须显式 `InvariantCulture`；MeCab 与 SQLite 是否依赖区域设置待实测。**默认取「开启」，除非 P0 发现功能性破坏**（主人未异议，P0 审计结论后定稿） | 审计清单逐条给出「安全 / 需改 / 阻塞」结论 |
| 8 | **嵌入资源的动态名** | `GetManifestResourceStream($"NaniKore.Phonetics.Data.{fileName}")` 动态拼接 | `IlcTrimMetadata=true` 下可能被裁。改为编译期强类型访问（源生成一张资源名→内容的表），或保留拼接但加 `TrimmerRootDescriptor` 保留资源 | AOT 发布物三语停用词表与常用字符表全部加载成功 |
| 9 | **`Segoe MDL2 Assets` 图标字体** | 约 20 处 `FontFamily="Segoe MDL2 Assets"` 字形（`&#xE894;` 等） | Windows-only 字体。方案：本期保持（Windows 仍是主目标），但在主题里收敛为单一 `IconGlyph` 资源键，跨平台时整体替换为路径图标或矢量字体 | — |
| 10 | **编译绑定** | `ExplanationCardView.xaml` / `FavoritesPage.xaml` 用 `{Binding}` 反射绑定 | Avalonia AOT 要求 `x:CompileBindings` + `x:DataType`。这是 XAML 层的机械改造，量不大但必须全做，否则 AOT 下绑定静默失效 | AOT 发布物设置页与精讲卡数据正常渲染 |
| 11 | **OcrHttpServer 的反射 JSON 序列化**（审计修订，10-24 新增） | `App/OcrHttpServer.cs` 用 `JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)` 反射序列化 OCR 结果；`Func<Bitmap, OcrResult>` 签名还带着 System.Drawing | 加 `JsonSerializerContext` 源生成（payload 类型图简单，改动集中）；签名随 Bitmap 迁移换掉 | AOT 发布物跑通 health / 原始二进制 / 路径查询 / 404 四条自测 + `--ocr-file --json` CLI |
| 12 | **托盘气泡通知无 Avalonia 对应物**（审计修订） | `TrayIconHost.ShowWarning`（WinForms `NotifyIcon.ShowBalloonTip`）有 7 个调用点：配置加载警告、配置版本不匹配、划词翻译失败、热键注册失败 ×2、OCR HTTP 启动失败。Avalonia `TrayIcon` 只有 `Icon/ToolTipText/Menu/Command/IsVisible`，**无 balloon API**（AvaloniaUI#2155 自 2018 挂起至今仍 open；#6734 关闭为重复）；Avalonia.Labs 实验性 NativeNotifications 走 Windows toast，需 Start Menu 快捷方式 + AUMID，与便携免安装冲突 | 计划期裁决（裁决 11，三选一）：(a) `ITrayHost` 直连 `Shell_NotifyIcon` P/Invoke 自带 `NIF_INFO` 气泡；(b) 保留 Avalonia `TrayIcon`，气泡替换为自绘 `WS_EX_NOACTIVATE` 通知小窗（`ABM_GETTASKBARPOS` 定位 + 定时淡出）；(c) 分级路由到 MessageDialog / 面板 InfoBar | 7 个调用点的提示在「无任何窗口」场景下仍可达，文案与现状一致（质量目标「降级路径可达性不回退」） |

### 5. 组件映射

| 现状 | Avalonia 目标 | 备注 |
|---|---|---|
| `System.Windows.Application` | `Application` + `ClassicDesktopStyleApplicationLifetime` | `ShutdownMode.OnExplicitShutdown` 语义需显式复刻（拒绝启动路径要拿到退出码 1） |
| `Window` / `UserControl` + `.xaml` | `Window` / `UserControl` + `.axaml` | `x:ClassModifier="internal"` 不再需要，代码后用 `internal` |
| `ResourceDictionary.MergedDictionaries` + 运行期换槽 | `Application.Styles` + `ResourceInclude`；深浅色用 `ThemeVariantScope` / `RequestedThemeVariant` | 主题热切换的机制不同，需重写 `ThemeManager`（约 226 行）；`SystemEvents.UserPreferenceChanged` 保留 |
| `FlowDocumentScrollViewer` | `SelectableTextBlock` + `Inlines` | 见阻断项 5 |
| `GridSplitter` / `Expander` / `ToggleSwitch` / `ContextMenu` / `Popup` | 同名控件均存在 | 直接映射 |
| `NumericUpDown`（被 `NumberBox` 取代过） | `NumericUpDown` | **删掉 `NumberBox`（约 200 行）**，用平台控件；这是本次唯一「变简单」的地方 |
| `HotkeyRecorderBox : Control` + `DependencyProperty` | 自定义 `Control` + `AvaloniaProperty` + `ControlTemplate` | 逻辑（手势解析、Esc 解绑、修饰键预览）照搬 |
| `InfoBar` / `SettingsGroup` / `SettingsRow` | 自写轻量控件或直接用 `Border`+`TextBlock` 组合 | 按实际样式决定，不默认搬类 |
| `MessageDialog : Window` | `Window` + `ShowDialog()` | 主题化对话框保留 |
| WinForms `NotifyIcon` + `ContextMenuStrip` + 自绘渲染器 | `TrayIcon.Icons` + `NativeMenu`（Avalonia 11.3+ / 12 内置） | **放弃 `TrayMenuRenderer`（约 200 行）**，主人已接受托盘菜单为系统原生菜单；见「有界简化」 |
| `Shell_NotifyIconGetRect` + 反射私有字段定位 | 删除 | Avalonia 在光标处弹原生菜单，不需要自定位 |
| `ApplicationIcon.LoadWinForms/LoadWpf` | `WindowIcon`（`AvaloniaResource` 里的 `.ico`） | 单一来源，去掉两条加载路径 |
| `Bitmap` / `Graphics` / `ImageFormat.Png` | `SKBitmap` / `SKImage.Encode` | `PngEncoder` 改约 20 行 |
| `SystemInformation.VirtualScreen` + `Graphics.CopyFromScreen` | Win32 `BitBlt` → `SKBitmap`；`Screens` API 取虚拟桌面 | `DesktopCapturer` 重写约 40 行 |
| `WinFormsClipboardGateway` | Win32 `OpenClipboard` / `GetClipboardData` / `SetClipboardData` + 序列号轮询 | 顺带去掉 STA 线程依赖 |
| `System.Windows.Clipboard.SetText` | `TopLevel.Clipboard`（异步） | 注意同步 API 消失，`RubyInlineControl.TrySetClipboard` 的三次重试要改成异步 |
| `Dispatcher.InvokeAsync` | `Dispatcher.UIThread.InvokeAsync` / `Post` | 全局替换；`NaniKoreApp` 里大量出现 |
| `WM_DISPLAYCHANGE` / DWM 深色标题栏 | 保留 `DwmSetWindowAttribute` P/Invoke | `Interop/NativeMethods.cs` 大部分原样保留 |
| `WindowInteropHelper(window).Handle` | `window.TryGetPlatformHandle()` | 主题层取 HWND 的入口 |
| `ProtectedData`（DPAPI）存 `keys.bin` | API 不变；文件外层加方案版本头 | 改动集中在 `SecretStore` 一个文件；方案设计见第 7 节 |

### 6. 体积预算

**这是目标，不是承诺；口径是「根目录 exe」，不含 `deps/` 与 `.data/`。** 第三方报告的量级（2026-10-25 已按穿刺 1 本机实测校准，见本节末）：

| 组成 | 量级 |
|---|---|
| .NET NativeAOT 运行时 + BCL（trimmed + `InvariantGlobalization` + `OptimizationPreference=Size`） | 8–12 MB |
| Avalonia + Fluent 主题 + Skia + HarfBuzz + ANGLE（2026-10-25 起原生部分改旁置 DLL，从 exe 剔除约 8–13 MB） | 10–18 MB |
| ONNX Runtime 托管封装 + SQLite + LibNMeCab + 业务代码 | 2–4 MB |
| **合计（未压缩）** | **20–34 MB** |

叠加 `StripSymbols=true`、`IlcTrimMetadata=true`、`DebugType=none`、`SatelliteResourceLanguages=en` 后落在 20–28 MB；UPX 可再到 12–18 MB，但牺牲启动速度与内存，**默认不用**（主人未异议）。

**（审计修订）口径校准：** 迁移前基线是 framework-dependent 单文件 **26.8 MB**（`--self-contained false` + `PublishSingleFile`，zip 9.0 MB）。AOT 的真实收益是**运行时依赖归零 + 免安装**；旁置原生 DLL 后 exe 预期 12–20 MB（约为基线一半），总体积 20–28 MB 量级，「体积轻巧」的感知主要来自 zip（预计 7–8 MB）。验收硬指标 = 零运行时依赖 + 目录契约；exe 绝对体积记录实测值（裁决 14 已定：不设硬指标，UPX 与压缩内嵌继续搁置）。

**（2026-10-25 主人修订）分布调整：** 放弃静态链接后，Skia/HarfBuzz/ANGLE 原生从 exe 移入旁置 DLL：exe 预期 12–20 MB。**变的只是分布、不是总量**的说法已被穿刺 1 实测部分推翻，见下。

**（2026-10-25 穿刺 1 实测校准，取代本节的量级估计）：** 本机实测（Avalonia 12.1.4 + Fluent + SkiaSharp 3.119.4 最小工程，开关组 = `InvariantGlobalization` + `OptimizationPreference=Size` + `StripSymbols` + `IlcTrimMetadata` + `DebugType=none` + `SatelliteResourceLanguages=en`，证据见 issue 001）：

| 组成 | 实测 |
|---|---|
| 精简 exe（NativeAOT，含 Avalonia + Fluent + 业务骨架） | 13.53 MB |
| 旁置原生 DLL（libSkiaSharp 11.1 + av_libglesv2 5.1 + libHarfBuzzSharp 1.7） | 17.97 MB |
| 目录合计 | 31.50 MB |
| 冷启动（进程创建 → 窗口呈现，墙钟） | 温 335–450 ms；冷文件缓存首启 1,399 ms |
| ILC 警告 | 0 条（最小工程基线，非业务代码面） |

对预算表的三点修正：**旁置原生 DLL 合计不是 8–13 MB 而是约 18 MB**（Skia 11.1 + ANGLE 5.4 占 16.5 MB）；目录合计落在 31.5 MB 而非 20–28 MB；「分布变、总量不变」不成立——总量同样高于原估约 3.5 MB。按裁决 14 这些是记录值不是硬指标；exe 仍约为迁移前 framework-dependent 单文件 26.8 MB 基线的一半，AOT 的真实收益保持「运行时依赖归零 + 免安装」不变。

**另一条必须带进 P6 的实测发现：** SkiaSharp / HarfBuzzSharp 的原生 RID 资产会把调试符号（`libSkiaSharp.pdb` 84 MB + `libHarfBuzzSharp.pdb` 21 MB）一起复制进发布目录，`StripSymbols` 管不到——不显式清理的话 zip 凭空多约 105 MB。清理靶已在 issue 001 的脚手架 csproj 里落地（`RemoveNativeSymbolsFromPublish`），P6 接入发布通道时照搬。

**必须说清的边界（不因实测校准而改变）：** NativeAOT 下托管代码无法拆成 DLL，能拆的只有原生库；应用代码更新仍然重发整个 exe。「更新方便」的真实构成是 (a) 原生库在 ABI 兼容时可独立替换（代价：混放不同版本的文件会 ABI 错配 crash，所以更新必须整目录覆盖 ZIP）；(b) exe 本身体积约减半。若主人真正想要的是「改业务逻辑只发一个业务 DLL」，那与 NativeAOT 互斥，只能回到 framework-dependent（违反零运行时依赖）或 self-contained JIT（exe + 上百框架 DLL，形态与体积都不占优）——当前裁决不指向那个方向。

**外置部分不计入体积口径。** ONNX Runtime native（`onnxruntime.dll` + `onnxruntime_providers_shared.dll`，合计约 13 MB）不随包，按需下载到 `deps/`——主旅程不依赖它。这是主人裁决的默认方案；「压缩内嵌 + 首启解压」的备选（约 28–32 MB）已评估并否决，记录在「有界简化」。（2026-10-25 修订：SQLite native 改为随包旁置根目录而非 `deps/` 下载，见穿刺 7 与裁决 15。）

`publish -full` 继续携带 `deps/`（OCR 权重 + 原生库），与今天口径一致；不新增发布形态。

### 7. 跨平台的真实边界

本期**只承诺 Windows 可运行 + 边界已切开**（主人已裁决：Linux/macOS 本期只留接口，实现另开事项）。以下每一项都是跨平台时的独立工作项，本 Epic 不实现，但接口要为它们留位：

| 能力 | Windows 实现 | 跨平台时需要 |
|---|---|---|
| 屏幕捕获 | Win32 `BitBlt`（GDI） | Linux X11/Wayland、macOS CGDisplay；**独占全屏与 DRM 边界不变**（Vision 已记录，不因迁移改变） |
| 全局热键 | Win32 `RegisterHotKey` | X11 `XGrabKey` / Wayland portal / Carbon |
| 托盘 | Avalonia `TrayIcon`（Win32 `Shell_NotifyIcon`） | StatusNotifierItem / AppIndicator（GNOME 需扩展）；macOS 已支持 |
| 剪贴板 | Win32 clipboard API | X11/Wayland selection、macOS NSPasteboard |
| 密钥加密 | DPAPI（`ProtectedData`） | 见下方方案对比；格式已留位，实现另开 |
| 深色标题栏 | `DwmSetWindowAttribute` | 各平台 WM 名不同，按平台空实现 |
| 原生运行时 | `onnxruntime.dll` / `e_sqlite3.dll` | `libonnxruntime.so` / `libe_sqlite3.so`；下载清单需按 RID 分流 |
| MeCab 词典数据 | 平台无关的二进制 | 无需改动 |

**密钥存储跨平台方案对比（主人要求展开）：**

先立威胁模型：API Key 要防的是「同一台机器上的其他用户」和「`.data` 目录被拷到别处」。四种候选：

| 方案 | 平台 | 外部依赖 | 防护强度 | 实现量 | 本期 |
|---|---|---|---|---|---|
| **DPAPI**（`ProtectedData`，CurrentUser） | Windows | 无 | 同一 Windows 用户可解；换用户 / 换机器不可解 | 已有 | **保留** |
| **macOS Keychain** | macOS | Security.framework（系统自带） | 按登录用户 + 逐项 ACL，可限「仅本应用」；与 DPAPI 等价 | 约 150–250 行 P/Invoke | 不做，留格式 |
| **Linux libsecret**（Secret Service） | Linux 桌面 | 需要运行中的 Secret Service（GNOME Keyring / KWallet） | 桌面环境与 DPAPI 等价；**无头 / 容器环境不可用** | 约 200–300 行 DBus 客户端 | 不做，留格式 |
| **AES-GCM + 机器派生密钥** | 全平台 | 无 | 同机任意用户可解（密钥由机器标识派生，无秘密材料）——明显弱于 DPAPI | 约 100 行 | 不做；仅作 libsecret 缺失时的降级候选 |

**结论与设计：**

1. Windows 保持 DPAPI 不变；跨平台实现不在本期（主人已裁决只留接口）。
2. `keys.bin` 增加**方案版本头**（scheme 标签 + 版本），`SecretStore` 按头分流；现有 DPAPI 文件识别为 scheme 1，**零迁移**。将来 macOS / Linux 实现只是新增 scheme，业务代码不碰。
3. 任何跨平台降级**不得静默**：若将采用最弱的 AES 机器派生方案，设置页 / 托盘必须给一行持久提示（质量目标「跨平台降级方案不得静默削弱」）。
4. 换机迁移对所有方案都意味着「重新填 Key」（DPAPI / Keychain 密文均不可移植），因此不需要密钥导出功能。

---

## 现在推什么、先搁什么

### 分支与合并

**开发分支（主人裁决，裁决 7）：** 本 Epic 全程在独立分支 `avalonia` 开发，`main` 不动。分支内允许中间态（双引擎并存、Debug 可跑而 AOT 未通）；合回 `main` 的判据 = 文末关闭条件全绿 + 主人确认，合入方式建议 squash（中间态无保留价值）。

**穿刺脚手架的位置：** 仓库根 `pierce/` 目录，每个穿刺一个子目录（如 `pierce/p1-avalonia-aot/`）。约束：不进 `src/`、不进 `NaniKore.slnx`、不被 `scripts/dev.ps1` 引用——`dev.ps1 build/test/run/publish` 的原语义不受穿刺影响。打通的部分是 P1 正式骨架的来源；P0 全部出结论后，`pierce/` 按各条结论决定去留（并入正式代码或删除）。

**（2026-10-25 主人裁定）`pierce/` 是本地工作区，已加入 `.gitignore`、不入库：** 穿刺的价值是结论而不是代码，结论全部沉淀在 `codestable/` 的 issue 与 epic spec 里；脚手架按 issue「执行痕迹」的记录随时可重建（官方模板 + 开关组 + 验证脚本都在 issue 中写明了）。这样 `avalonia` 分支上只有制度记忆与正式代码两类变更，不会被一次性产物污染。

### 穿刺计划（P0，不碰主分支）

按「不通就换方案」的风险排序。**每条都要先证明未来通路可通，再动手加厚。** 在独立分支里做（见上节），不进 `src/`。

| 序 | 穿刺 | 怎样算打通 | 不成立的退路 |
|---|---|---|---|
| 1 | **Avalonia 12 + .NET 10 + NativeAOT 精简 exe + 旁置原生 DLL**（2026-10-25 修订，放弃单文件与 StaticLink）：官方模板 + `InvariantGlobalization` + `OptimizationPreference=Size` + `StripSymbols`，`dotnet publish -r win-x64 -p:PublishAot=true` | 产出**一个精简 exe + 3 个旁置原生 DLL**（允许 `config.template.yaml` / `NOTICE`），干净环境（无 .NET 运行时）可启动并显示一个窗口；记录 exe 体积、DLL 合计、目录总体积与冷启动时间 | 原生 DLL 未被自动复制到输出 → 查 RID 资产与 `CopyToPublishDirectory`；仍不行才回退 `StaticLink.Avalonia`（届时单文件顺带可行，但非首选） |
| 2 | **YamlDotNet 静态上下文**：用现有 `AppConfig` / `AppConfigDocument` 类型图建 `StaticContext`，跑通读→写→读 | 一份真实 `.data/config.yaml` 三轮往返与迁移前逐字节等价；`config.template.yaml` 的四行档省略语义不变 | 换手写 YAML 子集解析器（已评估工作量 300–350 行，风险可控但需补一轮往返测试） |
| 3 | **ONNX Runtime 在 NativeAOT 下的加载**（审计修订：SQLite 拆出为独立穿刺 7）：解析器从 `deps/` 加载 + 显式 provider 设置 | 单文件 exe 跑通一次真实 OCR；trim 警告面可接受或已逐条处置 | OCR 依赖保持外部 `deps/`（即默认方案） |
| 4 | **LibNMeCab AOT 存活**：trim 分析 + 真实日/中注音 | AOT 发布物对固定句子的注音输出与迁移前一致 | 加保留描述符；仍不通则注音器改为「AOT 下惰性降级为纯文本」，主链路不受影响 |
| 5 | **振假名在 Avalonia 的可行形态**：`SelectableTextBlock` + `Inlines` + 两行堆叠词元 | 日语句子排版正确；三种复制格式可用；双击编辑双模可复刻；实测跨词元划选体验并记录取舍 | 划选体验不可接受 → 改「整段只读 + 右键复制三格式」，放弃跨词元原生划选（已在 Epic 001 记过的能力要显式降级，不能静默） |
| 6 | **无边框全屏浮层 + 全局热键 + 不抢焦点面板**：三个窗口层级问题一次打通 | 虚拟桌面（含负坐标）上浮层逐像素对齐冻结位图；热键注册/冲突提示/录制期注销三态正确；结果面板 `ShowActivated=false` + Win32 `WS_EX_NOACTIVATE` 不抢焦点 | 浮层坐标对不齐 → 查 Avalonia 11.1+ 的 `Screens` / 渲染缩放；不抢焦点需补 `SetWindowLong` 时序处理 |
| 7 | **SQLite native 在 NativeAOT 下的旁置加载**（2026-10-25 随交付形态修订）：验证 `e_sqlite3.dll` 作为 RID 资产随包旁置根目录时，`Microsoft.Data.Sqlite` / `SQLitePCLRaw` 的 bundle 探测在 AOT 下仍自动找到它（与今天 framework-dependent 发布物同布局，零自定义代码） | AOT 产物跑通一次收藏写入与一次跨实例读取；确认无需 `DirectPInvoke` / `NativeLibrary.Load` 定制 | bundle 的反射式自动装配在 AOT 下失效 → 显式 `SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlite3())` 或 `DirectPInvoke` + 显式加载；仍不通则给收藏加惰性降级（不影响主链路） |
| 8 | **主题与视觉保真度**（审计新增）：用 Epic 001 的视觉契约（双模原文区、划词气泡、精讲卡、状态栏）在 Avalonia 里做原型比对；默认路线 = FluentTheme + Palette 资源键覆写 + 局部 Style（裁决 12），全量移植 1060 行自定义 WPF 模板为备选 | 原型在深浅色下与 Epic 001 草图逐项人工比对通过；路线选择回写本 spec 与裁决 12 | 全量移植自定义模板（工作量与风险最高，仅当 FluentTheme 覆写达不到视觉契约时） |

**P0 的产物不是代码，是一张风险结论表**（每条：成立 / 不成立 / 退路 / 实测数字），回写进本 spec 的「当前怎么理解」，再决定 P1 是否开工。每条穿刺对应 Epic `issues/` 树内一个常规 issue（`type: feature`），一次只推进一条：出结论并回写本 spec 后才建下一条。

**P0 风险结论表（逐条累积）：**

| 序 | 穿刺 | 结论 | 关键数字 / 事实 | 对方案的影响 |
|---|---|---|---|---|
| 1 | Avalonia 12 + .NET 10 NativeAOT 精简 exe + 旁置原生 DLL | **成立**（2026-10-25，issue 001） | exe 13.53 MB + 原生 DLL 17.97 MB = 31.50 MB；exe 无 CLR 头、不引用 hostfxr；抹掉 DOTNET_ROOT/PATH 中 dotnet 后干净启动、退出码 0；ILC 警告 0；冷启动温 335–450 ms | 交付形态确认：零运行时依赖 + 免安装可行，**不需要 `StaticLink.Avalonia`、不需要自定义加载代码**；体积预算按实测校准（第 6 节）；P6 必须清理 RID 资产附带的 `.pdb` |
| 2 | **YamlDotNet 静态上下文**：`StaticContext` 覆盖 `AppConfig` / `AppConfigDocument` 类型图 | **成立**（2026-10-25，issue 002-x；升级已落地） | 10 份样本（含真实 `.data/config.yaml`：9 配置档 + `>-` 多行折叠块标量）读→写→读与迁移前**逐字节等价**；四行档省略语义、旧 `id`/未知键容忍、多行块标量/引号/折行发射全部一致；静态专用 AOT 发布 **0 警告**（含反射基线则 IL3050×2 + IL2104/IL3053，归因清晰）；16.3.0 与 18.1.0 跨版本产出 **0 差异**；主工程升级后 Debug 构建 0 警告、全量自测 **630 PASS / 0 FAIL** | 对策坐实为 `StaticDeserializerBuilder` + `[YamlStaticContext]` 上下文类（8 类型逐个显式登记，生成器不递归发现）；主人批准升级 18.1.0，`ConfigStore` 已切静态路线；P5 的 YamlDotNet 项提前完成 |
| 3 | **ONNX Runtime 在 NativeAOT 下的加载**：`SetDllImportResolver` 从 `deps/` 加载 + 显式 provider 设置 | **成立**（2026-10-25，issue 003-x） | AOT 发布 **0 警告 / 0 错误**（含链接进脚手架的 HttpClient 下载器文件，零 `NoWarn`）；摘除 NuGet 复制进根目录的原生 DLL 后，det 会话 110 ms / rec 110 ms、推理 20/15 ms；det 概率图 `[0.000000, 0.999705]`、8221/102400 像素过 thresh=0.2；rec 输出 `[1,40,18710]`，`C == 字典 18709 + blank`，CTC 解码不抛；输入敏感性 102384/102400 元素不同（计算图真实执行）；JIT vs AOT 确定性输出 **MATCH**；阴性对照（`deps/onnxruntime` 缺失）退出码 2 不走到原生加载；`--download` 运行时链路 JIT/AOT 双路线跑通 | ONNX Runtime 托管侧（1.28.0）AOT 零警告可用，`deps/` 解析机制有效，托管/原生版本配对无问题；**OCR 依赖保持外部 `deps/`（默认方案）坐实**；P6 的 onnxruntime DLL 摘除从「体积卫生」升级为**正确性要求**（根目录拷贝会遮蔽 `deps/` 下载的 DLL）；P5 接设置页下载流程时无额外 AOT 风险 |
| 4 | **LibNMeCab AOT 存活**：trim 分析 + 真实日/中注音 | **成立**（2026-10-25，issue 004-x） | NativeAOT 发布 **0 警告 / 0 错误**（`IlcTrimMetadata` 下零 IL2xxx，无需保留描述符）；exe 1.46 MB；ja/zh 真实词典（IPAdic + CC-CEDICT→MeCab，均 UTF-8）上 11 条金样本 + 原始分词 + 确定性断言全 PASS；JIT vs AOT 确定性输出 **MATCH**（逐字节等价）；真·隔离阴性对照 exit 2 不崩；`InvariantGlobalization=true` 对 MeCab 无副作用 | LibNMeCab 0.10.2 AOT 零警告可用，注音器按原样接回、不需惰性降级，阻断项 4 的退路均不触发；裁决 8 的 MeCab 部分获实测支撑（UTF-8 词典走内建编码）；四个未验证依赖收敛为 SQLite native（穿刺 7）一项 |
| 5 | **振假名在 Avalonia 的可行形态**：`SelectableTextBlock` + `Inlines` + 两行堆叠词元 | **成立**（2026-11-01，issue 005-x） | `InlineUIContainer` 在字符流中确定性占据 1 个 `U+FFFC` 字符，通过偏移量双向累加建立 O(N) 映射，三种复制格式（纯正文/带注音/纯读音）全量与子选区 13 项断言全 PASS；词元在拖选中表现为不可分割的原子单元，划选顺滑；双击切 TextBox 就地编辑与失焦恢复完好复刻；NativeAOT 发布 **0 警告 / 0 错误**，exe 13.93 MB，干净环境无运行时自测全绿 | 退路方案无需触发（无需降级为整段只读，保留原生划选）；P3（结果面板）的原型直接由 `AvaloniaRubyDocumentBuilder` 与 `AvaloniaRubyInlineControl` 承接；剪贴板接口对齐 Avalonia 12 的 `DataTransfer` |
| 6 | **无边框全屏浮层 + 全局热键 + 不抢焦点面板**：三个窗口层级问题一次打通 | **成立**（2026-11-01，issue 006-x） | 纯 Win32 BitBlt 抓屏直灌 SKBitmap（摆脱 System.Drawing）；HWND 强制对齐覆盖全虚拟桌面（含负坐标与混合 DPI），选区与物理像素 1:1 精准对齐；纯 Win32 消息专用窗口承载热键，注册/回调/1409冲突拦截/录制期注销三态全绿，0 委托分配与反射；结果面板 `ShowActivated=false` + `SetWindowPos(SWP_NOACTIVATE)` 严格保护前台焦点不抢夺，主动点击交互正常；NativeAOT 发布 **0 警告 / 0 错误**，exe 13.62 MB，干净环境无运行时自测 21 项断言全 PASS | 平台层核心服务与表现层三大硬风险全通；P2 批次直接由 `DesktopCapturer`、`SelectionOverlayWindow`、`Win32HotkeyHost` 承接，彻底扫清 WinForms/System.Drawing 移除障碍；P3 结果面板不抢焦点方案定案 |
| 7 | **SQLite native 在 NativeAOT 下的旁置加载**：`e_sqlite3.dll` 作为 RID 资产随包旁置根目录自动探测 + 收藏持久化 | **成立**（2026-11-01，issue 007-x） | NativeAOT 发布 **0 警告 / 0 错误**（零 `NoWarn`）；产物恰为 2 个文件：精简 exe 2.24 MB + 旁置 `e_sqlite3.dll` 1.89 MB = 4.13 MB，PE 无 CLR 头；标准原生探测自动发现并加载旁置 DLL，零自定义解析代码；干净环境抹除 SDK/运行时下 SqliteFavoriteService 全生命周期（惰性建库/CRUD/UTF-8多语言/Upsert/归一化去重/跨实例持久化/修改冲突拦截/删除）与 EnglishIpaDictionary 只读参数化查询等 10 模块 **51 项断言全 PASS**；`InvariantGlobalization=true` 模式兼容良好；阴性对照（缺失 DLL 时退出码 -1073740791）拦截生效 | 四大核心依赖可行性全部打通并收敛；P3 批次直接复用 `SqliteFavoriteService.cs` 与 `EnglishIpaDictionary.cs`，无需任何 AOT 适配胶水；P6 目录契约（根目录精简 exe + 4 个原生 DLL）坐实；裁决 8 全球化审计全面闭环 |
| 8 | **主题与视觉保真度**：用 Epic 001 视觉契约在 Avalonia 里做原型比对，验证 Route A 路线（FluentTheme + Palette 覆写） | **成立**（2026-11-01，issue 008-x） | NativeAOT 发布 **0 警告 / 0 错误**，exe 14.01 MB + 原生 DLL 17.97 MB = 31.97 MB；干净环境无 .NET 运行时自测 8 模块 **40 项断言全 PASS**；Route A 完美承载双模原文区、译文区、划词气泡、精讲微卡片与状态指标栏，深浅色下对比度全部超越 WCAG AAA 7:1（8.91:1 / 13.92:1 / 13.20:1）；深浅色热切换零闪烁；刻意现代化（Win11 Fluent 2 微圆角与微动效）显著提升视觉品质 | 裁决 12 定案：正式选定 Route A，**彻底否决并免除全量移植 1060 行旧 XAML 模板的庞大负担**；P1 基座工作量直接减负约 800 行；P3 结果面板原型直接就绪；**P0 八条穿刺全数圆满打通，进入 P1 授权准备态** |

### 分批推进（P1–P6，P0 通过后才授权）

| 批次 | 内容 | 完成判据 |
|---|---|---|
| **P1 基座** | TFM 改 `net10.0`（审计修订：补 `System.Security.Cryptography.ProtectedData` 与 `Microsoft.Win32.SystemEvents` 两个包——DPAPI 与主题跟随在非 Windows TFM 下不再随框架提供）；移除 `UseWPF`/`UseWindowsForms`；引 Avalonia + Fluent + SkiaSharp 3.119.x（**不需要 `StaticLink.Avalonia`**，2026-10-25 修订）；`App.axaml` + 主题资源搬迁（Palette 两张 + 控件模板路线按穿刺 8 结论）；`dev.ps1` 增加 `aot` 发布通道（Debug 构建保持 JIT，不受影响） | Debug 跑起来一个空窗口，深浅色与主题资源可解析；`dev.ps1 build/test/run` 原语义不变 |
| **P2 平台服务** | 托盘（裁决 11 定案：Avalonia `TrayIcon` + 自绘 `WS_EX_NOACTIVATE` 通知小窗替代气泡）、全局热键（Win32 消息窗口）、剪贴板网关（Win32 API）、屏幕捕获（BitBlt→SKBitmap）、PNG 编码（SkiaSharp）、选区浮层（无边框自绘窗口）、消息对话框、应用图标 | 穿刺 6 的四项全部在真实环境通过；`Overlay/SelectionGeometry.cs` 原样复用；托盘 7 个警告调用点在无窗口场景可达（阻断项 12） |
| **P3 结果面板** | `ResultPanelWindow` + `ExplanationCardView` + `SelectionBubblePopup` + `InfoBar` + 指标栏 + 振假名双模 + 划词气泡 + 异步剪贴板；SQLite 按穿刺 7 结论落地（`e_sqlite3.dll` 随包旁置根目录，收藏零下载） | 穿刺 5 通过；19 个 UI 自测中属于结果面板的部分按新引擎重写并通过 |
| **P4 设置界面** | `SettingsWindow` + 五个页面 + `HotkeyRecorderBox` + 删除 `NumberBox` 改用 `NumericUpDown` + 编译绑定改造 | 五个页面全部可达；保存/取消/尺寸记忆/版本提示/连接测试五条路径通过 |
| **P5 业务接回** | 按 P0 结论落实 AOT 对策（YamlDotNet **StaticContext 已定并已落地** / SecretStore + **OcrHttpServer** 两处 JSON + keys.bin 方案版本头 / MeCab / 资源名 / Globalization）；OCR 核心 Bitmap→Skia 边界迁移；平台层接口落地；`NaniKoreApp` 编排逻辑搬迁（含 10-24 新增的 OcrHttpServer 装配）；**ILC 警告预算逐条对账（`TreatWarningsAsErrors` 下零警告，或定向 `NoWarn` + 理由注释，策略见裁决 13）** | 全量自测全绿（审计修订后的现状基线：非 UI 88 + UI 23，含 OcrHttpServer 4 条；UI 相关按新引擎重写，数量不回退）；`--dump-prompt` / `--dump-config` / `--self-test` / `--inspect-models` / `--translate-*` / `--ocr-file [--json]` 全部 CLI 入口行为不变 |
| **P6 收尾** | AOT 发布通道接入 `dev.ps1 publish`（`-p:PublishAot=true -r win-x64`，**不设 `PublishSingleFile`**；保留现有的 onnxruntime DLL 摘除与 `.lib` 清理逻辑——摘除是**正确性要求**而非体积卫生：NuGet 复制进根目录的 `onnxruntime.dll` 会被 .NET 原生探测优先加载，使用户下载进 `deps/` 的清单哈希 DLL 形同虚设，issue 003 实测证实）；目录契约验收（根目录 = 精简 exe + 4 个随包原生 DLL + 模板 + NOTICE；免安装 ZIP 干净环境全旅程；deps/ 下载后 OCR 旅程）；体积与冷启动实测入档；`GlobalUsings.cs` 别名删除；`NOTICE` 许可条目更新**并加入发布输出**（当前 zip 并不含 NOTICE，是新增动作）；文档回写；分支合并回 `main` | 精简 exe + 旁置 DLL 在无 .NET 运行时的机器上完成「热键→框选→OCR→翻译→收藏」全旅程；关闭条件全绿并经主人确认 |

## 当前推进

**分支：** `avalonia`（裁决 7）。本 Epic 的 spec 更新与 issue 只存在于该分支；`pierce/` 穿刺脚手架是本地工作区（`.gitignore` 忽略，见「分支与合并」）；`main` 不动。

**P0 穿刺 issue（按风险排序逐条创建、逐条出结论，一次只推进一条）：**

| Issue | 穿刺 | 状态 |
|---|---|---|
| [001-x-穿刺1-avalonia12-nativeaot-精简exe与旁置原生dll](issues/001-x-穿刺1-avalonia12-nativeaot-精简exe与旁置原生dll.md) | 穿刺 1：Avalonia 12 + .NET 10 + NativeAOT 精简 exe + 旁置原生 DLL | **已关闭**（2026-10-25，结论「成立」） |
| [002-x-穿刺2-yamldotnet静态上下文与字节级往返](issues/002-x-穿刺2-yamldotnet静态上下文与字节级往返.md) | 穿刺 2：YamlDotNet 静态上下文 + 字节级往返 + 版本评估 | **已关闭**（2026-10-25，结论「成立」；升级 18.1.0 已落地 src） |
| [003-x-穿刺3-onnxruntime-nativeaot加载与真实推理](issues/003-x-穿刺3-onnxruntime-nativeaot加载与真实推理.md) | 穿刺 3：ONNX Runtime 在 NativeAOT 下从 `deps/` 加载并跑通真实推理 | **已关闭**（2026-10-25，结论「成立」；spec 已回写） |
| [004-x-穿刺4-libnmecab-nativeaot存活与日中注音等价](issues/004-x-穿刺4-libnmecab-nativeaot存活与日中注音等价.md) | 穿刺 4：LibNMeCab AOT 存活 + 日/中注音等价 | **已关闭**（2026-10-25，结论「成立」；spec 已回写） |
| [005-x-穿刺5-振假名在avalonia的可行形态](issues/005-x-穿刺5-振假名在avalonia的可行形态.md) | 穿刺 5：振假名在 Avalonia 的可行形态 | **已关闭**（2026-11-01，结论「成立」；spec 已回写） |
| [006-x-穿刺6-无边框全屏浮层全局热键与不抢焦点面板](issues/006-x-穿刺6-无边框全屏浮层全局热键与不抢焦点面板.md) | 穿刺 6：无边框全屏浮层 + 全局热键 + 不抢焦点面板 | **已关闭**（2026-11-01，结论「成立」；spec 已回写） |
| [007-x-穿刺7-sqlite-nativeaot旁置加载与持久化](issues/007-x-穿刺7-sqlite-nativeaot旁置加载与持久化.md) | 穿刺 7：SQLite native 在 NativeAOT 下的旁置加载与持久化 | **已关闭**（2026-11-01，结论「成立」；spec 已回写） |
| [008-x-穿刺8-主题与视觉保真度原型比对](issues/008-x-穿刺8-主题与视觉保真度原型比对.md) | 穿刺 8：主题与视觉保真度原型比对 | **已关闭**（2026-11-01，结论「成立」；spec 已回写） |

**P1–P6 批次 issue：** 编号在本 Epic 的 `issues/` 树内独立起算，稳扎稳打逐批推进：

| Issue | 批次 | 状态 |
|---|---|---|
| [009-x-p1-基座-tfm-avalonia依赖与主题资源](issues/009-x-p1-基座-tfm-avalonia依赖与主题资源.md) | P1 基座：TFM 改 net10.0、Avalonia 核心依赖引入、主题资源搬迁与 dev.ps1 AOT 通道 | **已关闭**（2026-11-02，结论「成立」） |
| [010-x-p2-平台服务-热键捕获浮层剪贴板托盘与通知对话框](issues/010-x-p2-平台服务-热键捕获浮层剪贴板托盘与通知对话框.md) | P2 平台服务：全局热键、屏幕捕获、选区浮层、Win32 剪贴板、托盘与通知对话框全面脱离 WinForms/WPF | **已关闭**（2026-11-02，结论「成立」） |
| [011-x-p3-结果面板-双模原文振假名精讲卡片与划词气泡](issues/011-x-p3-结果面板-双模原文振假名精讲卡片与划词气泡.md) | P3 结果面板：双模原文区、振假名排版、微结构化精讲卡片、划词气泡与异步剪贴板 | **已关闭**（2026-11-02，结论「成立」） |
| [012-x-p4-设置界面-窗口分页面板与编译绑定](issues/012-x-p4-设置界面-窗口分页面板与编译绑定.md) | P4 设置界面：窗口分页面板、编译绑定改造与平台原生数字框 | **已关闭**（2026-11-02，结论「成立」） |
| [013-x-p5-业务接回-aot对策bitmap迁移与应用编排](issues/013-x-p5-业务接回-aot对策bitmap迁移与应用编排.md) | P5 业务接回：AOT 对策、Bitmap 边界迁移与应用编排整合 | **已关闭**（2026-11-03，结论「成立」） |
| [014-x-p6-收尾-发布通道目录契约实测入档与文档回写](issues/014-x-p6-收尾-发布通道目录契约实测入档与文档回写.md) | P6 收尾：发布通道接入、目录契约验收、体积与冷启动实测入档、许可声明更新与文档回写 | **已关闭**（2026-11-03，结论「成立」） |

**节奏（裁决 17）：** P0 八条全出结论才开 P1；单条 5 个工作日时间盒，超时带「当前最优解 + 风险标注」升级主人，不无限期堵 P1。硬阻塞 = 穿刺 1/2/4/7（不成立就换方案）；穿刺 3/5/6/8 可带标注推进。

### 裁决

**已裁决（2026-10-14，主人）：**

1. **体积口径**：主交付物 = 单 exe 15–25 MB + OCR 原生库 / 注音词典外置按需下载；不内嵌、首启不解压；`publish -full` 契约不变。
2. **YamlDotNet**：按「高性能好维护」选 → `StaticContext` 源生成（无反射、改动集中在一个上下文类）；手写解析器仅为退路，不并行维护。
3. **跨平台深度**：本期只留接口，Linux/macOS 实现另开事项。
4. **DPAPI**：Windows 保留 DPAPI；`keys.bin` 加方案版本头；跨平台方案对比见第 7 节，实现另开。
5. **托盘菜单主题**：接受系统原生菜单不跟随应用深色主题；记为有界简化，升级方向 = 自绘托盘弹出窗（Avalonia `Popup` + 自绘菜单项），触发条件 = 主人认为视觉割裂不可接受
6. **`deps/` 位置与根目录口径**（2026-10-25 修订，取代原「根目录仅 exe」口径）：`deps/` 与 `.data/` 仍是仅有的两个子目录；根目录 = 精简 exe + 随包原生 DLL（`libSkiaSharp` / `libHarfBuzzSharp` / `av_libglesv2` / `e_sqlite3.dll`）+ `config.template.yaml` + `NOTICE`。主人明确偏好「exe 尽量精简、依赖拆 DLL、更新方便」胜过「根目录单独一个 exe」。
7. **分支名与合并方式**：`avalonia` + squash 回 `main`
8. **`InvariantGlobalization`**：默认开启（省约 30 MB）；P0 审计（4 文件 9 处 `CultureInfo`、`NumberBox`→`NumericUpDown` 的 `int.Parse`、MeCab/SQLite 区域依赖）后定稿。**（2026-10-25 穿刺 4 实测：MeCab 侧成立——ja/zh 词典为 UTF-8，走 .NET 内建 UTF-8 编码，与全球化模式无关；2026-11-01 穿刺 7 实测：SQLite 侧成立——SQLite 原生 C 库内部处理 UTF-8 基于字节流与内建 ASCII 规则，浮点数/ISO 日期/多语言文本与 LIKE 均不受影响；全球化审计全面通过实测支撑。）**
9.  **划选体验降级**：跨词元原生划选尽量保证，提前测试评估可行性（穿刺 5 结论后定）。
10. **UPX**：默认不用（牺牲启动速度与内存）；仅记录，不建议开。

**已裁决（2026-10-25，主人全部采纳审计建议）：**

11. **托盘警告通道**：选 (b)——保留 Avalonia `TrayIcon` 管图标与菜单，气泡替换为自绘 `WS_EX_NOACTIVATE` 通知小窗（`ABM_GETTASKBARPOS` 定位 + 定时淡出，约 +80–120 行，顺带做深浅色一致）。若 P2 批次（平台服务）发现小窗定位/焦点时序调不稳，退化到 (a) `ITrayHost` 直连 `Shell_NotifyIcon`（行为与现状逐字一致，代价约 +100–150 行 + 菜单退化）。
12. **主题路线与视觉契约权威性**：默认 FluentTheme + Palette 资源键覆写 + 局部 Style；**允许 deliberate modernization**（12.1 的 Win11 圆角等），但每条偏离必须显式记录在本 spec 的取舍清单，不允许静默漂移；穿刺 8 用原型比对验证「覆写能达到 Epic 001 视觉契约」。全量移植 1060 行自定义模板仅作覆写失败时的备选。**（2026-11-01 穿刺 8 实测定案）：正式选定默认路线 Route A（FluentTheme + Palette 语义色板覆写 + 局部 Style），正式放弃备选路线 Route B（全量移植 1060 行自定义 WPF 模板）。依据：穿刺 8 原型 40 项断言全 PASS，草地绿品牌色、分层底色与双模/卡片/气泡交互 100% 达成，WCAG AAA 对比度全部通过（8.91:1 / 13.92:1 / 13.20:1），深浅色热切换顺畅；消解 1060 行脆弱 XAML 维护包袱，P1 基座代码量骤减约 800 行。**
13. **ILC 警告预算**：定向 `NoWarn` + 白名单——集中在 csproj 一个 ItemGroup，逐条列库名/警告号/理由/复测条件；**项目自身代码的 ILC 警告零容忍全修**；白名单清单作为关闭条件的人工过审项。
14. **体积硬指标**：**记录值，不设硬指标**（主人原话「Q4 其实没那么严格」）。硬指标收敛为：零运行时依赖 + 免安装 + 解压即用 + 目录契约；exe 尽量精简（目标 ≤ 18 MB）即可，超了写明原因。UPX 与压缩内嵌继续搁置。
15. **SQLite 归宿**（同日随交付形态修订）：**不静态链接**；`e_sqlite3.dll` 随包旁置根目录（主旅程依赖，不走 `deps/` 下载），收藏零外置且无降级文案负担。
16. **OCR HTTP 服务 scope**：全量迁移（`JsonSerializerContext` + `Func<Bitmap, OcrResult>` 签名换型），不冻结不砍除——改造量小，且它是「同进程复用 OCR 给外部工具」的现成通道。
17. **推进节奏**：保持 P0 八条全出结论才开 P1（分支内允许中间态），但给 P0 设 **5 个工作日时间盒**：任一条超时未出结论，带着「当前最优解 + 风险标注」升级主人，不无限期堵 P1。硬阻塞 = 穿刺 1/2/4/7（不成立就换方案）；穿刺 3/5/6/8 可带标注推进。
18. **YamlDotNet 升级**（2026-10-25 收尾 issue 002 时裁定）：升级 16.3.0 → **18.1.0**（生成器同号），`ConfigStore` 同步切静态路线。依据：穿刺 2 实测两版静态路径跨版本产出零差异、AOT 均 0 警告，升级无行为风险；收益 = net10.0 原生 target + 上游 AOT/安全修复。**P5 的 YamlDotNet 对策项因此提前完成。**

**有界简化（记录上限、触发与方向）：**

- **托盘深色自绘渲染删除**（`TrayMenuRenderer` 约 200 行 + `TrayIconHost` 里的 DWM 圆角与反射定位）。已知上限：托盘菜单是系统原生菜单，不再跟随应用深浅色（**主人已接受**）。升级触发：主人认为视觉割裂不可接受。升级方向：自建 owner-draw 托盘弹出窗口（Avalonia `Popup` + 自绘菜单项），替换 `NativeMenu`。
  另注意：**气泡通知能力缺失是独立于菜单主题化的问题**（阻断项 12），不因本条简化而消失，按裁决 11 单独处理。
- **`NumberBox` 删除**，改用 Avalonia `NumericUpDown`。上限：若内置控件无法满足「失焦夹取 + 不写回无效值」的现有契约，需回头补薄封装。
- **跨平台只留接口不留实现**（**主人已裁决**）。上限：Linux/macOS 用户本期完全不被支持。升级触发：主人要求跨平台可用。
- **图标字体暂不替换**。上限：非 Windows 平台图标全部缺失。升级触发：跨平台进入实施。
- **原生库外置不内嵌**。上限：未下载 `deps/` 时仅 OCR 不可用（SQLite 原生随包旁置，收藏不受影响），依赖现有降级提示。升级触发：主人要求「下载一个文件即全功能」。升级方向：压缩内嵌 + 首启解压到 `.data/native/`（约 28–32 MB，已在第 6 节评估并否决）。

**Vision 同步检查（关闭时必须做）：** `codestable/vision/index.md` 正文仍描述 Go/Gio/leaf-ocr 与「跨平台不是目标」，且链接到一个已不存在的 epic。本 Epic 关闭时应重写「形态 / 依赖姿态 / 基础与边界」三节，并把「探索空间」里已被本 Epic 兑现或推翻的条目（本地 OCR 内嵌、单文件体积）更新状态。**这不是可选美化，是文档与真相冲突。**

（2026-10-25 补充基准：「形态」小节的表述基准已变——不再是「单文件可执行」，而是「免安装便携目录：精简 NativeAOT exe + 随包原生 DLL 旁置 + 按需下载 `deps/`」。关闭时按这个基准重写，并同步「探索空间」里「单文件体积」条目的状态——已从目标降级为记录值。）

---

## 关闭时要满足

- [x] P0 八条穿刺全部有结论（成立/不成立/退路）并回写本 spec。
- [x] `src/NaniKore` 内无 `System.Windows`、`System.Windows.Forms`、`System.Drawing` 引用；`GlobalUsings.cs` 的 WPF/WinForms 别名体系删除。
- [x] `./scripts/dev.ps1 test` 全量自测全绿（审计修订后的现状基线：非 UI 88 + UI 23 个测试方法、609 条断言，含 10-24 起的 OcrHttpServer 4 条；UI 相关按新引擎重写，数量不回退）。
- [x] 现有 .data/config.yaml、keys.bin、collections.db 副本在新程序上零迁移可用（人工确认）。
- [x] 全部 CLI 入口（`--self-test` / `--inspect-models` / `--build-phonetics` / `--phonetics-probe` / `--ocr-file [--json]` / `--dump-config` / `--dump-prompt` / `--translate-text` / `--translate-image` / `--settings-preview`）行为不变。
- [x] **交付物目录契约验收**：根目录 = 精简 `NaniKore.exe` + 4 个随包原生 DLL（`libSkiaSharp` / `libHarfBuzzSharp` / `av_libglesv2` / `e_sqlite3.dll`）+ `config.template.yaml` + `NOTICE`；子目录只有 `deps/` 与 `.data/`；解压即用（免安装）。
- [x] NativeAOT 产物在无 .NET 运行时的机器上完成「热键 → 框选 → OCR（deps/ 就位）→ 翻译 → 收藏」全旅程（人工确认）。
- [x] 体积与冷启动时间实测入档（裁决 14：exe 体积是记录值、不设硬指标；硬指标是零运行时依赖 + 免安装 + 解压即用 + 目录契约，超目标写明原因）。
- [ ] 本 Epic 已从开发分支合并回 `main`（主人确认后）。
- [x] `codestable/spec/index.md` 的「工具链与发布」「捕获与选区」等节已更新；Vision 三节已重写。

## 合并回 project spec 的候选

- 「工具链与发布」：NativeAOT 精简 exe + 旁置原生 DLL、目标机器零 .NET 运行时、体积口径（exe 记录值 + 目录契约硬指标）、目录契约（deps/ + .data/）、`dev.ps1` 新通道、开发分支与合并结论。
- 「捕获与选区」：渲染引擎从 GDI+/WinForms 改为 Avalonia 自绘 + Skia；捕获从 `CopyFromScreen` 改为 BitBlt→SKBitmap。
- 「翻译、结果与配置」：`keys.bin` 方案版本头；密钥存储的跨平台表述（本期仍为 DPAPI）。
- 新增一节「UI 引擎与平台边界」：Avalonia、平台层接口清单、跨平台就绪度。

## 相关材料

- `codestable/spec/index.md` — 要被改写的当前真相，动手前必读。
- `codestable/epics/001-o-学习型演进-精讲假名与生词库/spec.md` — **视觉契约与交互约束的唯一权威**（双模原文区、划词气泡、精讲卡片）。本 Epic 不重定义界面，只换承载。
- `codestable/roadmap/002-csharp-single-process-migration/spec.md` — 上一次迁移的硬约束与验收结论；其中「不迁移 WinUI 3、富文本由 WPF 承接」的取舍被本 Epic 正式取代。
- `codestable/vision/index.md` — 「形态」「依赖姿态」「密钥与数据」是本 Epic 的目标来源；正文其余部分已过期，关闭时同步。
- `codestable/notes/quick-reference.md`、`codestable/notes/phonetics-pipeline.md` — 注音与词典链路，P5 接回时对照。
- `scripts/dev.ps1` — 构建/测试/发布入口，P1 与 P6 都要改。
- `AGENTS.md` — 构建测试约定（`./scripts/dev.ps1 build|test|run|publish`）。

### 外部查证来源（2026-10-14）

本 spec 中关于 Avalonia Native AOT 的结论来自以下一手材料，非推断：

- Avalonia 官方文档《Native AOT》 — `PublishAot` / `IsAotCompatible` 配置、XAML 编译绑定要求、已知限制；12.0 前另需 `BuiltInComInteropSupport=false`。
- Avalonia 12.0 / 12.1.x release notes 与发布博客 — 版本现状（查证日 12.1.4）与 AOT 相关改进。
- Avalonia 官方文档《TrayIcon》 — `TrayIcon.Icons` / `NativeMenu` 用法与 Windows/macOS/Linux 支持矩阵。
- dotnet/sdk issue #39202《Support Include3rdPartyDLLForSelfExtract for NativeAoT》 — 证明 NativeAOT 下原生 DLL 无法自解压进单文件，只能旁置或静态链接。
- `greepar/StaticLink.Avalonia` README — Avalonia 12 ↔ SkiaSharp 4.150.1 ↔ `StaticLink.Avalonia 4.150.1-7922.1` 配对表；跟随 Avalonia 稳定版每日 CI 发版。（2026-10-25 起该路线放弃，此处备查。）
- Avalonia issue #20489 — 确认 Windows NativeAOT 的三个原生 DLL 为 `av_libglesv2.dll` / `libSkiaSharp.dll` / `libHarfBuzzSharp.dll`。
- Microsoft Learn《Deploy unpackaged WinUI apps》《Single-file EXE》《Self-contained deployment》— WinUI 3 单文件为首启解压型（`IncludeAllContentForSelfExtract`），自包含需捆绑 Windows App SDK 运行时。
- microsoft-ui-xaml discussion #8082 — WinUI 3 自 Windows App SDK 1.6 起支持 NativeAOT，「生成代码仍有未标 AOT 兼容的问题」。
- microsoft/CsWinRT `docs/aot-trimming.md` 与 dotnet/runtime issue #114179 — CsWinRT 3.0（trim/AOT first-class）尚在推进、未随 .NET 10 落地。
- YamlDotNet discussion #1063 与 Andrew Lock《Using the YamlDotNet source generator for Native AOT》 — `StaticContext` + `StaticDeserializerBuilder` 的用法与限制（不支持泛型、要求 public/internal + 无参构造）。
- ericsink/SQLitePCLRaw issue #657、Microsoft Learn《Native code interop with Native AOT》、dotnet/efcore issue #36068 — 静态库 + `DirectPInvoke` / `NativeLibrary` 是 SQLite 进单文件的现成路径，但 Microsoft.Data.Sqlite 的 NativeAOT 兼容性仍在补，需本机验证。
- komutan/NMeCab README — 「排除了转型处理与反射（new T）」的声称，是阻断项 4 的唯一依据，**仍需本机 trim 实测确认**。

体积数字（18 MB / 15 MB with UPX / 未 trim 58 MB）来自第三方仓库与 Avalonia 讨论区的实测报告，**不是本机测量**，P0 穿刺 1 必须重测。WinUI 3 自包含体积数字（WASDK 运行时约 30 MB 量级）同理为量级判断，无需实测——量级已足够否决。
