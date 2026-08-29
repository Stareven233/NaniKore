---
kind: design
status: draft
created: 2026-08-08
phase: 3
roadmap: 002-csharp-single-process-migration
---

# 阶段 3 翻译：设计方案

本文是阶段 3 的实施依据，只描述设计与决策，不含代码实现。上游约束见
`codestable/roadmap/002-csharp-single-process-migration/spec.md`，产品语言见 `codestable/vision/index.md`。

## 1. 目标与非目标

**目标**：把「选区 -> 本地 OCR -> 云端 LLM 翻译」打通成流式闭环；同时交付托盘、配置面板与
`config.yaml` 持久化，让 provider 可由用户自由配置。

**非目标**（留给后续阶段）：精讲面板的富文本排版、GGUF 本地推理运行时、剪贴板第二热键、
译文原位覆盖。设计上为前两者预留接口，但本阶段不实现。

## 2. 已确认决策

| # | 决策 | 结论 |
|---|---|---|
| 1 | 配置格式 | YAML，引入 `YamlDotNet` |
| 2 | OpenAI API 形式 | `completions` 与 `responses` 都支持，用户选，默认 `completions` |
| 3 | API Key 存储 | DPAPI 加密落 `%APPDATA%\NaniKore\`，`config.yaml` 无 key 字段 |
| 4 | 翻译触发 | `auto_translate` 开关，默认关，手动按钮触发 |
| 5 | 流式输出 | 第一版即流式 |
| 6 | 结果面板 | 单窗口纵向三段（原文/译文/精讲），总高封顶，段内滚动 |
| 7 | 设置面板 | Tab 按功能域分；provider 在 Tab 内以配置档列表切换，切换不清除其他档的 key |
| 8 | 配置位置 | exe 旁边（与 `deps/` 同级） |
| 9 | 多模态一条龙 | 契约层预留：请求带图像载荷，增量带频道 |
| 10 | WinUI 3 | 不迁移，见第 12 节 |
| 11 | 面板模式 | 单实例非模态 + `WS_EX_NOACTIVATE`，趁早改 |
| 12 | 能力失败 | 必须把失败原因告知用户，见第 7 节 |
| 13 | 配置写盘 | 只在用户点保存时写；不做实时同步，不做 `FileSystemWatcher` 回读 |
| 14 | 托盘图标 | 先放一个临时 `.ico`，主人后续自行替换 |
| 15 | 诊断命令 | 加无 UI 的翻译诊断命令 |

## 3. 目录结构

新增区域叠加在现有代码之上，OCR 核心不动：

```text
src/NaniKore/
├── Config/
│   ├── AppConfig.cs            配置 POCO 树（含 version 字段）
│   ├── ConfigStore.cs          加载、默认值生成、原子写盘
│   └── SecretStore.cs          DPAPI 加解密 API Key
├── Translation/
│   ├── UnderstandContracts.cs  请求/增量/能力枚举
│   ├── ITranslationProvider.cs provider 契约
│   ├── ProviderFactory.cs      按配置档构造 provider
│   ├── PromptTemplates.cs      内置翻译/精讲模板与哨兵标记
│   ├── SseReader.cs            共用的 SSE 行解析
│   └── Providers/
│       ├── OpenAiProvider.cs   completions + responses 两种形式
│       ├── ClaudeProvider.cs
│       └── GeminiProvider.cs
├── App/
│   ├── TrayIconHost.cs         NotifyIcon 与右键菜单
│   ├── ResultPanelForm.cs      三段结果面板（替换 OcrResultForm）
│   ├── Settings/
│   │   ├── SettingsForm.cs     Tab 宿主
│   │   ├── ISettingsPage.cs    Tab 页约定
│   │   ├── GeneralPage.cs
│   │   ├── TranslationPage.cs
│   │   └── AboutPage.cs
│   └── NaniKoreApplicationContext.cs   扩展：托盘、配置、面板生命周期
└── Assets/nanikore.ico         临时图标，可替换
```

## 4. Provider 契约

契约的两个关键选择：**输入是载荷而非字符串**，**输出是带频道的增量而非单条文本流**。
这样视觉一条龙与后续精讲都不需要改接口。

```csharp
// 一次理解请求：可只带文本、只带图像，或两者都带
internal sealed record UnderstandRequest(
    UnderstandKind Kind,              // Translate | Explain
    string? SourceText,
    ReadOnlyMemory<byte>? ImagePng,   // 视觉一条龙时传选区 PNG
    string TargetLanguage);

// 增量带频道，面板据此决定写入哪一段
internal enum DeltaChannel { SourceText, Translation, Explanation }

internal readonly record struct UnderstandDelta(DeltaChannel Channel, string Text);

[Flags]
internal enum ProviderCapabilities { None = 0, Text = 1, Vision = 2, Streaming = 4 }

internal interface ITranslationProvider : IDisposable
{
    string Id { get; }
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<UnderstandDelta> StreamAsync(
        UnderstandRequest request,
        CancellationToken cancellationToken);
}
```

实现约定：

- 每个配置档一个 `HttpClient`，由 `ProviderFactory` 缓存复用，避免 socket 耗尽。
- 三家的差异只在「请求体形状」与「SSE 增量取值路径」，SSE 分帧逻辑由 `SseReader` 共用。
- 取消由 `CancellationToken` 贯穿；面板关闭或用户重译立即取消上一条流。
- provider 不写日志、不回显 key 与原文内容（保密性约束）。

各家增量取值路径：

| kind | 端点 | 流式增量字段 |
|---|---|---|
| openai / completions | `POST {base_url}/chat/completions` | `choices[0].delta.content` |
| openai / responses | `POST {base_url}/responses` | `response.output_text.delta` 事件 |
| claude | `POST {base_url}/v1/messages` | `content_block_delta.delta.text` |
| gemini | `POST {base_url}/models/{model}:streamGenerateContent?alt=sse` | `candidates[0].content.parts[0].text` |

## 5. 两种流水线模式

由 `pipeline.mode` 切换，面板与热键路径完全一致：

- `local_ocr`（默认）：选区 -> PP-OCRv6 -> 原文段填充 -> provider 只回 `Translation` 频道。
- `vision_oneshot`：选区编码为 PNG 直接交给视觉模型，模型同时产出原文与译文，
  面板两段一起流式填充；本地 OCR 不加载。

一条龙的分段用 prompt 哨兵标记（`<<<SOURCE>>>` / `<<<TRANSLATION>>>`），流里跑一个小状态机切频道。
不用 JSON：JSON 流式解析一旦坏掉整块面板空白，标记坏掉最多是分段错位。这与 talk 里精讲的同一条判断一致。

## 6. 配置文件

`config.yaml` 位于 exe 旁边，缺失时由程序生成默认内容。

```yaml
version: 1

general:
  auto_translate: false
  target_language: zh-CN

pipeline:
  mode: local_ocr            # local_ocr | vision_oneshot
  active_profile: openai-default

profiles:
  - id: openai-default
    kind: openai             # openai | claude | gemini（后续 gguf）
    api_type: completions    # completions | responses，仅 openai 有效
    base_url: https://api.openai.com/v1
    model: gpt-4o-mini
    supports_vision: false   # 用户可覆盖的能力声明，见第 7 节
    timeout_seconds: 60
  - id: claude-default
    kind: claude
    base_url: https://api.anthropic.com
    model: claude-haiku-4-5
    supports_vision: true
  - id: gemini-default
    kind: gemini
    base_url: https://generativelanguage.googleapis.com/v1beta
    model: gemini-2.0-flash
    supports_vision: true

hotkeys:
  capture: Ctrl+Shift+A
  translate_clipboard: ''    # 预留

prompts:
  translate: ''              # 空则用内置模板
  explain: ''
```

**读写策略（决策 13）**：

- 启动时读一次，之后内存中的配置即唯一真相；**程序运行期间以程序为准**。
- 只在用户点「保存」时写盘。防抖仅用于合并连点保存，不做编辑期实时同步。
- 不装 `FileSystemWatcher`。手动编辑 `config.yaml` 只在程序未运行时有效。
- 写盘为原子替换：写临时文件 -> `File.Move(overwrite: true)`，避免中途崩溃留下半个文件。
- `version` 为将来的迁移留口；读到未知高版本时提示用户而不是静默丢字段。
- 未知字段与非法值不致命：回落到默认值并在设置面板顶部显示一条提示。

## 7. 能力检测与失败通知

并非所有模型都有视觉能力，也不是所有端点都支持 SSE。原因必须让用户看懂，这是本节的全部目的。

**三道关卡**：

1. **保存设置时**：若 `mode = vision_oneshot` 但当前配置档 `supports_vision = false`，
   保存按钮旁直接给出阻塞式提示，不让用户配出一个必然失败的组合。
2. **发请求前**：`ProviderFactory` 比对 `UnderstandRequest` 与 `ProviderCapabilities`，
   不匹配时抛 `ProviderCapabilityException`，面板显示「该配置档未声明图像输入能力，
   请改用 local_ocr 模式或在设置里勾选此模型支持图像」。
3. **HTTP 响应**：把状态码与错误体翻译成人话，而不是抛原始异常。

**错误映射**：

| 情况 | 面板提示要点 |
|---|---|
| 401 / 403 | API Key 无效或无权限，指向设置面板 |
| 404 | 模型名或 base_url 不对，回显当前配置档的两个值 |
| 429 | 触发限流，建议稍后重试或换配置档 |
| 400 且错误体提到 image/vision/multimodal | 该模型不支持图像输入，建议切回 `local_ocr` |
| 400 其他 | 回显服务端 message 原文 |
| 超时 / 网络不可达 | 区分「连不上」与「连上但超时」，后者提示调大 `timeout_seconds` |
| 流中断 | 保留已收到的部分译文，状态栏标注「输出被截断」 |

`supports_vision` 的判断不靠内置模型名单猜测——模型名变化太快，猜错反而挡住用户。
做法是：新建配置档时按 `kind` 给一个保守默认值，用户可在设置面板勾选覆盖。

「测试连接」按钮发一次极短的非流式请求，把上述映射后的结果直接显示在面板里。

## 8. 密钥存储

- 路径 `%APPDATA%\NaniKore\secrets\<profile-id>.bin`，`ProtectedData` + `DataProtectionScope.CurrentUser`。
- 按配置档 id 独立存放，切换配置档不清除其他档的 key（决策 7）。
- 删除配置档时同步删除对应密钥文件。
- 设置面板输入框用 `UseSystemPasswordChar`；已存在的 key 显示为占位符，不回读明文到 UI。
- key 不进日志、不进异常消息、不进 `config.yaml`。

## 9. 结果面板

单实例非模态窗口，`WS_EX_NOACTIVATE` 不抢焦点（决策 11），对应 vision 的「不打断」原则。

```text
┌─ NaniKore ─────────────────────────── ✕ ─┐
│ 原文（可编辑）                     ✎ ⟳    │
│ ここは通れないよ                     ▲▼   │
├═══════════════ 可拖动分隔 ════════════════┤
│ 译文（流式追加）                     ▲▼   │
│ 这里过不去哦                              │
├═══════════════ 可拖动分隔 ════════════════┤
│ 精讲（默认折叠，后续阶段启用）             │
├───────────────────────────────────────────┤
│ openai-default · gpt-4o-mini · 1.2s       │
│                     [复制][翻译][精讲]     │
└───────────────────────────────────────────┘
```

结构与行为：

- 嵌套 `SplitContainer`：外层拆「原文 / 下半」，内层拆「译文 / 精讲」。
  精讲未启用时 `Panel2Collapsed = true`，后续加入不改结构。
- 每段是 `Multiline + ScrollBars.Vertical` 的 TextBox，段内独立滚动。
- 总高度封顶为当前屏工作区高度的 60%，同时设 `MaximumSize`，避免长文本把窗口顶出屏幕。
- 原文可编辑，`⟳` 重译：取消上一条流后用编辑后的原文重发。
- 流式增量经 `Control.BeginInvoke` 回 UI 线程追加；按频道路由到对应段。
- 单实例：再次热键复用同一窗口并重置内容，不叠窗。窗口关闭即取消进行中的请求。
- 不抢焦点带来的取舍：需要显式点击面板才能编辑原文，这是「不打断」的必要代价。

替换现有 `OcrResultForm`；`OcrProgressForm` 保留，首次加载模型仍需要它。
`NaniKoreApplicationContext` 的 `_selecting` 重入标记随之调整：模态等待消失后，
重入保护改为「选区浮层存在期间」加「面板正在接收流期间可被新请求抢占」。

## 10. 设置面板

平时不显示，从托盘菜单或面板上的配置按钮唤出；单实例，非模态。

```text
[通用] [翻译] [热键] [高级] [关于]
          ↑
   ┌ 翻译 Tab ──────────────────────────────────┐
   │ 配置档: [openai-default ▾] [+][-][复制]     │
   │ 类型:   ( ) OpenAI  ( ) Claude  ( ) Gemini  │
   │ API 形式: [Chat Completions ▾]  ← 仅 OpenAI │
   │ Base URL: [https://api.openai.com/v1     ]  │
   │ 模型:     [gpt-4o-mini                   ]  │
   │ API Key:  [••••••••••] [测试连接]           │
   │ [x] 此模型支持图像输入                       │
   │ ──────────────────────────────────────      │
   │ 目标语言: [中文 ▾]  [x] OCR 后自动翻译       │
   └─────────────────────────────────────────────┘
                          [保存] [取消]
```

- Tab 只按功能域分，不按厂商分（决策 7）。
- 配置档列表而非固定三槽：可同时留着 OpenAI 官方、DeepSeek、本地 llama-server；
  将来加 GGUF 只是多一种 `kind`。
- 扩展空间：Tab 页实现 `ISettingsPage`（`Title` / `Load(AppConfig)` / `Validate()` / `Apply(AppConfig)`），
  新增页只往列表里加一项，不碰宿主窗口。
- 「取消」丢弃全部未保存修改；「保存」通过校验后一次性落盘并热应用（含重新注册热键）。
- 热键 Tab 修改后立即尝试 `RegisterHotKey`，冲突时把错误显示给用户而不是吞掉。

## 11. 托盘

- `NotifyIcon` 挂在 `NaniKoreApplicationContext` 上，右键菜单：`截图翻译` / `设置` / `退出`。
- 双击托盘图标唤出设置面板。
- 图标 `src/NaniKore/Assets/nanikore.ico` 作为资源嵌入，先放临时图（决策 14）。
- 退出路径统一走 `ExitThread()`，确保 provider、OCR 会话与热键窗口都被释放。

## 12. WinUI 3 评估

**结论：可行但不必要，现在迁移是净亏。**

不利的三点都打在这个项目的要害上：

- **体积与运行时**：WinUI 3 需要 Windows App SDK。framework-dependent 要求用户装
  WindowsAppRuntime，self-contained 会额外拖进大量原生文件。当前整包约 40 MiB、
  只依赖 .NET 10 Desktop Runtime，vision 里「不捆运行时」这条会破。
- **DPI 与像素**：WinUI 3 全程 DIP，需靠 `RasterizationScale` 换算物理像素。
  而「选区物理像素零偏移」正是本项目唯一栽过跟头的地方，换栈等于把已收敛的风险重新打开。
- **托盘与位图**：无内置 NotifyIcon，需第三方或 Win32 interop；整张虚拟桌面位图要从
  GDI `Bitmap` 转 `SoftwareBitmapSource` 才能显示，4K 多屏下多一次大拷贝。

能赚到的只有精讲面板的富文本排版与 Fluent 观感，而这笔账有更便宜的付法：**WPF**。
它随 .NET Desktop Runtime 内置（零额外体积）、支持逐像素透明（浮卡圆角阴影用得上）、
排版与绑定强于 WinForms，且能与 WinForms 共存于同一进程、同一 STA 消息循环——
可以只把结果面板换成 WPF，热键、捕获、选区浮层一个字都不动。

**路线**：阶段 3 继续 WinForms 打通翻译闭环；等精讲面板真长成富文档、WinForms 明显吃力时，
先换 WPF 面板；WinUI 3 只在确定要做 MSIX 打包分发时重新评估。

上述体积判断来自经验而非本机实测，未在本环境跑过 Windows App SDK 发布。
若需确凿依据，可另做一次最小 WinUI 3 空壳发布来量。

## 13. 诊断命令

延续现有 `--ocr-file` 风格，调 provider 时不必真按热键：

```powershell
# 用当前活动配置档翻译一段文本，流式打印到控制台
NaniKore --translate-text "ここは通れないよ"

# 指定配置档
NaniKore --translate-text "..." --profile claude-default

# 视觉一条龙：整图直接交给多模态模型
NaniKore --translate-image path\to\image.png

# 打印生效配置来源与各配置档密钥是否就位（不回显 key）
NaniKore --dump-config
```

## 14. 实施顺序

1. `Config` + `SecretStore`：POCO、默认生成、原子写盘、DPAPI，配 `--dump-config`。
2. `Translation` 契约 + `OpenAiProvider`（completions）+ `SseReader`，配 `--translate-text`。
3. 结果面板改造：非模态、`WS_EX_NOACTIVATE`、三段布局、流式追加、重译。
4. 托盘 + 设置面板 + 热键热应用。
5. `ClaudeProvider`、`GeminiProvider`、OpenAI `responses` 形式。
6. `vision_oneshot` 模式与 `--translate-image`。
7. 错误映射与「测试连接」打磨，补自测项。

每批结束跑 `scripts/dev.ps1 build` 与 `test`，保持 0 warning / 0 error。

## 15. 验收标准

机器可验证：

- Debug/Release 构建 0 warning、0 error（`TreatWarningsAsErrors` 已开启）。
- 自测新增项：YAML 往返一致、未知字段回落默认、SSE 分帧（含跨包切断的 `data:` 行）、
  哨兵标记状态机分频道、能力不匹配抛出、错误映射文案选择。
- `--dump-config` 不出现任何 key 明文。
- `--translate-text` 对真实端点流式返回译文。
- 发布产物 `--self-test` 通过。

需要主人真机验收：

- 托盘唤出设置、改 provider、保存、立即生效。
- 结果面板不抢焦点：游戏或视频全屏下弹出，原窗口不失焦。
- 三段各自滚动，长文本不把窗口顶出屏幕。
- 故意填错 key、错模型名、错 base_url，确认提示能看懂。

## 16. 风险与未验证项

- `YamlDotNet` 尚未在本地 NuGet 缓存里，首次 restore 需联网。
- `WS_EX_NOACTIVATE` 与 WinForms TextBox 编辑的交互需实机确认：不抢焦点的窗口
  在某些前台窗口下点击行为不一致，可能要配合 `SetForegroundWindow` 的显式激活按钮。
- OpenAI `responses` 流式事件名以经验为准，实施时以官方文档核对后再定。
- 各家 SSE 心跳与错误帧格式不统一，`SseReader` 需按实测补容错。
- 精讲频道本阶段只留通路不实现，`Explanation` 段保持折叠。