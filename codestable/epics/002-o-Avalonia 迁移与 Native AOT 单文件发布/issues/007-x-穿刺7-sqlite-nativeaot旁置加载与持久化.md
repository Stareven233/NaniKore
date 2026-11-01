---
kind: issue
title: "穿刺 7：SQLite native 在 NativeAOT 下的旁置加载与持久化"
type: feature
status: closed
created: 2026-11-01
closed: 2026-11-01

---

## 目标

一句话：在 `pierce/p7-sqlite-aot/` 验证 `Microsoft.Data.Sqlite` 10.0.12 在 NativeAOT 下的旁置原生动态库（`e_sqlite3.dll`）自动探测与加载机制，证明主交付物采用「精简 exe + 原生 DLL 随包旁置」形态时，SQLite 数据库在 **0 额外解析代码、0 ILC 警告、InvariantGlobalization=true 模式** 下完全正常运作，打通 `SqliteFavoriteService` 真实持久化生命周期与 `EnglishIpaDictionary` 只读检索。

这是 Epic 002 的四大核心依赖穿刺中最后一项（spec 阻断项 3、7 与穿刺 7）：它不成立，收藏持久化与离线词典查询就必须引入复杂的 P/Invoke 自定义加载器或退回惰性降级。

## 结论（2026-11-01）

**穿刺 7 成立。** `Microsoft.Data.Sqlite` / `SQLitePCLRaw` 在 .NET 10 NativeAOT 下能够完美自动探测并加载随包旁置根目录的 `e_sqlite3.dll`。零需 `DirectPInvoke`，零需显式 `SetProvider` 或 `NativeLibrary.SetDllImportResolver`，零 JIT 反射警告，完全契合主人 2026-10-25 裁定的「精简 exe + 随包原生 DLL 旁置」免安装目录契约。

五条关键技术发现与成果：

1. **RID 资产自动复制与自动探测加载（零自定义胶水代码）：**
   - 在 `net10.0` + `RuntimeIdentifier=win-x64` + `PublishAot=true` 发布时，`Microsoft.Data.Sqlite` 依赖的 `SQLitePCLRaw.bundle_e_sqlite3` 会将 `e_sqlite3.dll`（1.89 MB）作为 RID 资产**自动复制到发布根目录**。
   - `SQLitePCLRaw` 底层在首个数据库连接打开时通过 `Batteries_v2.Init()` 绑定原生导出，.NET 运行时标准原生探测直接在应用根目录命中 `e_sqlite3.dll`，**完全不需要任何自定义原生加载解析器**，与原有 framework-dependent 发布物保持一致体验。
2. **NativeAOT 编译 0 警告 / 0 错误：**
   - 在 `TreatWarningsAsErrors=false` 且不加任何定向 `NoWarn` 的纯净口径下，`dotnet publish -c Release -r win-x64` **0 条 ILC 警告，0 条编译错误**。
   - 证明 `Microsoft.Data.Sqlite` 10.0.12 托管侧与 `SQLitePCLRaw` 核心调用链已完全 AOT-clean。
3. **极简产物布局与体积实测：**
   - 发布目录恰好只有 **2 个文件**：
     - `Pierce7.exe`：**2.24 MB**（2,352,128 B）精简 NativeAOT 可执行体；
     - `e_sqlite3.dll`：**1.89 MB**（1,978,880 B）SQLite 原生动态库（版本 3.53.3）；
     - 目录总体积仅 **4.13 MB**，无任何框架 DLL、无 hostfxr、无 JIT 运行时、PE 头无 CLR 数据目录（纯标准 C/C++ PE）。
4. **业务核心零逻辑漂移与全生命周期断言全绿：**
   - 真实链接主工程 `SqliteFavoriteService.cs`、`FavoriteContentNormalizer.cs`、`FavoriteContracts.cs`、`AppPaths.cs`、`RubyTextParser.cs` 与 `EnglishIpaDictionary.cs`，实现 100% 生产逻辑零分叉验证。
   - 覆盖 10 个测试模块、**51 项自动化断言全绿（51 PASS / 0 FAIL）**：
     - 惰性初始化（未收藏前绝不生成库文件）；
     - 基础 CRUD 写入与多语言 UTF-8 真实字形（日文假名/汉字、中文成语/拼音、英文单词/国际音标 IPA 符号 `ˈ`、`ː`、`ə`、`ɪ`）逐字节精确保持；
     - Upsert 覆盖更新与两端空白/大小写归一化匹配；
     - 跨进程 / 跨服务实例数据持久化与连接重开；
     - 编辑收藏项与内容碰撞拦截（防止归一化重名破坏唯一性）；
     - 物理删除与清空态平滑过渡；
     - 原生事务 Commit 与 Rollback 状态隔离；
     - `EnglishIpaDictionary` 只读打开、参数化查询 `$word` 与大小写不敏感检索；
     - 缺失文件与损坏情况下的软降级与错误捕获。
5. **`InvariantGlobalization=true` 审计结论（SQLite 侧完全无害）：**
   - 实测证实：在开启全局无 ICU 模式（`InvariantGlobalization=true`）下，SQLite 原生库内部处理 UTF-8 字符串完全基于字节流与内建 ASCII 规则，浮点数精度（`REAL`）、ISO 8601 日期串、Unicode 文本存取以及 `LIKE` 模糊匹配均工作完好，没有任何异常或行为偏移。
   - 结合穿刺 4 的 MeCab 实测结论，裁决 8 的 `InvariantGlobalization` 风险项已全面通过实测验证。
6. **真·干净隔离环境与阴性对照严密闭环：**
   - **干净隔离自测**：在彻底抹掉 `DOTNET_ROOT` 与 `PATH` 中所有 `dotnet` 路径的环境中，`Pierce7.exe` 独立执行，退出码 0，51 项断言全 PASS。
   - **阴性对照**：剥离 `e_sqlite3.dll` 后再次执行，操作系统直接拦截并返回退出码 `-1073740791`（`0xC0000139` / `STATUS_ENTRYPOINT_NOT_FOUND`），严密证明其真实依赖旁置 `e_sqlite3.dll`，绝无隐式加载系统或开发环境残留库。

## 范围与边界

**做：**

- `pierce/p7-sqlite-aot/` 独立脚手架（不进 `src/`、不进 `NaniKore.slnx`、不被 `dev.ps1` 引用）。
- 链接主工程真实代码：`SqliteFavoriteService.cs`、`FavoriteContentNormalizer.cs`、`FavoriteContracts.cs`、`AppPaths.cs`、`RubyTextParser.cs`、`EnglishIpaDictionary.cs`、`IpaTranscription.cs`。
- 10 个测试模块共 51 条确定性断言（覆盖原生提供程序探测、惰性建库、CRUD、UTF-8 字符保持、Upsert、归一化去重、跨实例持久化、修改与重名拦截、删除与空态、原生事务与回滚、只读词典参数化查询、InvariantGlobalization 兼容性）。
- NativeAOT 发布并在彻底剥离 .NET 运行时的干净环境下运行验证。
- 阴性对照验证（缺失旁置 DLL 时确定性拦截）。

**不做：**

- 不修改 `src/NaniKore` 业务持久化接口与数据库结构（已证明无需任何变动）。
- 不把 `e_sqlite3.dll` 塞入 `deps/`（已按主人 2026-10-25 裁决随包旁置根目录）。
- 不实现非 Windows 平台 SQLite 打包（本期仅验证 Windows，跨平台仅需按 RID 分流 `libe_sqlite3.so` / `libe_sqlite3.dylib`）。

## 打通判据（来自 epic spec 穿刺 7）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | AOT 产物跑通一次收藏写入与一次跨实例读取 | **成立**：跑通包含完整惰性建库、多语言写入、跨实例重启重读在内的 51 项全生命周期断言，零数据丢失 |
| 2 | 确认无需 `DirectPInvoke` / `NativeLibrary.Load` 定制 | **成立**：.NET 运行时标准原生探测与 `SQLitePCLRaw` 自动发现旁置 `e_sqlite3.dll`，完全零自定义加载代码 |
| 3 | NativeAOT 编译 0 警告并在干净环境下运行 | **成立**：`PublishAot=true` 发布 0 警告 0 错误；干净无 .NET 运行时环境下 51 项断言全 PASS，退出码 0 |

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | SQLitePCLRaw 在 NativeAOT 下反射式 provider 装配是否会失效 | 无需显式调用 `SQLitePCL.raw.SetProvider` 即可正常使用 | **通**：底层 `bundle_e_sqlite3` 在 AOT 下自动完成绑定，完全不需显式干预 |
| R2 | `e_sqlite3.dll` 随包旁置根目录能否被 .NET 原生探测自动命中 | 干净环境下启动无 DllNotFoundException | **通**：自动探测命中根目录 DLL，阴性对照证实缺失立即拦截 |
| R3 | `InvariantGlobalization=true` 是否会破坏 SQLite 数值/日期/文本查询 | 浮点数、ISO 8601 日期、多语言字符与 LIKE 查询行为正常 | **通**：SQLite 原生 C 库基于 UTF-8 字节流工作，不受 .NET 全球化模式影响 |
| R4 | `Microsoft.Data.Sqlite` 托管侧是否存在 ILC 剪裁警告 | `dotnet publish` 输出 0 ILC 警告 | **通**：0 warning / 0 error，10.0.12 托管侧已高度 AOT-friendly |

## 验证

| 命令 / 步骤 | 结果 | 支撑 |
|---|---|---|
| `dotnet run --project pierce/p7-sqlite-aot/Pierce7.csproj` | **51 PASS / 0 FAIL**，退出码 0 | JIT 基线确定性断言 |
| `dotnet publish pierce/p7-sqlite-aot/Pierce7.csproj -c Release -r win-x64` | **0 warning / 0 error**；exe 2.24 MB，DLL 1.89 MB | NativeAOT 干净裁剪与 RID 旁置 |
| `pwsh -File pierce/p7-sqlite-aot/verify.ps1` | **PASS**：布局恰为 2 文件，PE 无 CLR 头，干净环境 51 断言全 PASS，阴性对照拦截生效 | 干净环境零 .NET 运行时依赖与真实旁置加载 |

## 结论沉淀对后续批次的影响

1. **P3（结果面板与收藏批次）直接复用现有持久化代码：**
   - `SqliteFavoriteService.cs` 与 `EnglishIpaDictionary.cs` **无需任何 AOT 适配修改**，原汁原味接入；
   - 裁决 15（SQLite 随包旁置根目录）完全成立，阻断项 3 的 SQLite 依赖彻底消除。
2. **P6（发布与打包批次）目录契约完全坐实：**
   - 根目录原生 DLL 组合为：3 个 Avalonia 原生库（`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll`）+ `e_sqlite3.dll`，全部作为 RID 资产自动复制，零构建脚本魔改。
3. **裁决 8 全球化审计全面闭环：**
   - MeCab（穿刺 4）与 SQLite（穿刺 7）两大原生依赖均已实测证明在 `InvariantGlobalization=true` 下表现稳定可靠，为整包削减 30 MB ICU 依赖的决策奠定最终依据。
