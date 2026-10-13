---
kind: issue
title: "018 审查整改：资源释放、竞态收敛与文案泛化"
type: chore
status: open
created: 2026-10-12
updated: 2026-10-12
---

# 018 审查整改：资源释放、竞态收敛与文案泛化

> **读者：** 跨会话接手的人——「要做成什么、别碰什么、现状与方案是否还成立、怎么验、关了要回写哪里」。

## 做成以后是什么样

018（选取/剪贴板翻译热键）实现后的审查发现全部落地：

- GDI+ 临时位图（OCR 前的透明铺白产物、文本优先路径上的快照图像副本）在用毕即释放，不再拖到 GC 终结；
- 面板复用时旧翻译/精讲流的缓冲片段**不可能**插进新内容开头（代际守卫，覆盖 `Reset` 早于 `CancelTranslation` 的调用顺序窗口）；
- PNG 编码只有一份实现，截图链路与剪贴板图像链路共用；
- `INPUT` 结构布局的 x64 假设有运行时守护，架构变化早失败而非静默发错键；
- 热键页文案泛化：两个录制框共用同一套口径说明（F1–F12 单键、Esc 语义按槽位分流）。

外部行为不变：不新增功能、不改剪贴板终态规则、不改输入优先级。

**范围：** 仅 018 引入代码的卫生与健壮性修补 + 文案泛化。不包含「前台程序复制图像」「paste 图像分流口径」「同文本判定文案」三项待裁决设计（见文末），不包含 018 遗留项（超大图缩放、面板位置持久化、UIPI 实测）。

**归属：** 根 issues 树独立事项；整改对象是 commit `4f62c4c`（018 实现）。

## 为什么现在做 / 当前坏在哪

对 `4f62c4c` 做了逐文件审查（构建 0 警告；自测仅 2 条既有英语注音失败，与本 commit 无关，已回退纯净 HEAD 实测复现确认）。确认的问题：

| # | 问题 | 证据 | 性质 |
|---|---|---|---|
| 1 | `FlattenTransparency(image)` 的产物从未 Dispose，大剪贴板图只能等 GC 终结 | `PasteInputAcquirer.cs` OCR 分支 | 资源泄漏（潜伏） |
| 2 | 剪贴板同时含文本+图像时走文本优先分支，快照自持图像副本无人释放 | 同上，`ClipboardText` 分支 | 资源泄漏（潜伏） |
| 3 | `Reset()` 早于 `StartTranslationAsync` 内的 `CancelTranslation()`：已过取消检查、尚未派发的旧流 delta 仍能落进新内容开头 | `NaniKoreApp.PasteTranslateAsync` / `CaptureSelectAndTranslateAsync` 调用顺序；018 加的 `IsCancellationRequested` 守卫只覆盖循环顶部，覆盖不了这个窗口 | 竞态（低概率、真机可复现） |
| 4 | `EncodePng` 在 `NaniKoreApp` 与 `PasteInputAcquirer` 各一份 | 两份私有实现 | 重复实现 |
| 5 | `NativeInput` 的 `FieldOffset(8)` 是 x64 口径，无任何守护；架构不符时 `SendInput` 静默发垃圾键 | `NativeMethods.cs` | 潜伏错误 |
| 6 | 热键页待机提示与截图行描述未泛化（仍写「需要至少一个修饰键」，未提 F1–F12 单键与 Esc 解绑语义） | `HotkeyPage.xaml` / `.xaml.cs`、`HotkeyRecorderBox.BeginRecording` | 文案 |

审查中提出但**核实后不成立**、明确不做的两项（记录避免后人重复踩）：

- ~~还原用 `FormsDataObject` 未 Dispose~~：.NET 10 WinForms 的 `System.Windows.Forms.DataObject` **不实现 `IDisposable`**（实测反射接口列表：`ITypedDataObject`/`IDataObject`/`IDataObjectInternal`/`IManagedWrapper`/`IComVisibleDataObject`，无 `Dispose`），没有可调用的释放入口；内含位图由 GC 终结器回收。
- ~~`Capture` 对同一张图读两遍~~：实测真实剪贴板 `DataObject.GetData` 对位图格式**返回同一缓存实例**（两次调用 `ReferenceEquals` 为 true），并不存在双重解码；`new Bitmap(source)` 的克隆是自持副本的必需成本。

另核实：`DataObject.GetData` 的 WFDEV005 过时标记只作用于具体类方法；仓库全程经 `IDataObject` 接口调用（`Capture`/`ReadText`/`ReadImage` 的形参类型），所以构建干净，**无需**迁移 `TryGetData<T>`。

## 动哪些、验哪些

- 必须改：
  - `src/NaniKore/Input/PasteInputAcquirer.cs`（铺白产物释放、快照图像释放、`SendInputCopySimulator` 布局守护、改用共享编码）
  - `src/NaniKore/App/NaniKoreApp.cs`（代际守卫、notice 单次 SetStatus、改用共享编码）
  - `src/NaniKore/Imaging/PngEncoder.cs`（新增，共享 PNG 编码）
  - `src/NaniKore/App/Settings/HotkeyPage.xaml` / `.xaml.cs`、`src/NaniKore/App/Controls/HotkeyRecorderBox.cs`（文案泛化）
  - `src/NaniKore/Diagnostics/SelfTest.Input.cs`（补「剪贴板文本优先于共存图像」用例）
  - `config.template.yaml`（0.5.0 → 0.5.1）
- 需要验：构建 0 警告；自测全绿（除既有 2 条英语注音失败）；面板连按/复用路径手测旧片段不混入。
- 仍未知：代际守卫的真机复现路径（原竞态低概率，无法稳定构造，靠代码推理 + 现有面板复用自测覆盖）。

## 方案与实现安排

1. **资源释放**：铺白产物改 `using`；`ClipboardText` 分支显式 `DetachImage()?.Dispose()`（所有权已转移，用毕即释）。不做 `ClipboardSnapshot : IDisposable`——还原后 `_formats` 里的位图可能仍被系统剪贴板按需渲染引用，提前释放有回归风险，不值得为省一次 GC 终结冒险。
2. **代际守卫**：`NaniKoreApp` 增加 `_translationGeneration`（long，`Interlocked`）；`CancelTranslation()` 递增；翻译/精讲两条流在启动时捕获代数，循环顶部、`Dispatcher.InvokeAsync` 回调内、完成块三处比对，代数不符即静默舍弃。与既有 `ReferenceEquals(_translationCancellation, …)` finally 守卫互补，不冲突。
3. **共享编码**：新增 `NaniKore.Imaging.PngEncoder.Encode(Bitmap)`，两处调用点改为共用，删除两份私有实现。
4. **布局守护**：`SendInputCopySimulator` 静态构造里校验 `Marshal.SizeOf<NativeInput>() == 40` 且联合体偏移为 8，不符抛 `NotSupportedException`（x64 部署下永不触发，是给未来架构移植的早失败保险）。
5. **文案泛化**：待机提示两个框共用一套口径；截图行描述补 F1–F12 单键；`BeginRecording` 的状态文案按 `AllowUnbind` 分流（选取框提示 Esc 解绑，截图框提示 Esc 取消）。

## 验证

- `pwsh scripts/dev.ps1 build`：0 警告 0 错误（项目 `TreatWarningsAsErrors=true`）。
- `pwsh scripts/dev.ps1 test`：新增用例通过；总失败数保持既有 2 条（英语注音，与本事项无关）。
- 手测：连续按两次 paste 热键（第一次取选区文本、第二次立即触发），确认第二次面板开头不出现第一次的残留译文片段；截图链路同样复测一次。

## 待主人裁决（grilling，未定不动手）

（本轮两条均已裁决并落地，见第三轮执行记录；暂无未决项。）

## 执行记录

2026-10-12 实现完毕，构建 0 警告，自测仅剩既有 2 条英语注音失败。

**已落地：**

1. 铺白产物 `using` 即释（`PasteInputAcquirer` OCR 分支）；
2. `ClipboardText` 分支显式释放快照图像副本（文本优先，克隆无人再用）；
3. 代际守卫：`_translationGeneration`（long + Interlocked），`CancelTranslation` 递增，翻译/精讲两流的循环顶部、`Dispatcher.InvokeAsync` 回调、完成块三处比对，迟到派发自我否决；
4. 新增 `NaniKore.Imaging.PngEncoder`，截图与剪贴板图像两链路共用，两份私有 `EncodePng` 删除；
5. `SendInputCopySimulator` 静态构造校验 INPUT 布局（40 字节 / 联合体偏移 8），不符抛 `NotSupportedException`；
6. 热键页文案泛化（待机提示、截图行描述补 F1–F12 单键、`BeginRecording` 状态文案按 `AllowUnbind` 分流）；
7. notice 与「正在连接翻译服务…」合并为一次 `SetStatus`；
8. 新增自测：剪贴板文本优先于共存图像（OCR 委托直接抛异常证明不被调用）、INPUT 布局守护本机成立。

**方案偏差与中途发现（都比原方案更重要，记录在案）：**

- **铺白位图的所有权契约是被测试逼出来的**：原 4b 用例在 `Acquire` 返回后仍持有铺白位图做断言，与「用毕即释」直接冲突（自测当场以 `ObjectDisposedException` 失败）。据此在 `PasteInputRequest.Recognize` 上写明「位图仅在调用期间有效」，用例改为在识别回调内取事实（格式 + 角像素 ARGB），不留引用。运行期委托 `bitmap => GetOcrEngine().Recognize(bitmap)` 同步完成且 `OcrEngine.Recognize` 只克隆裁剪、不留引用，契约成立。
- **`ClipboardSnapshot : IDisposable` 评估后不做**：还原后 `_formats` 里的位图可能仍被系统剪贴板按需渲染持有引用，提前释放有回归风险；只释放确定无人引用的快照图像克隆，其余交 GC 终结器。
- **审查中两项「问题」核实后不成立**（详见上节）：`DataObject` 无 `IDisposable`；真实剪贴板 `GetData` 对位图返回同一缓存实例，不存在双重解码。

**验证：** `dev.ps1 build` 0 警告；`dev.ps1 test` 新增 3 条用例全过，总失败数与整改前一致（2 条英语注音，与本事项无关）。代际守卫属低概率竞态、无法稳定构造，未加单测，靠代码推理 + 既有面板复用自测覆盖；建议真机按 issue「验证」节手测一遍。

## 第二轮执行记录（2026-10-12，Q2/Q3 裁决落地）

**裁决：** Q2 改为「mode 优先 + 能力不符弹提醒，不静默回落」；Q3 保持现状（同文本时显示「已读取剪贴板文本」，仅文案差异、下发结果一致）。

**实现：**

1. 新增 `Input/ClipboardImageRoute.cs`：`ClipboardImageRoute` 三态枚举（`DirectFeed` / `LocalOcr` / `UnsupportedVision`）+ `ClipboardImageRouteResolver.Resolve(pipelineMode, supportsVision)`。口径：`vision_oneshot` + 支持视觉 → 直喂；`vision_oneshot` + 不支持视觉 → 弹提醒；其余（含未知管线值）→ 本地 OCR（保守，与截图链路一致）。
2. `PasteInputRequest` 的 `bool SupportsVision` 换成 `ClipboardImageRoute ImageRoute`；`PasteInputAcquirer` 图像分支三分支；新增 `PasteInputKind.VisionUnsupported`。
3. `NaniKoreApp.PasteTranslateAsync`：路由经 resolver 解析；`VisionUnsupported` 时弹 `MessageDialog`（当前配置档吃不下图像 + 两条出路：换配置档 / 改 local_ocr），叠加还原失败异状时一并带上；不触碰面板与 `_lastImagePng`。
4. 自测：新增路由矩阵用例（含大小写与未知值）与「能力不符回报宿主」用例（OCR 委托直接抛异常证明不被调用）。

**未做（等裁决）：** 无。

## 第三轮执行记录（2026-10-12，Q1/Q2 裁决落地）

**裁决：**

1. **Q1（是）**：轮询基准从「只读文本」改为「剪贴板序列号」，并支持选区图像。前台程序响应 Ctrl+C 复制的是图时，不再误判「没选中」，也不再被还原操作冲掉。
2. **Q2（口径按来路分流）**：OCR 写回不再一律替换——**程序自己 Ctrl+C 复制的图**（选区图像）识别后原文直接替换；**主人手动复制的图**（剪贴板图像）原图必须保住，原文以「图像 + 文本」附加写回。

**探针实测（全部本机验证，探针已删）：**

- 序列号对任何写入都 +1：文本（含与现有内容一模一样的文本）、图像、文件引用、空文本、清空；只读不 +1。
- 读序列号不打开剪贴板：另一线程狂写 200 次期间连读零异常（对比读文本/读图在并发时会抛 `ExternalException`）。
- 各回应形态：文本 `[UnicodeText,System.String,Text]`；图像 `[Bitmap,System.Drawing.Bitmap]`；文本+图 `[UnicodeText,Bitmap,…]`；原生空文本 `[UnicodeText,Locale,Text,OEMText]` 值为 `""`；文件引用 `[FileDrop,FileNameW,FileName]`；私有格式 `[SomePrivateFormat]`。
- `Clipboard.SetText("")` 在 .NET 10 直接抛 `ArgumentNullException`，空串场景只能原生写入（自测仍按网关层模拟）。

**实现：**

1. `IClipboardGateway` 新增 `ReadSequenceNumber()` 与 `SetDataObject(object, bool)`；移除已无调用方的 `ReadCurrentText()`（状态机改看序列号 + 快照，轮询循环里的快照读取自带 `ExternalException` 重试）。
2. `PasteInputAcquirer.Acquire`：快照后读序列号基准；`TakeResponse()` 统一取「前台对 Ctrl+C 的回应」——非空文本 → 选区文本（与原有文本相同也判定为新复制，Q3 文案歧义自然消失）；有图像 → 选区图像走 `TranslateImage`；两者皆无 → 按「空文本/空剪贴板还原兜底，文件引用/私有格式原样保留并提示」收尾（`LeftClipboardEmpty` 按格式名集合判定，文本类格式表来自实测）。**收工前再抓一次回应**：复制可能正好落在最后一次轮询的睡眠里，漏抓就会被还原冲掉。
3. 图像处理上收为 `TranslateImage(image, source, notice, sequenceAtRead, programOwnCopy)`：选区图像与剪贴板图像共用同一路由；OCR 写回前用 `sequenceAtRead` 校验剪贴板未被用户动过（OCR 要跑几百毫秒，绝不覆盖期间新复制的内容）；写回口径按来路分流（替换 / 追加）。
4. 新增 `PasteInputKind.SelectionImage`；宿主状态文案按选区/剪贴板分别措辞。
5. 自测新增 9 条：相同文本仍判选区、选区图像×三条去向、追加写回保住原图、OCR 期间剪贴板变化的写回拦截、文件引用原样保留、复制落在最后一次轮询睡眠里仍被抓到。

**验证：** 构建 0 警告；自测全绿（既有 2 条英语注音失败不变）。真机需按「Office/浏览器选中图像按 Ctrl+Shift+S」实测一次。

> **排查提示（2026-10-12 补）**：自测套件存在一条既有 flake——真实剪贴板 round-trip 偶发失败（形态 `formats=[UnicodeText, System.Drawing.Bitmap, NaniKore.SelfTest]`），与本事项无关，已立项 `codestable/issues/021-o-clipboard-roundtrip-flake.md`。红跑时先对照是否为该形态，别误判成本事项回归。

## 关闭时

- 回写到 project spec 的候选：若三项裁决改动行为，在「翻译、结果与配置」补选取/剪贴板热键的行为口径；否则无 spec 变化（纯卫生整改）。
- 关闭判断与验证摘要：
- 遗留：三项待裁决；018 既有遗留（超大剪贴板图缩放、面板位置不持久化、UIPI/提权窗口实测、`OleSetClipboard` 数据所有权真机确认）。
