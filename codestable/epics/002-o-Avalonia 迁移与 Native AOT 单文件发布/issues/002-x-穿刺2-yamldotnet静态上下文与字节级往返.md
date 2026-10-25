kind: issue
title: "穿刺 2：YamlDotNet 静态上下文覆盖 AppConfig 类型图，读→写→读与迁移前逐字节等价"
type: feature
status: closed
created: 2026-10-25
closed: 2026-10-25

---

## 目标

一句话：在 `pierce/p2-yamldotnet-static/` 用**真实的 `AppConfig` / `AppConfigDocument` 类型图**（直接链接 `src/NaniKore/Config/` 的类型文件，零漂移）建 `[YamlSerializable]` 静态上下文，跑通「读→写→读」往返，与迁移前（反射式 YamlDotNet 16.3.0）**逐字节等价**；同时评估升级 YamlDotNet 18.1.0（StaticGenerator 已收进主仓）是否改变行为。

这是 Epic 002 的硬阻塞穿刺（裁决 17）：它不成立，配置读写要么退手写 YAML 子集解析器（约 300–350 行），要么整个 AOT 方案要为配置开洞。

## 范围与边界

**做：**

- `pierce/p2-yamldotnet-static/` 独立脚手架（不进 `src/`、不进 `NaniKore.slnx`、不被 `dev.ps1` 引用）。
- `<Compile Include>` 链接 `src/NaniKore/Config/AppConfig.cs` 与 `ConfigDocument.cs`——类型图即真相，P5 改 schema 脚手架自动跟随。
- 静态上下文 + `StaticDeserializerBuilder` / `StaticSerializerBuilder`，选项与 `ConfigStore` 现有两个静态字段逐项对齐（`UnderscoredNamingConvention` / `IgnoreUnmatchedProperties` / `ConfigureDefaultValuesHandling(OmitNull)`）。
- 样本矩阵：真实 `config.template.yaml` + 全字段非缺省 + 显式 `~`（prompts 塌缩场景）+ 旧 `id` 键与未知键 + 多行块标量 prompt + 需转义/引号/超长折行的标量 + 最小文档。
- 每份样本跑三条路径并两两逐字节比对：反射（基线）、静态（候选）、以及「静态读反射的产出」交叉验证。
- NativeAOT 发布脚手架（`PublishAot` + 体积开关组，与穿刺 1 同口径），记录 ILC 警告面并在隔离目录跑一次发布产物。
- 版本评估：同一脚手架的 `YamlDotNet` 版本可用 `-p:YamlDotNetVersion=16.3.0` 切换，分别采集「静态(18.1.0) / 静态(16.3.0) / 反射(18.1.0) / 反射(16.3.0)」四组产出做升级行为漂移判定。

**不做（穿刺阶段边界；收尾时主人另行批准了 src 升级落地，见「执行记录」与「结论」）：**

- 不改 `src/NaniKore` 任何文件（含 `ConfigStore`）；链接是只读引用。
- 不碰 `SecretStore`（那是阻断项 2 的 JSON 穿刺范围）、不碰 `dev.ps1`、不碰解决方案。
- 不做手写 YAML 解析器退路——只在静态路线不成立时才启动，且那是另一条 issue。
- 不引入除 YamlDotNet 与静态生成器之外的任何包。

## 打通判据（来自 epic spec 穿刺 2）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | 真实 `.data/config.yaml` 三轮往返（读→写→读）与迁移前逐字节等价 | **PASS**（10 份样本，含机器上三份内容一致的真实 `.data/config.yaml`：9 配置档 + `>-` 多行折叠块标量 + `{{target_language}}` 占位符） |
| 2 | `config.template.yaml` 的四行档省略语义不变 | **PASS**：`api_type` / `supports_vision` / `timeout_seconds` / `enable_thinking` 在输出中不落行（C6） |
| 3 | ILC 警告面可处置（裁决 13 素材：能分辨第三方库与脚手架自身代码） | **PASS**：静态专用 AOT 发布 **0 警告**；含反射基线的构建报 IL3050×2 + YamlDotNet 程序集 IL2104/IL3053，全部归因于反射 builder——归因清晰，白名单素材现成 |

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | 静态上下文覆盖整个类型图（含 `List<ProviderProfileDocument>`、`bool?` / `int?` 可空值类型、嵌套段） | 生成器对根类型产完整代码，无 YDxxxx 诊断错误 | **通**：8 个类型全部显式登记后 0 条 YDNG 警告；`Nullable&lt;bool&gt;` / `Nullable&lt;int&gt;` 在 18.1.0 与 16.3.0 下均正常生成 |
| R2 | 命名约定在静态路径下仍生效（`auto_translate` ↔ `AutoTranslate`） | 静态往返产出的键名与反射一致 | **通**：往返键名一致即证明；另见执行记录「API 情报」 |
| R3 | `IgnoreUnmatchedProperties` 语义保留（旧 `id` 键、未知键静默吞掉） | 含 `id`/未知键的样本不抛且产出一致 | **通**：`legacy-keys.yaml`（根级 `id` + 未知段 + 档内 `id` + 未知键）C1 逐字节等价 |
| R4 | `OmitNull` 省略语义保留（缺省字段不落行，四行档形态） | 全字段样本序列化后缺省键不出现 | **通**：C6 专项断言 + 真实用户配置中仅 `supports_vision: false` 落行（唯一非缺省键） |
| R5 | 多行块标量与需引号/折行的标量发射行为一致 | 多行 prompt 样本逐字节相同 | **通**：真实配置 `>-` 折叠块（含空行、引号、占位符）与 `multiline.yaml` / `scalars.yaml`（`|` / `|-` / `#` / `:` / 字面量 null / 超长折行 / emoji / 制表符）全部逐字节相同 |
| R6 | NativeAOT 下无 ILC 警告（或警告可归因可处置） | `dotnet publish -p:PublishAot=true` 警告清单干净 | **通**：`-p:PierceStaticOnly=true` 静态专用发布 0 警告；发布 exe（3.0 MB）跑全部样本退出码 0 |
| R7 | 升级 18.1.0 不引入行为漂移 | 反射(18.1.0) 与反射(16.3.0) 产出一致，且静态(18.1.0) 与反射(16.3.0) 一致 | **通**：`compare-versions.ps1` 跨版本逐字节比对 **0 差异**（10 样本 × B1/B2/S1/S2 四标签） |

## 方案与实现安排

**为什么是静态上下文而不是手写解析器：** 裁决 2 已定「单路线、不并行维护」；静态上下文改动集中在一个上下文类，反射消除在编译期完成，是三条路（源生成 / 手写子集 / 保持反射）里唯一同时满足 AOT 与可维护性的。

**脚手架结构：**

```text
pierce/p2-yamldotnet-static/
├── Pierce2.csproj            YamlDotNet 版本可注入；AOT 开关组同穿刺 1
├── YamlStaticContext.cs      [YamlSerializable(typeof(AppConfigDocument))] 上下文（P5 的正式落点原型）
├── Program.cs                基线/候选/交叉三条路径 + 逐字节比对 + 退出码
└── samples/                  样本矩阵与四组产出（.gitignore 已忽略 pierce/）
```

**比对口径：** 输出一律 UTF-8 无 BOM（与 `ConfigStore.AtomicWrite` 的 `UTF8Encoding(false)` 一致）；「逐字节等价」= 文件字节序列完全相同，不是语义等价。

**升级评估口径：** 若静态(16.3.0) 因生成器能力不足（如可空值类型不支持）跑不通，升级即为被迫结论而非可选项；若两版都通，以「反射(18.1.0) vs 反射(16.3.0)」判定升级本身的行为漂移，再结合 18.1.0 的 AOT/安全修复给出建议。

## 验证

| 命令 / 步骤 | 结果 | 支撑 |
|---|---|---|
| `dotnet run -c Release --project pierce/p2-yamldotnet-static`（18.1.0） | 10 样本 C1–C7 全 PASS，退出码 0 | 判据 1/2、R1–R5/R7 |
| `dotnet run -c Release -p:YamlDotNetVersion=16.3.0 -p:StaticGeneratorVersion=16.3.0` | 10 样本全 PASS | R7、升级评估 |
| `powershell -File pierce/p2-yamldotnet-static/compare-versions.ps1` | 跨版本逐字节 **0 差异** | R7、判据 1（对「迁移前」= 16.3.0 的等价） |
| `dotnet publish -c Release -r win-x64 -p:PierceStaticOnly=true`（18.1.0） | **0 警告 0 错误**；exe 3.0 MB；跑全部样本退出码 0 | 判据 3、R6 |
| `dotnet publish -c Release -r win-x64`（含反射基线） | IL3050×2 + YamlDotNet IL2104/IL3053，全部归因反射 builder | 判据 3（归因素材）、裁决 13 |
| `dotnet publish -c Release -r win-x64 -p:PierceStaticOnly=true -p:YamlDotNetVersion=16.3.0` | 0 警告；exe 跑样本退出码 0 | R7（两版 AOT 均干净） |
| 生成器产物核查（`obj/generated/.../YamlDotNetAutoGraph.g.cs`） | 8 类型 × 全属性描述符 + `Nullable<bool>`/`Nullable<int>` + `List<ProviderProfileDocument>` | R1 |
| 主工程 `NaniKore.exe --self-test`（升级切换后的 Debug 产物） | **630 PASS / 0 FAIL / 退出码 0**（含 ConfigStore 全部往返测试；WinExe 在此 shell 下 stdout 不落盘，经 Start-Process 管道重定向取证） | 升级切换零回归 |

## 执行记录

**新增文件（`pierce/` 为本地脚手架，已 .gitignore 不入库；src 三处为升级落地）：**

- `pierce/p2-yamldotnet-static/Pierce2.csproj` — 版本可注入（`YamlDotNetVersion`/`StaticGeneratorVersion`，默认 18.1.0）；AOT 开关组同穿刺 1；`PierceStaticOnly` 开关剔除反射基线供 AOT 证据。
- `pierce/p2-yamldotnet-static/ConfigYamlStaticContext.cs` — 上下文原型（P5 落点原型，已原样搬入 src）。
- `pierce/p2-yamldotnet-static/Program.cs` — 比对台（C1–C7 矩阵 + 首差异定位 + 退出码）。
- `pierce/p2-yamldotnet-static/samples/*.yaml` — 10 份样本（见「方案」）。
- `pierce/p2-yamldotnet-static/compare-versions.ps1` — 跨版本逐字节比对。
- `pierce/p2-yamldotnet-static/publish-static.log` / `publish-both.log` / `publish-static-1630.log` — 发布证据。
- **src 升级落地（2026-10-25 收尾时执行）：** `src/NaniKore/NaniKore.csproj`（YamlDotNet 18.1.0 + 静态生成器 18.1.0）、`src/NaniKore/Config/ConfigYamlStaticContext.cs`（新建）、`src/NaniKore/Config/ConfigStore.cs`（两个 builder 切静态）。

**API 情报（P5 直接可用）：**

1. **两个属性职责不同**：`[YamlStaticContext]` 标记「这是静态上下文类」；`[YamlSerializable(typeof(X))]` 才用于登记类型——只写前者不生成任何代码，只写后者不识别上下文。`YamlStaticContextAttribute` 在 18.1.0 的 XML 文档里没有记录，只在程序集里（反编译生成器才确认）。
2. **生成器不沿类型图递归发现**：嵌套段与 `List` 元素类型必须逐个 `[YamlSerializable(typeof(X))]` 显式登记，漏登记报 YDNG001 警告（主工程 `TreatWarningsAsErrors` 下直接构建失败）。改配置 schema 时，`ConfigYamlStaticContext` 的登记清单是唯一要同步的地方。
3. **生成的部分类是 public**：上下文类必须声明为 `public partial`，写 `internal` 会报 CS0262（可访问性冲突）。
4. 命名约定由 builder 侧 `WithNamingConvention` 提供，静态路径同样生效；往返键名逐字节一致即为证据。

**与设计的偏差：**

1. **比对矩阵从 C1–C6 重构为 C1–C7**：初版把「互读」设成 `静态(基线产出) == 基线产出`、「幂等」从第一轮起算，两者都不成立——`prompts: {}` 首轮产出再读会展开成显式空串（序列化层固有行为，真实 `ConfigStore` 同样如此，因为 `Normalize` 的 null→空串收敛发生在读侧）。重构后：互读比「同一文本两条路的产出」（C2/C3），幂等从第二轮起算（C4/C5），另加三轮总等价（C7）。**核心判据 C1 不受影响，初版即全 PASS。**
2. **`ConfigStore.Normalize` 不进入比对管线**：两条路共享同一管线，变量只有 YamlDotNet 实现；归一化是 P5 原样保留的业务逻辑。YAML 层的任何分歧都会在反序列化时暴露，与是否含归一化无关。
3. **`StaticOnly` 属性名撞 MSBuild 保留名**（MSB4004），改名 `PierceStaticOnly`；条件 PropertyGroup 不能嵌套在主 PropertyGroup 内。

**环境备注：** 还原版本 YamlDotNet 18.1.0 / Vecc.YamlDotNet.Analyzers.StaticGenerator 18.1.0（对照组 16.3.0 / 16.3.0）。真实 `.data/config.yaml` 在仓库根、`artifacts/win-x64/`、Debug 输出目录各一份，内容一致，取仓库根那份为 `real-user.yaml` 样本。

## 关闭时

- 回写候选：epic spec「P0 风险结论表」第 2 行、AOT 阻断项 1 的对策与版本结论、裁决 2 的补充实测证据、技术选型 YamlDotNet 条目、「当前推进」视图。**已全部回写（2026-10-25，见 epic spec 本次修订）；另因升级落地，P5 批次表的 YamlDotNet 项已标注提前完成。**
- project spec 不动——YamlDotNet 静态化不是当前真相，等 Epic 关闭合并。

## 结论（2026-10-25，主人授权收尾）

**穿刺 2 成立，升级已执行，可以关闭。**

**路线结论：** 静态上下文路径与迁移前反射路径在 10 份样本（含真实用户配置）上逐字节等价；四行档省略语义、旧 `id`/未知键容忍、多行块标量与引号折行发射全部一致；静态专用 NativeAOT 发布 0 警告，反射基线则报 IL3050——反射正是阻断点，静态上下文把它整个移出 AOT 图。

**版本结论（主人 2026-10-25 批准）：升级 YamlDotNet 16.3.0 → 18.1.0 并切换静态路线，已在主工程落地：**

- `src/NaniKore/NaniKore.csproj`：`YamlDotNet` 18.1.0 + `Vecc.YamlDotNet.Analyzers.StaticGenerator` 18.1.0（同号不混搭）；
- 新增 `src/NaniKore/Config/ConfigYamlStaticContext.cs`：`[YamlStaticContext]` + 8 类型逐个 `[YamlSerializable]` 登记（穿刺 2 原型原样搬入）；
- `ConfigStore` 两个 builder 从反射式换成 `StaticDeserializerBuilder` / `StaticSerializerBuilder`，三个选项与迁移前逐项一致；
- 主工程 Debug 构建 **0 错误 0 警告**（`TreatWarningsAsErrors` 下零 YDNG，即类型图无漏登记）；
- 主工程全量自测 **630 PASS / 0 FAIL / 退出码 0**——静态路径在真实代码路径（含 ConfigStore 全部往返测试）上回归全绿。

**遗留事项：** ① P5 的 YamlDotNet 对策项已提前完成（版本 + 上下文 + ConfigStore 切换），P5 只剩「验证」性质的工作；② 穿刺 3 起按顺序逐条建 issue（003 待创建）；③ `pierce/p2-yamldotnet-static/` 脚手架按主人裁定为本地工作区，结论已全部沉淀于本 issue 与 epic spec。
