kind: issue
title: "穿刺 3：ONNX Runtime 在 NativeAOT 下从 deps/ 加载并跑通真实推理"
type: feature
status: closed
created: 2026-10-25
closed: 2026-10-25

---

## 目标

一句话：在 `pierce/p3-onnx-aot/` 用**真实的 ONNX 依赖与真实的模型推理代码**（链接 src，零漂移）做 NativeAOT 发布，证明 `Microsoft.ML.OnnxRuntime` 1.28.0 托管侧 + `OnnxRuntimeNativeLoader` 的 `deps/` 解析机制在 AOT 下能创建 det/rec 两个 `InferenceSession` 并跑通一次**真实 OCR 推理**，同时记录 ILC 警告面。

这是 Epic 002 的硬阻塞穿刺（裁决 17）：它不成立，OCR 要么换推理引擎，要么整个 AOT 方案要为 OCR 开洞。

## 范围与边界

**做：**

- `pierce/p3-onnx-aot/` 独立脚手架（不进 `src/`、不进 `NaniKore.slnx`、不被 `dev.ps1` 引用）。
- `<Compile Include>` 链接 src 的真实文件：`OcrDependencies.cs`（路径/清单/下载器/`OnnxRuntimeNativeLoader`）、`ModelLocator.cs`、`PaddleOcrModelConfig.cs`、`OnnxModelRunner.cs`、`CtcDecoder.cs`。
- `ImageTensor` 记录在脚手架内逐字镜像（src 的那份与 `TensorImageConverter` 同文件，后者依赖 `System.Drawing.Bitmap`，NativeAOT 下不可编译——Bitmap→Skia 是 P5 的事项，本穿刺只镜像这个纯数据记录）。
- 从 ModelScope 按清单哈希下载 6 个真实依赖到 `deps/`（det/rec 模型 + onnxruntime 原生 DLL），`Get-FileHash` 逐条校验。
- 合成输入张量（det 概率图 + rec CTC 输出）跑通真实推理：不经过 `TensorImageConverter` 的 Bitmap 路径，直接构造 `ImageTensor` 并按 yml 的归一化参数填充。
- NativeAOT 发布（`PublishAot` + 体积开关组，同穿刺 1/2 口径），记录 ILC 警告面；发布 exe 与 `deps/` 旁置后在隔离环境运行。

**不做：**

- 不改 `src/NaniKore` 任何文件；链接是只读引用。
- 不做 Bitmap→Skia 迁移（P5）、不碰 `OcrEngine`/`TextDetector`/`TextRecognizer` 的公开签名（它们收 `Bitmap`，AOT 下编不过）。
- 不做 SQLite native 旁置验证（那是穿刺 7）、不做 YamlDotNet（穿刺 2 已关）。
- 不做 UPX、不做压缩内嵌。

## 打通判据（来自 epic spec 穿刺 3）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | NativeAOT 发布物创建 det + rec 两个 `InferenceSession` 并跑通一次真实推理 | **成立**：det 输出 `[1,1,320,320]` 概率图 `[0.000000, 0.999705]`，8221/102400 像素过 thresh=0.2；rec 输出 `[1,40,18710]`，`C == 字典 18709 + blank`，CTC 解码不抛 |
| 2 | `onnxruntime.dll` 只存在于 `deps/onnxruntime/`（exe 同目录没有）时仍被加载——证明 `SetDllImportResolver` 机制在 AOT 下有效 | **成立**：NuGet 复制进发布根目录的原生 DLL 按 `dev.ps1` 先例摘除后，会话创建与推理全绿；阴性对照（`deps/onnxruntime` 改名）退出码 2 且不走到原生加载 |
| 3 | ILC 警告面可归因可处置（Microsoft.ML.OnnxRuntime 托管侧 vs 脚手架自身 vs 其他库） | **成立（0 条）**：AOT 发布 0 warning / 0 error——含链接进来的 HttpClient 下载器文件也无警告，无需任何 `NoWarn` |

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | `Microsoft.ML.OnnxRuntime` 1.28.0 托管侧（`InferenceSession` / `SessionOptions` / `OrtValue` / `Run`）在 NativeAOT + trim 下可用 | AOT 发布物成功建会话并推理，产出形状正确 | **成立**：两会话均创建成功，det/rec 推理与 CTC 全过 |
| R2 | `NativeLibrary.SetDllImportResolver` 在 AOT 下仍能把 `onnxruntime` 解析到 `deps/onnxruntime/onnxruntime.dll` | 原生 DLL 不在 exe 同目录也能加载成功 | **成立**：见判据 2 |
| R3 | 托管/原生版本配对（1.28.0 ↔ 清单哈希对应的 onnxruntime.dll） | 会话创建不报版本不匹配 | **成立**：无任何版本/ABI 报错 |
| R4 | 显式 provider 设置 API 在 AOT 下可达（epic spec 穿刺 3 的「显式 provider 设置」） | `AppendExecutionProvider` 类 API 可调用且不引入新警告 | **成立**：`AppendExecutionProvider_CPU()` + 建会话成功 |
| R5 | rec 模型输出类数 == yml 字典 + blank，CTC 解码路径可用 | 输出形状 `[1,T,C]` 且 `C == characters.Count + 1`，`CtcDecoder` 不抛 | **成立**：18710 == 18709 + 1 |
| R6 | `OcrDependencyDownloader`（HttpClient + SHA-256）在 AOT 下不引入额外阻断性警告 | 警告可归因到 HttpClient 且不影响 OCR 主路径 | **成立（超出预期）**：0 警告；`--download` 模式在 JIT 与 AOT 双路线均实测跑通（ModelScope 下载 + 哈希校验 + 清单复原） |

## 方案与实现安排

**为什么这样切：** 本穿刺要证伪的只有「ONNX Runtime 在 AOT 下的加载与推理」。OCR 管线的 Bitmap 入口是 P5 的迁移事项，混进来只会让失败无法归因；合成张量直接喂 `OnnxModelRunner`（真实代码）保留了全部风险面（会话创建、原生加载、OrtValue 往返、输出形状），只绕过成像边界。

**脚手架结构（`pierce/p3-onnx-aot/`，本地工作区、已 gitignore）：**

```text
Pierce3.csproj            OnnxRuntime 1.28.0（与主工程同版）；AOT 开关组；链接 src 5 文件
ImageTensor.cs            src 同名记录的逐字镜像（纯数据，无平台依赖）
Program.cs                清单校验 → 模型配置 → det 推理 → rec 推理 + CTC → provider 试验 → 输入敏感性
verify-deps.ps1           6 个 deps 文件 SHA-256 与清单逐条校验
stage-deps.ps1            把 deps/ 原样复制到 EXE 旁（发布目录与 JIT 输出目录各一次）
compare-jit-aot.ps1       JIT 与 AOT 确定性输出比对（对齐穿刺 2 的 C1 口径）
run-from-cwd.ps1          从无关工作目录运行 AOT exe（路径跟随 EXE 验证）
run-negative-control.ps1  阴性对照：deps/onnxruntime 改名后运行
deps/                     6 个真实依赖（ModelScope 下载，哈希对齐清单）
```

**比对口径：** 「真实推理」= 真实模型文件 + 真实归一化参数 + 真实 `OnnxModelRunner`/`CtcDecoder` 代码；输入张量为合成图案（三条深色横条 + 白底，按 det yml 的 mean/std 归一化），检测/识别结果本身不预设。补充**输入敏感性对照**：同一次会话对合成图案与确定性伪随机噪声的输出必须不同（102384/102400 元素不同，最大差 0.999525）——证明张量真的流过计算图。

## 执行记录

**2026-10-25 实测（本机，分支 `avalonia`）：**

1. **依赖就位**：ModelScope 下载 6 个文件（det onnx 9.4M + yml 885B；rec onnx 20.2M + yml 147K；onnxruntime.dll 15.1M + providers_shared 21.3K），`verify-deps.ps1` 逐条 SHA-256 = 清单值，**0 mismatch**（HuggingFace 源本机不可达，走 ModelScope 主源成功）。
2. **JIT 基线**（`dotnet run -c Release`）：6 节全 PASS，退出码 0，总耗时 978 ms。
3. **AOT 发布**（`dotnet publish -c Release`，开关组 = `PublishAot` + `IsAotCompatible` + `SelfContained` + `InvariantGlobalization` + `OptimizationPreference=Size` + `StripSymbols` + `IlcTrimMetadata` + `DebugType=none` + `SatelliteResourceLanguages=en`）：**0 warning / 0 error**，产物 `Pierce3.exe` 2.1 MB（最小骨架，主工程业务面另计）。
4. **AOT 运行**（发布目录 = 精简 exe + `deps/` 旁置，NuGet 复制进来的 `onnxruntime*.dll`/`.lib` 已摘除）：6 节全 PASS，退出码 0，总耗时 786 ms。从无关工作目录（`C:\Windows\Temp`）运行同样退出码 0——路径跟随 EXE。
5. **JIT vs AOT 确定性输出比对**：`compare-jit-aot.ps1` = **MATCH**（剔除计时行与绝对路径行后逐字节一致）。
6. **阴性对照**：`deps/onnxruntime` 改名后运行 → 退出码 **2**，清单级失败并逐文件列出缺失，不走到原生加载（与生产行为一致）。
7. **下载器运行时验证**：`--download` 模式删一个 yml 后重跑，JIT 与 AOT 双路线均从 ModelScope 下载成功、哈希校验通过、清单复原。
8. **主工程零影响**：穿刺只读链接 src，未改任何 src 文件；Debug 构建 0 错误 0 警告。

**关键数字：**

| 项 | JIT | AOT |
|---|---|---|
| det 会话创建（含原生 DLL 解析 + 图优化） | 167 ms | 110 ms |
| rec 会话创建 | 126 ms | 110 ms |
| det 推理（320x320） | 30 ms | 20 ms |
| rec 推理（320 宽） | 17 ms | 15 ms |
| 全流程总耗时 | 978 ms | 786 ms |

模型元数据：det `x[-1,3,-1,-1] → fetch_name_0[-1,1,-1,-1]`；rec `x[-1,3,48,-1] → fetch_name_0[-1,-1,18710]`（均为动态形状）。配置：det thresh=0.2 / box=0.45 / unclip=1.4 / limit=736 / mean=[0.485,0.456,0.406] / std=[0.229,0.224,0.225]；rec shape=[3,48,320] / max=3200 / 字典 18709 字符。

**结论：成立。** ONNX Runtime 1.28.0 托管侧在 NativeAOT 下零警告可用；`deps/` 解析机制在 AOT 下有效；托管/原生版本配对无问题。**OCR 依赖保持外部 `deps/`（默认方案）坐实。**

## 附带发现（须带入 P5/P6）

1. **NuGet 的 `Microsoft.ML.OnnxRuntime` 会把 `onnxruntime.dll` 复制进发布根目录**——不摘除的话 .NET 原生探测优先加载根目录拷贝，用户在设置页下载进 `deps/` 的清单哈希 DLL 反而不是实际加载的那个。`dev.ps1` 现有的 onnxruntime DLL 摘除逻辑因此是**正确性要求（deps/ 单一来源），不只是体积卫生**；P6 接入 AOT 发布通道时保留并加注释说明。
2. **下载器运行时路径已实测退役**：`OcrDependencyDownloader`（HttpClient + ModelScope/HuggingFace 回退 + SHA-256 校验）在 AOT 下编译零警告且运行跑通，P5 把它接进设置页时无额外 AOT 风险。
3. `OnnxModelRunner.Run` 返回 `TensorOutput`（原始维度），`TextDetector`/`TextRecognizer` 均按原始维度消费——契约一致，src 无需改动；本穿刺的 `ImageTensor` 镜像仅用于输入侧。

## 关闭结论

**判断：成立。** 三条打通判据、六条风险点全部通过，且超出两点预期：ILC 警告面为 0 条（无需任何 `NoWarn`，含链接进脚手架的 HttpClient 下载器文件）；`OcrDependencyDownloader` 的运行时链路在 JIT 与 AOT 双路线实测跑通。OCR 依赖保持外部 `deps/`（默认方案）坐实，不改选型。

**验证摘要：** ModelScope 下载 6 个真实依赖（SHA-256 与清单 0 mismatch）→ JIT 6 节全 PASS（978 ms）→ AOT 发布 0 warning / 0 error（exe 2.1 MB）→ AOT 运行 6 节全 PASS（786 ms，异工作目录亦退出码 0）→ JIT vs AOT 确定性输出 MATCH → 阴性对照（`deps/onnxruntime` 缺失）退出码 2 不走到原生加载 → `--download` 双路线跑通 → 主工程 Debug 0 错误 0 警告（穿刺零影响）。det/rec 关键数字见「执行记录」。

**回写位置（epic spec，均已落地）：**

1. 「P0 风险结论表」新增第 3 行（成立 + 关键数字 + 对方案的影响）。
2. 「AOT 阻断项与对策」第 3 行的 ONNX 半结论（含 NuGet 根目录 DLL 必须摘除的正确性说明）。
3. 「结论先行」四个依赖的未验证清单收敛为 SQLite native / LibNMeCab 两项。
4. 「当前推进」issue 表：本行更新为已关闭；下一条 = 穿刺 4。
5. P6 批次：onnxruntime DLL 摘除从「体积卫生」升级为「正确性要求」。

**遗留事项（不藏在结论里）：**

- 穿刺 4–8 按顺序逐条创建推进（下一条 = 穿刺 4：LibNMeCab AOT 存活）；硬阻塞 4 / 7 未出结论前不开 P1。
- OCR 的**真实端到端**（Bitmap 入口 → Skia 表示）属 P5 的边界迁移，本穿刺只证到 ONNX 运行时层；`OcrEngine.Recognize` 全旅程要等 P5。
- P6 接入 AOT 发布通道时，把 `dev.ps1` 现有的 onnxruntime DLL 摘除逻辑注释化为「deps/ 单一来源的正确性要求」。
- 脚手架 `pierce/p3-onnx-aot/` 按 epic 约定留在本地工作区（gitignore），不入库；结论已全部沉淀于本 issue 与 epic spec，可随时按「执行记录」重建。
