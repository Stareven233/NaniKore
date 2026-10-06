---
kind: issue
title: "配置模板化、配置档名唯一索引与 .data 数据目录（密钥单文件）"
type: feature
status: resolved
created: 2026-10-06
updated: 2026-10-06
---

<!--
type: feature
写法：按读者此刻要解决的问题组织，不套固定章节名凑满。
-->

# 配置模板化、配置档名唯一索引与 `.data` 数据目录（密钥单文件）

> **读者：** 跨会话接手的人——「要做成什么、别碰什么、哪些决策已经拍板、怎么验、关了要回写哪里」。
> **自检：** 目标与范围 · 决策记录（含被否决方案）· 现状 · 方案与分期 · 质量承诺 · 风险 · 验证 · 遗留 · 关闭回写。

---

## 做成以后是什么样

1. **程序目录根只放交付物**：exe、`deps/`、`config.template.yaml`。所有本机数据收拢到 `程序目录\.data\`（`.data/config.yaml`、`.data/keys.bin`、`.data/collections.db`），启动时确保该目录存在。发布 zip 里没有 `.data`，解压覆盖在物理上碰不到它。
2. **`config.yaml` 归用户**：`.data` 里没有 `config.yaml` 时，从程序目录的 `config.template.yaml` **字节级原样复制**生成一份（保留模板注释，用户第一眼看到的就是可读的模板）；已有时**原样不动**。模板随包发布、程序永不写它。
3. **每个配置档 4 行**：`name` / `kind` / `base_url` / `model`。`api_type`、`supports_vision`、`timeout_seconds`、`enable_thinking` 只在偏离缺省时才落行；保存一次设置后不会反弹成 8 行。
4. **配置档唯一身份是 `name`**：`pipeline.active_profile` 记激活档的 name；翻译页下拉、错误提示、密钥索引全部用它。旧配置里的 `id` 无痕消失——反序列化器本来就不认识它，**没有任何代码「做」这件事**。
5. **密钥是一个文件**：`.data/keys.bin`，整块 DPAPI 密文，内部是 `{配置档名: API Key}` 的 JSON 映射。翻译页能一键打开 `.data`、能清除当前配置档的密钥。密钥位置只有一个，不可改、不需要改。
6. **收藏库改名搬家**：`nanikore.db` → `.data/collections.db`（旧名字大于实质——它只是收藏数据，没道理独占程序目录根）。
7. **本版没有任何迁移机制**：无脚本、无按钮、无骨架刷新、无规则引擎。破坏性变更的兜底 = 「关于」页版本差的一行提示 + 模板就在程序目录、用户照它手改。具体要不要做迁移，等正式版发布时再决策（见「遗留」）。旧安装升级的现实代价：首启拿到全新 `.data/`——配置按模板重建、密钥重粘一遍；旧根目录的 `config.yaml`、`nanikore.db` 成为孤儿（不删、不读、不提）。
8. **版本差有可见信号**：`config.template.yaml` 与 `.data/config.yaml` 的 `version` 在 major 或 minor 上不一致时——启动后托盘提示一次，设置-「关于」页给一行 InfoBar。仅提示，没有按钮、没有自动动作。「关于」页版本显示 `v{A}({B})`：A = 模板版本（最新），B = 配置版本。

**范围：** `.data/` 数据目录、模板化与首启复制、配置精简与缺省省略、name 唯一索引、密钥单文件、收藏库改名搬迁、版本差提示（仅提示）、构建/发布/忽略项更新、文档。
**不包含：** 任何迁移机制（脚本/规则/按钮——正式版再决策）、`deps/` 迁移、导出/导入配置、密钥目录重定向（随单文件方案取消）、任何旧格式兼容分支。

## 决策记录（讨论收敛，含被否决的方案）

| # | 议题 | 结论 | 理由 / 纠正过程 |
| --- | --- | --- | --- |
| 1 | 配置放哪 | **程序目录 `.data/config.yaml`，模板 `config.template.yaml` 留在程序目录根** | 我原提「优先读 `%APPDATA%\NaniKore\config.yaml` + 指针文件重定向」，主人否决：程序没有安装动作，覆盖更新的心智模型就是解压；改放 APPDATA 会引入种子复制、指针、整目录迁移一大套机制。最初拍板「程序目录根/config.yaml」，收尾讨论时主人进一步把所有本机数据收拢进 `.data/`，定下唯一规则：**根目录 = 交付物，`.data/` = 本机数据**。模板化 + `.data/` 两级隔离后，「解压永远冲不掉用户配置」天然成立 |
| 2 | 密钥放哪、怎么存 | **`.data/keys.bin` 单文件：整块 DPAPI 密文，内装 `{档名: Key}` JSON** | 我原提 `%APPDATA%\NaniKore\secrets\{id}.bin` 每档一文件，主人两步修正：① 一个文件统一存比每档一文件简单，`key-{sha256(name)}.bin` 这套文件名编码（原方案最别扭的部分）直接消失；② 密钥放进程序目录 `.data/`——项目是无安装 portable 模型，`nanikore.db` 早已以相同理由（源码注释原话「绿色便携、覆盖升级不丢数据」）放在程序目录，密钥做邻居心智才一致：整个文件夹就是「本软件」，备份 = 复制 `.data/`。且单文件若放 `%APPDATA%`，新旧两版程序目录并存试用时会抢同一个文件、互相抹条目；放程序目录天然按实例隔离。代价（解压到新目录的用户丢密钥）与「不做迁移」同类，主人认了 |
| 3 | `deps/`、收藏库 | **`deps/` 原地不动；`nanikore.db` 改名 `collections.db` 并搬进 `.data/`** | 数据目录只承接配置与密钥这类小文件；OCR 权重、ONNX DLL、注音词典不搬家。`nanikore.db` 名字大于实质（只是收藏），且既然有了 `.data/`，就没理由让它独占根目录 |
| 4 | `timeout_seconds` 缺省 | 统一 **30 秒**，三个官方默认档也不再显式写 60 | 主人原话：没人愿意为翻译等 10 秒 |
| 5 | 「允许修改」指向哪个目录 | **撤销**：单文件方案下密钥只有 `.data/keys.bin` 一个位置，不提供任何目录重定向 | 原决策（`data-dir.txt` 指针可改密钥目录）建立在「密钥在 `%APPDATA%`、需要允许用户搬走」的前提上；决策 2 翻转后前提消失。翻译页只保留「打开数据目录」与「清除当前配置档密钥」 |
| 6 | 迁移 | **本版完全不做**：无版本差驱动的迁移流程、无骨架刷新、无一次性脚本；major/minor 判定保留，但只用于「提示」 | 主人拍板：目前不会有任何大的迁移，真有就麻烦用户照着 `config.template.yaml` 手配；具体迁移等正式版发布再决定。我原方案（骨架刷新 + 后续规则式引擎）是半成品——它修不了真正要搬的键（`active_profile` 的 id→name，原文档自认的「已知限制」），而能自动补删的键本来也不要紧 |
| 7 | 代码兼容立场 | **运行时代码不留旧格式分支，且不写「忽略旧 id」的专门代码 / 注释 / 测试** | 主人的项目原则。issue 009 已有先例：模板键改名后，旧配置「反序列化无对应属性、被忽略、回落空串」，用户曾自定义就在设置页重填一次——容忍是反序列化器的既有行为，不是功能。旧 `id` 同理：`ProviderProfileDocument` 不设 `Id` 字段 + 既有 `IgnoreUnmatchedProperties()` 就是全部机制 |
| 8 | 程序目录不可写 | 拒绝启动并提示（弹错 + 退出码 1），**不回落别处** | 宁可明确失败，也不要两份真相。`.data` 创建/写入失败走同一条路 |
| 9 | 密钥相关 UI 放哪 | 翻译页（与配置档、API Key 同页）：「打开数据目录」+「清除当前配置档密钥」 | 相关性优先；原「密钥目录」行随重定向取消而删除 |
| 10 | 一次性迁移脚本 | **取消**：不进仓库、不进交付物 | 「必须是一次性的」原是对脚本的要求；迁移整体砍掉后，脚本唯一要解决的真实问题（6 个旧密钥文件改名）由「重填一遍」承接，正式版前不值当为它建机制 |
| 11 | 迁移的定位 | **正式版再决策**：本版接受「一次性手动重填」 | 我曾纠正主人「用户升级后重填密钥」的说法，坚持密钥换索引必须无感、属于迁移；主人重新拍板：pre-1.0 没有真实用户群，重填几分钟的代价 < 维护一套迁移机制的代价 |
| 12 | 关于页版本号 | **两个都显示**：`v{A}({B})`，A = `config.template.yaml` 版本（最新），B = `config.yaml` 版本 | 主人指定格式；一眼看出配置落后于模板多少 |

## 现状怎么工作（改动前）

- `ConfigStore` 以 `AppContext.BaseDirectory` 为配置目录：目录里有 `config.yaml` 就直接读，没有就写一份默认配置。
- 每个配置档同时持有 `Id`（稳定内部索引，用于 `pipeline.active_profile` 与 DPAPI 文件名）和 `Name`（面向用户的显示名），两者在模板里各占一行。
- API Key 以 DPAPI 密文存在 `%APPDATA%\NaniKore\secrets\{id}.bin`。
- 收藏库存 `AppContext.BaseDirectory/nanikore.db`（SQLite）。
- 仓库根的 `config.yaml` 是不含密钥的可审阅模板，`dev.ps1` 构建时递增它的 build 版本段，发布时复制到发布目录；csproj 也把它复制到 Debug 输出。

一句话：**配置跟着程序目录走，解压更新会连用户的配置一起冲掉；密钥已在用户数据区，但索引用的是即将被删除的 `id`；收藏库占着程序目录根、顶着一个过大的名字。**

## 方案与实现安排

### A 期 · `.data/`、模板化与 name 唯一索引

- 仓库根 `config.yaml` → `git mv` 为 `config.template.yaml`，按 4 行形态重写成干净骨架（当前库里那份带着真实 profile 列表，执行时手写，不走脚本）；头部注释写清「程序永不写本文件；首启在 `.data/` 生成 config.yaml」。
- **数据目录解析**：`Path.Combine(AppContext.BaseDirectory, ".data")`，启动时 `Directory.CreateDirectory` 确保存在；创建或写入失败走 B 期拒绝启动路径。
- `ConfigStore` 改为面向数据目录：`.data/config.yaml` 存在 → 读；不存在 → 从程序目录 `config.template.yaml` **字节级原样复制**过去再读（保留模板注释）；模板也没有 → 写内置默认。复制只在缺失时发生一次，之后永不自动改写用户配置。
- `AppConfig.cs`：`ProviderProfile` 删掉 `Id`；缺省值改为 `api_type=completions`、`supports_vision=true`、`timeout_seconds=30`、`enable_thinking=false`；三个默认档工厂方法保持各自 Base URL / 模型，超时交给缺省 30。
- 新增磁盘契约 `ProviderProfileDocument`（与 `AppConfigDocument` 同处收敛）：可缺省字段全部可空。`ToRuntime()` / `ToDocument()` 两个映射函数是「缺省折叠成 null」的唯一位置，只在写盘时走。**DTO 不设 `Id` 字段**：旧配置里的 `id` 由既有 `IgnoreUnmatchedProperties()` 吞掉，不写专门代码、注释、测试。
- `ConfigStore.Normalize`：只做 name 唯一化（重名追加 ` (2)`）、`kind` / `base_url` / `model` / `timeout_seconds` 合法性兜底；`active_profile` 只按 name 匹配，不中则回落第一个档（既有不变量，不为旧 id 开特例）。
- `Id` 的全部使用点改为 `Name`：`GetProfile` / `GetActiveProfile`、`ProviderFactory`（查找 + provider 缓存字典 + 报错文案）、`ProviderErrorMapper`（参数改名 `profileName`）、`SettingsWindow.Save` 的活动档解析、`TranslationPage`（`_pendingKeys` / `_deletedProfileKeys` / 新建 / 复制 / 删除）、`Program`（`--dump-config` 输出、`--profile <name>` 用法文案）、`SelfTest`。
- 复制配置档不再拼 `-copy` id，只生成唯一 name；新建档不再生成 GUID id。
- 构建链路同批改名：`NaniKore.csproj` 复制项改指 `..\..\config.template.yaml`（Link 同名）；`scripts/dev.ps1` 的 `Get-NaniKoreProductVersion` 改读模板，publish 里「把根 `config.yaml` 复制进输出目录」整段删除（运行期数据不再进交付物）；`scripts/update_version.py` 默认 `--config` 改指模板；`AGENTS.md` 「自动递增 config.yaml 版本号」措辞同步为模板。

### B 期 · 密钥单文件 + 可写性检查 + 收藏库搬迁

- `SecretStore` 改为实例类 `new SecretStore(directory)`，默认 `directory` = `.data`；自测一律注入临时目录（**现状 `SelfTest.Config` 直接写真实 `%APPDATA%`，本版修掉**）。
- `keys.bin` 信封：外层整块 DPAPI（`ProtectedData.Protect(整个 JSON 的 UTF-8 字节, 熵值 "NaniKore.v1", CurrentUser)`），内层 `{ 配置档名: API Key }` 的 JSON。落盘即二进制密文：无明文、无文件名编码问题、点开是乱码。
- 写盘走与 `ConfigStore.AtomicWrite` 相同的临时文件 + `File.Move` 原子写，避免崩溃留下半份映射。
- API：`Save(name, key)` / `Load(name)` / `Exists(name)` / `Delete(name)` / `Rename(oldName, newName)`。`Load` 遇到整体解析失败或 DPAPI 解不开（换用户/换机器）一律按「未配置」处理，绝不泄露密文内容。
- **不读旧路径**：`SecretStore` 只看注入目录下的 `keys.bin`；旧布局 `%APPDATA%\NaniKore\secrets\{id}.bin` 与旧 `nanikore.db` 不读、不删、不提，有自测断言这一点。`%APPDATA%\NaniKore` 恢复无人居住——所有本机数据收拢到 `.data/`。
- 翻译页「数据目录」行：路径只读展示 +「打开目录」（资源管理器定位 `.data`）+「清除当前配置档密钥」（确认后进 `_deletedProfileKeys`，保存时从 `keys.bin` 删该键）。原「密钥目录可改/恢复默认」整套随重定向取消而删除；「通用」页不改动。
- 重命名配置档时 `Rename` 把密钥键名跟着搬（name 索引的必然要求，否则一改名 Key 就丢）。
- 可写性前置检查：启动时确保 `.data` 可创建、可写；不能则 GUI 走 `MessageDialog`、CLI 走 `Console.Error`，退出码 1。`--self-test` 不在此列（用临时目录）。
- 收藏库：`SqliteFavoriteService.DatabaseFileName` 改 `collections.db`，默认路径改 `AppContext.BaseDirectory/.data/collections.db`；类注释与 `FavoritesPage.xaml` 工具提示（那行说明存在哪、还带复制路径按钮）同步。历史 epic 文档不动，真相随 spec/index.md 回写。

### C 期 · 版本差提示（仅提示）

- 「关于」页版本显示 `v{A}({B})`：A 取 `config.template.yaml` 的 `version`（本次发布版本），B 取 `.data/config.yaml` 的 `version`；宿主通过 `Func<string?>? templateVersion` 注入，缺注入时只显示 B（现有行为）。
- 版本差判定：取两者的 `version` 前缀（`+build` 之前），**major 或 minor 不同**即视为落后；任一侧解析不出版本号则不判定（不误报）。dev 构建每次只递增 build 段，开发期不误报。
- 检测时机：每次启动一次 + 每次打开「关于」页。命中时：托盘气泡「检测到配置版本与模板版本不一致，详见 设置-关于」；关于页常驻一行 InfoBar「当前配置版本落后于模板，可对照程序目录 `config.template.yaml` 手动核对」。**无按钮、无自动动作**。无宿主注入（自测、`--settings-preview`）时不显示，不影响页面其他断言。
- 实现要点：读配置时要能指定「模板从哪个文件夹来」，不指定就用程序自己所在的文件夹。这样自测才能在临时文件夹里跑：既能测「没有模板就用内置默认」，也能测「从一份假模板复制出 config.yaml」，不会顺手读到真程序目录里的模板而测出错误结果。模板的版本号也要能单独读出来，给「关于」页显示 A 用。

### D 期 · 发布链路与文档

- `.gitignore` 加 `/.data/` 与 `/config.yaml`（`git mv` 之后运行期配置任何时候都不能进仓库；`.data/` 一条罩住配置、密钥、收藏库全部运行期数据）。
- `scripts/dev.ps1` 的 `New-NaniKoreReleaseArchive`：打包排除 `.data`。它现在整目录复制 `artifacts\win-x64`，从那儿跑过一次应用就会把密钥密文和收藏库打进发布 zip；一个 `.data` 全挡住（顺带消除 `nanikore.db` 长期存在的同样隐患）。
- 文档：`README.md`（配置与翻译服务、发布包结构两节——发布包结构变成 exe + `deps/` + `config.template.yaml`，首启生成 `.data/`）、`codestable/spec/index.md`（「翻译、结果与配置」「工具链与发布」两节：模板化、`.data/`、name 索引、`keys.bin`、`collections.db` 新真相）回写。

## 质量承诺（选中即承诺）

| 特性 | 承诺 | 证据 |
| --- | --- | --- |
| 信息安全性 · 保密性 | 密钥只以当前用户 DPAPI 密文落盘；`keys.bin` 整块密文、无明文；文件与目录名不含档名、不含 Key；`--dump-config` 只报「有/无」 | 自测断言写盘字节不含明文；自测改用临时目录，不再触碰真实 `%APPDATA%` 与真实 `.data` |
| 交互能力 · 用户差错防御 / 自描述 | `.data` 可见（一键打开）、单档密钥可清除；配置从 8 行变 4 行 | 自测 + 手测路径 |
| 可维护性 · 可修改性 / 可测试性 | 「哪些字段落盘、缺省如何省略」收敛在单一映射处，改 schema 只改一处并有自测锁形态；应用内零旧格式分支 | 自测断言写盘恰好 4 行；自测断言不读 `secrets\` 旧布局 |
| 可靠性 · 可恢复性 | 程序目录/`.data` 不可写、已有 `.data/config.yaml`、旧文件残留三类情况都明确失败或保持现状，不产生半份状态 | 手测 + 自测（拒绝路径） |

## 风险与对策

| 风险 | 对策 |
| --- | --- |
| `keys.bin` 损坏或被误删 | 原子写把半份概率压到近零；整块丢失 = 全部 Key 重填（与「密钥可再生」的定位一致）；删掉整个 `.data` = 回到首启态，语义同当年 `nanikore.db`「删除即回空态」 |
| 从 `artifacts\win-x64` 跑过应用，`.data` 被打进发布包 | `New-NaniKoreReleaseArchive` 排除 `.data` + 手测 ⑧ |
| 同一 `.data` 被多实例并发写 | 原子写保证不留半份，最后写入者胜；桌面单用户场景可接受（按实例隔离的设计本就把「新旧两版并存」冲突降到了最低） |
| 重命名配置档丢密钥 | `Rename` 显式搬键 + 自测 |
| 「删除数据目录」被程序目录旧文件复活 | 模板只在 `.data/config.yaml` 缺失时复制一次；用户删掉 `.data` 只会按模板重建骨架 |
| dev 构建递增 build 段造成误报 | 只比 major/minor |
| 自测误写真实用户数据 | `SecretStore` 目录可注入 + 所有自测传临时目录 |
| 解压到新目录升级的用户丢密钥 | 版本差提示覆盖不到这类用户（两边版本号可能一致）；代价已认（重填），正式版再评估迁移 |

## 验证

自测（`SelfTest.Config` / `SelfTest.Ui.Settings`，全部走临时目录）：
1. 默认档写出去恰好 4 行；`api_type` / `supports_vision` / `timeout_seconds` / `enable_thinking` 在缺省时全部省略；显式非缺省值原样保留。
2. 只有 `name` 的档：`kind=openai`、`api_type=completions`、`supports_vision=true`、`timeout_seconds=30`。
3. 写盘形态不含 `id` 键（DTO 形状的结果性断言；**不**写「旧配置 id 可加载」的专门断言）。
4. **反向断言（零兼容）**：在临时目录造一个 `secrets\xxx.bin`，`SecretStore.Load(name)` 返回 null——应用只认注入目录下的 `keys.bin`。
5. `Rename(旧名, 新名)` 后新名读得到、旧名读不到。
6. `keys.bin` 信封：文件字节不是明文 JSON；Save/Load 往返一致；整文件损坏时 `Load` 按空处理不抛。
7. `.data/config.yaml` 缺失时从模板字节级复制生成（注释保留）；已存在时不被覆盖。
8. 版本差：major/minor 不同判落后；仅 build 段不同不判；任一侧不可解析不判。
9. 「关于」页版本 `v{A}({B})` 形态与提示行可见性。
10. 可写性拒绝路径（`.data` 创建失败模拟）。
11. 收藏库默认路径为 `.data/collections.db`（自测传临时路径，功能语义不变）。
12. 现有 `TestConfigRoundTrip` / `TestSettingsCloseSizeWriteBackKeepsExternalUpdates` 等按新语义改写。

手测：① 删掉 `.data` 重启 → 按模板重建，翻译页/收藏页正常 ② `./scripts/dev.ps1 build` / `run` → 配置档、活动档、API Key 回显都在 ③ `./scripts/dev.ps1 test` 全绿 ④ 解压一个新发布包覆盖程序目录 → `.data` 不动、托盘不报（版本一致时）⑤ 手工把模板 minor +1 → 托盘提示与关于页行出现 ⑥ 翻译页清除当前配置档密钥 → `keys.bin` 少一个键；重命名配置档 → 密钥跟着搬 ⑦ 收藏功能正常，路径为 `.data/collections.db` ⑧ 从 `artifacts\win-x64` 跑一次应用再 `./scripts/dev.ps1 publish` → 发布 zip 不含 `.data` ⑨ 旧安装痕迹（程序目录根的旧 `config.yaml`、`nanikore.db`）原样保留，不被删不被读。

## 遗留

- **迁移评估（正式版发布时决策，已登记 `_todo.md`）**：若届时存在真实用户群与破坏性变更需求，再做按版本区间的规则式迁移（键改名 / 语义变更 / 值搬运 / 密钥换索引）+ 无感执行 + 执行前可确认清单。本版明确不做，代价 = 一次性手动重填（配置按模板重建、密钥重粘一遍）。
- 旧安装残留（用户机器程序目录根的 `config.yaml`、`nanikore.db`、`%APPDATA%\NaniKore\secrets\`）不自动清理——正式版前主人大可手动删；要不要提供「一键清理旧文件」留到正式版评估。

## 关闭时

- 回写 `codestable/spec/index.md`：「翻译、结果与配置」补模板化、`.data/`、name 索引、`keys.bin`、`collections.db` 真相；「工具链与发布」补 `config.template.yaml` 与版本号来源变化。
- 回写 `README.md`：配置与翻译服务、发布包结构两节。
- 关闭判断与验证摘要：以「验证」全绿 + 手测 ①~⑨ 通过为准。
- 遗留：迁移评估（见上）。

---

## 关闭记录（2026-10-06）

**实现落点**

- 新增 `Config/AppPaths.cs`（`.data` 解析与可写性探测、`DataDirectoryUnavailableException`）与 `Config/ConfigDocument.cs`（`ProviderProfileDocument` / `AppConfigDocument` 磁盘契约 + `ToRuntime` / `ToDocument` 唯一映射处 + `ConfigVersionComparator`）。
- `config.yaml` 经 `git mv` 成为 `config.template.yaml`，重写为 4 行干净骨架（版本 0.4.0，标记破坏性变更）；`ConfigStore` 面向数据目录，首启字节级复制模板，模板目录可注入；`ProviderProfile` 删除 `Id`，缺省口径收敛到 `ProviderProfileDefaults`。
- `SecretStore` 改为实例类 + `keys.bin` 单文件信封（整块 DPAPI + `{档名: Key}` JSON + 原子写），API 增补 `Rename`；旧 `secrets\{id}.bin` 布局不读不删。
- 翻译页新增「数据目录」行（只读路径 + 打开目录 + 清除当前配置档密钥）；重命名配置档时保存设置会把密钥键名搬到新档名。
- `SqliteFavoriteService` 改名 `collections.db` 并落到 `.data/`；`ProviderFactory` / `ProviderErrorMapper` / `Program` / `NaniKoreApp` 全部按 name 索引；「关于」页 `v{A}({B})` + 版本差 InfoBar；启动可写性拒绝路径（GUI 弹错、CLI Console.Error，退出码 1）。
- 构建发布链路：csproj 改复制 `config.template.yaml`；`dev.ps1` 版本号改读模板、显式同步模板进发布目录（`PreserveNewest` 在中间产物较新时会整段跳过，不能只靠 csproj 复制项）、打包排除 `.data` 与已退役的 `nanikore.db`；`update_version.py` 默认路径改模板；`.gitignore` 加 `/.data/` 与 `/config.yaml`。

**验证摘要**

- `./scripts/dev.ps1 test`：544 项 PASS / 0 FAIL（覆盖 4 行形态、缺省省略、无 `id` 键、模板字节级复制、`keys.bin` 信封与损坏降级、旧布局反向断言、`Rename` 搬键、版本差判定、关于页 `v{A}({B})` 与提示行、可写性拒绝、收藏库新路径）。
- 手测：`--dump-config` 输出 name 索引且 `api_key` 只报有/无；Debug 输出首启生成的 `.data/config.yaml` 与模板字节一致（注释保留）；`.data` 被同名文件占住时 CLI 退出码 1（stderr 提示）、GUI 弹错后退出码 1；`dev.ps1 build -ForceBump` 正确递增模板版本段；`dev.ps1 publish` 的 zip 恰为 `NaniKore.exe` + `config.template.yaml`（版本 0.4.0、含注释），从 `artifacts\win-x64` 跑过一次应用后再发布，`.data` 不入包。

**遗留**：迁移评估（正式版发布时决策，已登记 `_todo.md`）；旧安装残留不自动清理。
