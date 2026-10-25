---
kind: issue
title: "穿刺 4：LibNMeCab 在 NativeAOT 下存活，日/中注音输出与迁移前逐字节等价"
type: feature
status: closed
created: 2026-10-25
closed: 2026-10-25

---

## 目标

一句话：在 `pierce/p4-mecab-aot/` 用**真实的注音标注链路**（直接链接 `src/NaniKore/Phonetics/` 的标注器 + 契约 + 路径 + 格式化，零漂移）与**仓库真实词典**（`deps/phonetics/{ja,zh}`）做 NativeAOT 发布，证明 `LibNMeCab` 0.10.2 的 `MeCabTagger.Create` / `MeCabIpaDicTagger.Create` + `Parse` 在 AOT + trim 下可用，且**对固定句子的注音输出与迁移前（JIT）逐字节等价**，同时记录 ILC 警告面。

这是 Epic 002 的硬阻塞穿刺（裁决 17）：它不成立，注音要么加保留描述符硬撑、要么降级为纯文本，主链路之外的振假名/拼音能力整体存疑。

## 范围与边界

**做：**

- `pierce/p4-mecab-aot/` 独立脚手架（不进 `src/`、不进 `NaniKore.slnx`、不被 `dev.ps1` 引用）。
- `<Compile Include>` 链接 src 真实文件：`JapaneseFuriganaAnnotator.cs`、`ChinesePinyinAnnotator.cs`、`AnnotationContracts.cs`、`AnnotatedTextFormatter.cs`、`PhoneticDependencies.cs`、`Translation/RubyTextParser.cs`——MeCab 的调用点就在这两个标注器里，其余是被它们的真实闭包。
- 以主工程同一 LogicalName 约定内嵌 `Phonetics/Data/*.txt`（停用词/常用汉字判据表），使 `EmbeddedData` 经 `typeof(AnnotationProfile).Assembly` 取到资源，金样本的过滤与生产一致。
- NativeAOT 发布（`PublishAot` + 体积开关组，与穿刺 1/2/3 同口径，`InvariantGlobalization=true`），记录 ILC 警告面。
- 词典摆到 EXE 旁（`deps/phonetics/{ja,zh}`），从无关工作目录隔离运行；JIT vs AOT 确定性逐字节比对；阴性对照（无词典）验证软降级。

**不做（穿刺阶段边界）：**

- 不改 `src/NaniKore` 任何文件；链接是只读引用。穿刺全程 src 零改动（git status 干净）。
- 不链接 `EnglishIpaAnnotator` / `EnglishIpaDictionary`（SQLite，属穿刺 7）、不碰 `TextAnnotatorRegistry` / `PhoneticDictionaryBuilder` / `PhoneticsProbe`。
- 不做 `OcrDependencies.cs` 的真实链接（会拖入 ONNX 包与 `SetDllImportResolver` 反射，污染 LibNMeCab 的 ILC 归因）——用最小垫片 `OcrDependencyPaths` 仅为让 `PhoneticDependencyPaths` 编译通过，且是 walk-up 失败才走的回落分支，本穿刺永不执行（见 `OcrDependencyPathsShim.cs` 注释）。
- 不做 Bitmap→Skia（P5）、不碰 SQLite native 旁置（穿刺 7）、不做 YamlDotNet（穿刺 2 已关）。

## 打通判据（来自 epic spec 穿刺 4）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | AOT 发布物对固定句子的注音输出与迁移前一致 | **成立**：11 条金样本 + 1 条原始分词 + 1 条确定性断言全部 PASS；JIT vs AOT 确定性证据行**逐字节 MATCH** |
| 2 | trim 分析：ILC 警告面可归因可处置 | **成立（0 条）**：NativeAOT 发布 0 warning / 0 error，无需任何 `NoWarn`，无需保留描述符 |
| 3 | 真实日/中注音（词典就绪时） | **成立**：ja 用真实 IPAdic（UTF-8 编译态），zh 用真实 CC-CEDICT→MeCab 词典，11 条金样本与主工程自测值逐条一致 |

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | `LibNMeCab` 0.10.2 托管侧在 NativeAOT + trim（`IlcTrimMetadata`）下不报 IL2xxx | `dotnet publish -p:PublishAot=true` 警告清单干净 | **成立（超出预期）**：0 警告 0 错误——上游「排除 new T 反射」的声称属实，虽未标 `IsTrimmable` 但 trim 面干净 |
| R2 | `MeCabTagger.Create` / `MeCabIpaDicTagger.Create` 在 AOT 下能创建 Tagger | `IsAvailable==true`（词典齐备时） | **成立**：ja/zh 两个标注器均创建成功 |
| R3 | 真实词典加载 + 分词正确 | 原始分词 dump 的词面/读音/feature 合理 | **成立**：ja `躊躇→ちゅうちょ`、zh `不行/就/去/银行` 四词切分，读音正确 |
| R4 | AOT vs JIT 注音输出逐字节等价 | `compare-jit-aot.ps1` 确定性行 MATCH | **成立**：见判据 1 |
| R5 | `InvariantGlobalization=true` 下 MeCab 的 `Encoding.GetEncoding` 不踩码页 | 词典加载与读音正确（不被 NotSupportedException 阻断） | **成立**：词典为 UTF-8，走内建 UTF-8 编码，与全球化模式无关；详见「附带发现」1 |
| R6 | 词典缺失时 AOT 下软降级（不崩） | 无词典运行退出码 2，不抛未处理异常 | **成立**：真·隔离阴性对照 exit 2，`IsAvailable==false`，注音返回空 |

## 方案与实现安排

**为什么这样切：** 本穿刺要证伪的只有「LibNMeCab 在 AOT 下能否加载词典、分词、产出与 JIT 一致的读音」。把两个真实标注器整条链接进来，MeCab 的创建/解析/素性访问全在真实调用路径上；判据表资源按生产 LogicalName 内嵌，过滤行为与生产一致。垫片只挡「与 MeCab 无关却会污染 ILC 归因」的 OCR 路径，且该分支运行期不执行。

**脚手架结构（`pierce/p4-mecab-aot/`，本地工作区、已 gitignore）：**

```text
Pierce4.csproj            LibNMeCab 0.10.2（与主工程同版）；AOT 开关组；链接 src 6 文件 + 内嵌判据资源
OcrDependencyPathsShim.cs 最小垫片（仅 ApplicationDependenciesDirectory，walk-up 回落用，运行期不执行）
Program.cs                词典清单 → 判据计数 → ja/zh 原始分词 → ja/zh 金样本 → 确定性断言 → 退出码
verify-deps.ps1           ja/zh 词典 SHA-256 与 PhoneticDependencyManifest 逐条校验
stage-deps.ps1            把仓库 deps/phonetics(ja,zh) 摆到 EXE 旁（模拟解压即用）
compare-jit-aot.ps1       JIT vs AOT 确定性证据行逐字节比对（对齐穿刺 2 的 C1 / 穿刺 3 口径）
run-from-cwd.ps1          从无关工作目录运行 AOT exe（路径跟随 EXE 验证）
deps/                     （EXE 旁）ja/zh 真实词典，由 stage-deps.ps1 摆入
```

**比对口径：** 金样本断言值直接取自主工程已绿的自测 `SelfTest.Phonetics.TestJapaneseFuriganaDictionary` / `TestChinesePinyinDictionary`——AOT 复现即「与迁移前一致」。所有非 ASCII 输出经 `Dump()` 转 `U+XXXX`，控制台编码无关、可比。按设计会不同的行（计时、EXE 绝对路径、AOT/JIT 身份行）在比对时整条剔除，其余每一行必须逐字节一致。

## 验证

| 命令 / 步骤 | 结果 | 支撑 |
|---|---|---|
| `dotnet build -c Release` | 0 警告 0 错误 | 链接闭包可编译 |
| `dotnet run -c Release`（JIT 基线） | **17 PASS / 0 FAIL**，退出码 0，总耗时 536 ms | 判据 1/3、R2/R3 |
| `dotnet publish -c Release -r win-x64` | **0 warning / 0 error**；exe 1.46 MB（最小骨架） | 判据 2、R1 |
| `stage-deps.ps1` + 从 `C:\Windows\Temp` 运行 AOT exe | **17 PASS / 0 FAIL**，退出码 0，409 ms；词典定位到 EXE 旁 `deps/phonetics`（非仓库） | 判据 1/3、R2–R5、隔离布局 |
| `compare-jit-aot.ps1` | **MATCH**：确定性证据行逐字节一致 | 判据 1（核心）、R4 |
| 真·隔离阴性对照（exe 复制到仓库外、无词典） | 退出码 **2**，`IsAvailable==false`，进程不崩 | R6、软降级 |
| 主工程 git status | src 零改动，工作树干净 | 穿刺零影响 |

## 执行记录

**2026-10-25 实测（本机，分支 `avalonia`）：**

1. **依赖就位**：`verify-deps.ps1` 校验仓库 `deps/phonetics/{ja,zh}` 共 8 文件 SHA-256 = `PhoneticDependencyManifest` 清单值，0 mismatch（ja sys.dic 49 MB IPAdic + zh sys.dic 9.8 MB CC-CEDICT 转译，均 UTF-8 无 BOM）。
2. **JIT 基线**：17 PASS / 0 FAIL / 退出码 0 / 536 ms。判据数据装载：ja 停用词 189 条 / 常用汉字 240 字，zh 停用词 883 条 / 常用汉字 3500 字。
3. **AOT 发布**：`PublishAot` + `InvariantGlobalization` + `OptimizationPreference=Size` + `StripSymbols` + `IlcTrimMetadata` + `DebugType=none` + `SatelliteResourceLanguages=en`，**0 warning / 0 error**，产物 `Pierce4.exe` 1.46 MB。
4. **AOT 隔离运行**：词典摆到 EXE 旁，从 `C:\Windows\Temp` 运行，17 PASS / 0 FAIL / 退出码 0 / 409 ms；`PhoneticsRootDirectory` 解析到 EXE 旁 `deps/phonetics`（walk-up 第 0 层命中，不依赖仓库）。
5. **JIT vs AOT**：`compare-jit-aot.ps1` = **MATCH**（剔除计时/路径/身份行后逐字节一致）。
6. **阴性对照**：exe 复制到仓库外临时目录、不摆词典 → 退出码 2，ja/zh 各 4 文件缺失、`IsAvailable=false`，无未处理异常。

**金样本清单（ja/zh 各，AOT 全部复现）：**

| 语言 | 输入 | 期望（= 主工程自测值） |
|---|---|---|
| ja | 躊躇した | 躊躇[ちゅうちょ]した |
| ja | 薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬。 | 含 薬[くすり]·過剰[かじょう]·摂取[せっしゅ]·致死[ちし]量[りょう]·睡眠薬[すいみんやく] |
| ja | 食べた | 食[た]べた |
| ja | 買い出しに行く | 買い出[かいだ]しに行く |
| ja | 行きます | 行[い]きます |
| ja | 日本語を勉強する日本語 | 日本語[にほんご]を勉強[べんきょう]する日本語 |
| ja | これはiPhoneです | 0 span（拉丁不认领） |
| zh | 俳佪 | 俳佪[pái huái] |
| zh | 龃龉 | 龃龉[jǔ yǔ] |
| zh | 魑魅魍魉横行 | 魑魅魍魉[chī mèi wǎng liǎng]横行 |
| zh | 不行就去银行 | 不行[bù xíng]就去银行[yín háng] |

**排查花絮（记录以免重复踩）：** 初版把 zh 期望串里的「银」误打成了繁体「銀」（U+9280 vs U+94F6），导致 `==` 假失败；标注器输出始终正确（表面字符沿用输入）。修正码点后全绿。**这不是 LibNMeCab 怪癖**，是期望串的 Unicode 不一致——金样本断言对组合/简繁体字符极敏感，P5 接回时若要加新金样本需留意。

## 附带发现（须带入 P5/P6）

1. **`InvariantGlobalization` 对 MeCab 安全（UTF-8 词典）**：LibNMeCab 经 `Encoding` 读词典字符集，本词典为 UTF-8（`PhoneticDictionaryBuilder` 全程 `utf-8` + 无 BOM），走 .NET 内建 UTF-8 编码，与全球化模式无关。这给裁决 8 的 MeCab 部分提供了实测支撑。**边界**：仅对 UTF-8 词典成立；若将来引入 Shift_JIS / EUC-JP 词典，需 `System.Text.Encoding.CodePages` provider（NativeAOT 下另需评估），届时要重测。
2. **LibNMeCab 0.10.2 无需保留描述符**：虽未标 `IsTrimmable`，trim 面 0 警告，上游「排除 new T 反射」声称属实。P5 不需要 `TrimmerRootAssembly` / `DynamicDependency`，注音器也不需要「AOT 下惰性降级为纯文本」的改造——阻断项 4 的退路都不触发。
3. **内嵌判据资源在 AOT 下正常装载**：`EmbeddedData` 的动态资源名 + `IlcTrimMetadata=true` 组合下，ja/zh 停用词与常用汉字计数与 JIT 一致（189/240、883/3500）——这是阻断项 8 的第一个正面旁证（完整验证归穿刺 8）。
4. **MeCab 无内部时序/随机状态**：同句多次 `Parse` 结果逐字节一致（确定性断言 PASS）——这是「JIT 与 AOT 输出可比」的前提，也让 P5 的金样本回归稳定。

## 关闭结论

**判断：成立。** 三条打通判据、六条风险点全部通过，且超出两点预期：ILC 警告面为 0 条（无需任何 `NoWarn` 或保留描述符）；`InvariantGlobalization=true` 对 MeCab 无副作用（UTF-8 词典走内建编码）。LibNMeCab 0.10.2 在 NativeAOT 下零警告可用，日/中注音输出与迁移前逐字节等价。

**验证摘要：** 仓库 ja/zh 词典 8 文件哈希 0 mismatch → JIT 17 PASS / 0 FAIL（536 ms）→ AOT 发布 0 warning / 0 error（exe 1.46 MB）→ AOT 隔离运行（EXE 旁词典、异工作目录）17 PASS / 0 FAIL（409 ms）→ JIT vs AOT 确定性 MATCH → 真·隔离阴性对照 exit 2 不崩 → src 零改动。

**回写位置（epic spec，均已落地）：**

1. 「P0 风险结论表」新增第 4 行（成立 + 关键数字 + 对方案的影响）。
2. 「AOT 阻断项与对策」第 4 行坐实成立（无需保留描述符/惰性降级）。
3. 「结论先行」四个未验证依赖收敛为 SQLite native 一项（穿刺 7）。
4. 「当前推进」issue 表：本行更新为已关闭；下一条 = 穿刺 5。
5. 裁决 8 的 MeCab 部分补实测支撑（见「附带发现」1）。

**遗留事项（不藏在结论里）：**

- 穿刺 5–8 按顺序逐条创建推进（下一条 = 穿刺 5：振假名在 Avalonia 的可行形态）；硬阻塞 7 未出结论前不开 P1。
- P5 接回注音时：YamlDotNet 已提前完成，LibNMeCab 无需任何 AOT 对策，按原样接回两个标注器即可；新增金样本须防简繁体/组合字符的 Unicode 不一致（见「排查花絮」）。
- 若未来引入非 UTF-8 注音词典，重测 `InvariantGlobalization` 交互（见「附带发现」1 边界）。
- 脚手架 `pierce/p4-mecab-aot/` 按 epic 约定留在本地工作区（gitignore），不入库；结论已全部沉淀于本 issue 与 epic spec，可随时按「执行记录」重建。
