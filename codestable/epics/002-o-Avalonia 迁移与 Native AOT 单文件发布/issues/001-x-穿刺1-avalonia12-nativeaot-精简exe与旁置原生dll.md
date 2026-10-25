kind: issue
title: "穿刺 1：Avalonia 12 + .NET 10 NativeAOT 精简 exe + 旁置原生 DLL 打通"
type: feature
status: closed
created: 2026-10-25
closed: 2026-10-25

---

## 目标

一句话：在 `avalonia` 分支里用官方 Avalonia 12 + .NET 10 NativeAOT 路径产出**一个精简 exe + 3 个旁置原生 DLL**，干净环境（无 .NET 运行时）能启动并显示一个窗口，并拿到本机真实体积与冷启动时间。

这是 Epic 002 的硬阻塞穿刺（裁决 17）：它不成立，整个「零运行时依赖 + 免安装」的交付形态就要回炉。

## 结论（2026-10-25）

**穿刺 1 成立。** 官方 `avalonia.app` 12.1.4 模板 + 本机发布，产物恰为「精简 exe + 3 个旁置原生 DLL」，隔离目录里抹掉 `DOTNET_ROOT`/`PATH` 中的 dotnet 后正常起窗；exe 是纯原生 PE（无 CLR 头、不引用 hostfxr）。**不需要 `StaticLink.Avalonia`，也不需要任何自定义加载代码。**

三条需要记入 epic spec 的实测校准：

1. **体积：exe 13.53 MB（落在 12–20 MB 预期内），但旁置原生 DLL 合计 17.97 MB（原估 8–13 MB），目录总计 31.50 MB（原估 20–28 MB）**——超出约 3.5 MB，主要来自 Skia 11.1 MB 与 ANGLE 5.4 MB。按裁决 14 这是记录值不是硬指标，但 spec 第 6 节的预算表要按实测改写。
2. **SkiaSharp / HarfBuzzSharp 的原生 RID 资产会把 `.pdb`（84 MB + 21 MB）一起复制进发布目录**，`StripSymbols` 管不到。已在脚手架 csproj 加 `RemoveNativeSymbolsFromPublish` 目标删除；P6 接入发布通道必须带上这条，否则 zip 凭空多 105 MB。
3. **冷启动（本机、窗口呈现为终点）：温启动墙钟 335–450 ms，应用内自报 Main 入口→窗口呈现 170–230 ms；冷文件缓存首次启动 1,399 ms。** 应用内计时零点晚于 NativeAOT 运行时初始化，所以真实冷启动看墙钟。

## 范围与边界

**做：**

- 仓库根 `pierce/p1-avalonia-aot/` 建独立脚手架工程（不进 `src/`、不进 `NaniKore.slnx`、不被 `scripts/dev.ps1` 引用，见 epic spec「分支与合并」）。
- 按 epic spec 穿刺 1 的配置口径发布：`PublishAot` + `IsAotCompatible` + `InvariantGlobalization` + `OptimizationPreference=Size` + `StripSymbols` + `IlcTrimMetadata` + `DebugType=none` + `SatelliteResourceLanguages=en`，`dotnet publish -r win-x64`。
- 验证产物布局、启动、体积、冷启动，记录 ILC 警告面。

**不做：**

- 不碰 `src/NaniKore`、不碰 `dev.ps1`、不改解决方案。
- 不引入 `StaticLink.Avalonia`（2026-10-25 已放弃单文件路线）。
- 不做 UPX、不做压缩内嵌（裁决 10/14 搁置）。
- 不做任何业务迁移（OCR / YamlDotNet / SQLite 是穿刺 2/3/7 的事）。

## 打通判据（来自 epic spec 穿刺 1）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | 一个精简 exe + 3 个旁置原生 DLL（允许 `config.template.yaml` / `NOTICE`） | **PASS**：发布目录恰 4 个文件，无 `hostfxr`/`hostpolicy`/托管框架 DLL |
| 2 | 干净环境（无 .NET 运行时）可启动并显示一个窗口 | **PASS**：隔离目录 + `DOTNET_ROOT` 指向不存在目录 + PATH 去掉所有 dotnet 段，窗口呈现、进程存活、退出码 0 |
| 3 | 记录 exe 体积、DLL 合计、目录总体积、冷启动时间 | **已记录**（见「结论」三条校准） |
| 4 | 记录 ILC 警告清单（裁决 13 素材） | **基线 0 条**——最小脚手架的 Avalonia 12.1.4 + Fluent + Skia 在本组开关下零 ILC 警告；真实业务代码的警告面要等 P5 |

**退路未启用：** 原生 DLL 由 RID 资产自动旁置，无需 `CopyToPublishDirectory` 修补，更不需要回退 `StaticLink.Avalonia`。

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | 原生 DLL 随 RID 资产自动旁置（不写任何加载代码） | 发布目录恰为 exe + 3 个原生 DLL，无 `hostfxr`/`hostpolicy`/托管框架 DLL | **通**（附带发现：RID 资产顺带复制 105 MB `.pdb`，已加删除目标） |
| R2 | AOT 下 Avalonia 12 + Fluent 主题能真正起窗口（含 `IlcTrimMetadata` 等激进开关） | 隔离目录里启动，窗口出现且进程存活 | **通**：`windowShownMs≈170–230`、退出码 0、截图作证 |
| R3 | 体积与冷启动落在 epic spec 预期区间 | exe 12–20 MB、总量 20–28 MB 量级；冷启动有具体数字 | **部分达成**：exe 在区间内；总量 31.50 MB 超预期约 3.5 MB（记录值，非硬指标）；冷启动已量化 |
| R4 | ILC 警告面可处置 | 逐条记录库名/警告号/原因，能分辨「第三方库」与「脚手架自身代码」 | **基线成立**：脚手架自身代码刻意不用反射 JSON（`Program` 用内插字符串写日志），当前 0 条警告，归因清晰 |

## 执行痕迹

**新增文件（全在 `avalonia` 分支，不进 `main`）：**

- `pierce/README.md` — 穿刺树的定位与约束。
- `pierce/p1-avalonia-aot/Pierce1.csproj` — 官方模板结构 + AOT 开关组 + 符号清理目标。
- `pierce/p1-avalonia-aot/{App,MainWindow}.axaml(.cs)`、`Program.cs` — 对齐官方 `avalonia.app` 12.1.4 模板，只加计时埋点（`mainEntryMs` / `windowShownMs` / `exitMs` 写 `PIERCE1_LOG`）。
- `pierce/p1-avalonia-aot/verify.ps1`、`capture-window.ps1` — 验证脚本（布局 / 原生性 / 干净启动 / 截图）。

**与设计的偏差：**

1. **脚手架从「手写」改为「官方模板派生」**：手写版在 Avalonia 12 下编译不过（`InitializeComponent` 对 `Application` 不再生成、扩展方法名是 `StartWithClassicDesktopLifetime` 而非 `StartWithClassicDesktopStyleLifetime`），装 `Avalonia.Templates` 生成 `avalonia.app -av 12.1.4` 后照其结构改写。这本身是一条对 P1 有用的情报：**官方模板就是正确的工程基准**。
2. **`TreatWarningsAsErrors` 在脚手架里显式关掉**：ILC 警告面是本条穿刺要记录的证据，不当错误掐掉。
3. **截图走 Windows PowerShell 5.1 + `CopyFromScreen` 整屏截取**：pwsh 7 无 System.Drawing；`Control.FromHandle` 跨进程取不到窗口；Add-Type 在 5.1 下对嵌套类型不可靠。整屏截图对「窗口居中且唯一」的场景足够作证。

**环境备注：** 本机 .NET SDK 10.0.302（`DOTNET_ROOT=D:\E_extension\csharp\dotnet`），NuGet 全局缓存 `D:\E_extension\csharp\nuget-packages`（与 `NUGET_PACKAGES` 环境变量指向的路径不同，以 `dotnet nuget locals` 输出为准）。还原版本：Avalonia 12.1.4 / SkiaSharp 3.119.4 / HarfBuzzSharp 8.3.1.3 / Avalonia.Angle.Windows.Natives 2.1.27548。

## 验证

| 命令 / 步骤 | 结果 | 支撑 |
|---|---|---|
| `dotnet restore` | 成功（ Avalonia 12.1.4 全家） | 依赖可用性 |
| `dotnet publish -c Release` | 成功；ILC 警告 0 条 | R1/R2/R4 |
| `pwsh -File pierce\p1-avalonia-aot\verify.ps1` | 布局 PASS、原生性 PASS（无 CLR 头 / 不引用 hostfxr）、干净启动 PASS（退出码 0）、截图生成 | 判据 1/2、R1/R2 |
| 连续 3 次干净启动 | 墙钟 1,399 ms（冷文件缓存）→ 335 ms → 351 ms；应用内 `windowShownMs` 173.7 / 172.6 / 170.5 ms | 判据 3、R3 |
| 主工程 `NaniKore.exe --self-test`（既有 Debug 产物） | `All self-tests passed.` 退出码 0 | 穿刺未影响主工程 |
| `git status` | 仅 `codestable/.../spec.md` 修改 + `issues/` 与 `pierce/` 新增；`bin`/`obj`/`artifacts` 均被 gitignore | 分支卫生 |

**已知局限（诚实记录）：** 「无 .NET 运行时的机器」无法在本机直接复现——本机装着 .NET 10。可得的替代证据是：产物无任何 JIT/框架 DLL、exe 无 CLR 头且不引用 hostfxr、并在抹掉 `DOTNET_ROOT` 与 PATH 中 dotnet 的隔离目录里成功运行。真正的干净机器验收留在 P6 关闭条件。

## 关闭结论（2026-10-25，主人授权收尾）

**判断：可以关闭。** 四条打通判据全部达成（判据 3 是「已记录」性质，数字见「结论」）；范围未暗扩——没碰 `src/NaniKore`、`dev.ps1`、解决方案；验证证据齐全。

**验证摘要：** `dotnet publish -c Release` 成功且 ILC 警告 0；隔离目录 + 抹掉 `DOTNET_ROOT` 与 PATH 中 dotnet 后启动、窗口呈现、退出码 0、截图作证；三次干净启动给出冷启动区间；主工程 `--self-test` 全绿证明穿刺零影响。已知局限（本机装不了「无 .NET 运行时」的对照环境）已显式记录，真机验收留在 P6 关闭条件，不作为本条阻塞。

**回写位置（毕业）：** epic spec 的「穿刺计划 → P0 风险结论表」第 1 行、第 6 节「穿刺 1 实测校准」、「当前推进」视图。project spec 不动——AOT 还不是当前真相，等 Epic 关闭再合并。

**遗留事项：** ① `.pdb` 清理靶由 P6 正式落地（本 issue 的脚手架 csproj 是范本）；② 穿刺 2–8 按顺序逐条建 issue；③ `pierce/` 按主人裁定改为本地工作区（`.gitignore` 忽略、不入库），重建命令见「执行痕迹」。
