---
kind: issue
title: "首次公开发布加固清单"
type: chore
status: open
created: 2026-11-11
---

# 首次公开发布加固清单

NaniKore 已具备截图 OCR、本地注音、翻译精讲和生词收藏的完整流程。本事项集中处理首次公开发布前的可靠性、第三方分发材料和用户说明，按下列顺序逐项推进。

本文件是发布加固的主入口。每项均有稳定编号、代码落点和验收条件；后续处理时更新本文件的状态与证据，复杂事项再按实际需要拆为独立 Issue 并回链，不预先创建一批空文档。

## 已确认的边界与验证

- **HTTP OCR 服务不纳入本轮加固。** 服务默认关闭、不会作为宣传功能；主人确认主动启用者熟知风险。此前提出的监听、路径、UNC、连接预算等问题不列为本次发布阻塞。
- **最终 ZIP 解压使用已由主人测试通过。** 不把这项重复列为待验证，也不将该结论扩大为所有异常场景均已验证。
- **不做敏感信息检查。** 本轮不扫描工作区或 Git 历史中的敏感信息，不读取实际密钥。
- **自测已补跑通过。** 2026-11-11，主人退出本地应用后执行 `./scripts/dev.ps1 test`，退出码 0，输出 `All self-tests passed (1641 checks).`。这是本轮 Debug 无 UI 自测结果，不代表新增问题已有回归覆盖，也不是本轮重新验证 Native AOT。
- 修复按下方各项状态逐步实施。此前审查结论以代码分析为主；没有实际复现的项目在下面明确标注。
- 不新增 Anki、跨平台或内置本地翻译运行时等功能；发布无需等待这些能力。

## 推进顺序

P1 为建议正式推广前完成的事项；P2 为发布质量改进，应逐项处理或明确记录延期原因。先处理会破坏用户状态、阻断首次使用的问题，再处理请求与数据保护，最后补分发和介绍材料。许可证事项可提前并行核实。

| 编号 | 优先级 | 事项 | 状态 |
| --- | --- | --- | --- |
| R01 | P1 | 注音词典下载后的文件移动冲突 | 已修复并验证 |
| R02 | P1 | 剪贴板文件列表还原丢失 | 已修复并验证 |
| R03 | P1 | 取消翻译后新输入的按钮状态 | 已修复并验证 |
| R04 | P1 | 收藏数据库异常的 UI 隔离 | 已修复并验证 |
| R05 | P1 | 翻译重定向与凭据传输边界 | 已修复并验证 |
| R06 | P1 | 第三方许可与仓库授权 | 已修复并验证 |
| R07 | P2 | 小工作区与跨屏结果窗口定位 | 已修复并验证 |
| R08 | P2 | 流式正文超时与设置热应用 | 超时待修复；热应用待复现 |
| R09 | P2 | 配置及密钥读取失败后的原文件保护 | 待修复 |
| R10 | P2 | 发布归档清单与完整包哈希校验 | 待改进 |
| R11 | P2 | 用户入口、离线说明及默认值一致性 | 待整理 |
| R12 | P2 | 博客素材与性能数据 | 待准备 |

## R01：让词典首次下载正常完成

**代码依据：** [PhoneticDependencies.cs](../../src/NaniKore/Phonetics/PhoneticDependencies.cs) 的 `DownloadWithRetryAsync`。校验临时文件的 `using var verifyStream` 尚在作用域中，后续即执行 `File.Move`；流以 `FileShare.ReadWrite` 打开，没有删除共享权限。

**用户影响与根因：** Windows 下源文件句柄会阻止移动，三语词典首次下载均经过此路径。已在 Windows 上用真实 ModelScope 下载复现：修复前在 `File.Move` 处抛出文件被占用的 `IOException`。

**处理与验收：**

- [x] 在移动前释放校验流，保持固定 SHA-256 校验及取消/失败清理行为。
- [x] 在 Windows 隔离空目录下载完整日语词典的四个文件，全部通过清单 SHA-256 校验；用 LibNMeCab 加载新下载词典，确认「日本語」读音为「ニホンゴ」。
- [x] 校验失败或取消时，不把未验证文件当作已就绪资源；验证哈希失败及预先取消均保留已安装文件，并且不残留 `.download` 文件。

**执行记录（2026-11-11）：**

- 只将校验流改为显式 `using` 块，在移动前释放，并添加中文注释解释 Windows 句柄约束；未改变下载接口、固定哈希、文件清单及失败清理逻辑。
- 隔离验证工程位于 Git 忽略的 `artifacts/r01-download-verification/`，直接链接实际下载器源码，仅将目标文件定位到独立测试目录，不访问用户数据或替换现有词典。使用本机 .NET 11 SDK 执行 `dotnet run --project artifacts/r01-download-verification/R01.csproj -p:NuGetAudit=false`。
- 同一真实下载检查在修复前失败、修复后通过；另验证成功下载替换旧目标文件、错误哈希保留旧文件、预先取消保留旧文件及临时文件清理。完整日语词典下载与读音查询也已通过。
- `./scripts/dev.ps1 test`：退出码 0，`All self-tests passed (1641 checks).`。本次未重新构建或验证 Native AOT，也未进行设置页点击下载手测。
- R01 已完成；整份发布加固事项保持 open。当前稳定功能规格未改变，无需新增兼容逻辑。

## R02：选取翻译不破坏用户已复制的文件

**复现场景：** 资源管理器复制文件 → 切到不响应 Ctrl+C 的窗口 → 触发选取翻译 → 等待超时 → 回资源管理器粘贴。

**代码依据与根因：** [PasteInputAcquirer.cs](../../src/NaniKore/Input/PasteInputAcquirer.cs) 的 Win32 快照与还原。修复前 `CF_HDROP` 只记录空的 `FileDrop` 标记；还原先 `EmptyClipboard()`，再跳过该格式，无法还原文件列表。现已保留完整 HGLOBAL 数据块，并使用标准 `CF_HDROP` ID 写回。

**处理与验收：**

- [x] 保留并正确恢复 `CF_HDROP` 的真实数据，复核相关文件复制格式的往返行为。
- [x] 增加上述场景的真实 Win32 回归，不仅依赖假剪贴板测试。
- [x] 确认正常文本、图片、自定义格式与新 Ctrl+C 结果仍按原有输入契约处理。

**执行记录：**

- 复用原有 HGLOBAL 复制路径，将 `CF_HDROP` 的完整 `DROPFILES` 头和路径数据保存进快照；还原时保留标准格式 ID，不将 `FileDrop` 注册为同名私有格式。文本、位图与注册格式的原有处理路径保持原契约。
- 新增 [SelfTest.ClipboardFiles.cs](../../src/NaniKore/Diagnostics/SelfTest.ClipboardFiles.cs)，直接通过 Win32 写入文件列表、`Preferred DropEffect`（复制/剪切）及 `Shell IDList Array`。通过真实网关和输入获取状态机执行完整 600ms 轮询超时、空 Ctrl+C 回应还原；使用独立 Shell API `DragQueryFileW` 验证两条含中日文及空格的路径，并逐字节核对上述 Shell 格式。另验证新 Ctrl+C 文本和文件列表不被旧快照覆盖。测试结束恢复原剪贴板，剪贴板不可访问会使本回归失败，不静默跳过。
- 修复前运行 `./scripts/dev.ps1 test`：4 项路径检查失败，分别覆盖复制/剪切与超时/空回应两组条件；Shell 注册格式仍在，明确复现文件列表单独丢失。
- 修复后运行 `./scripts/dev.ps1 test -ShowPass`：退出码 0，`All self-tests passed (1662 checks).`；本项真实 Win32 回归全部执行通过，既有文本、图片、自定义格式往返及输入状态机检查也通过。详细输出位于 Git 忽略的 `artifacts/r02-self-test.log`。
- 回归以无操作复制委托模拟不响应 Ctrl+C 的窗口，不向当前桌面发送按键；路径为测试数据，未实际执行资源管理器粘贴或文件复制。此次证据覆盖真实系统剪贴板和 Shell 路径读取，未进行资源管理器手测或 Native AOT 验证。
- R02 已完成；整份发布加固事项保持 open。关闭候选：现有剪贴板还原契约应包含文件列表完整往返；稳定输入优先级及终态规则未改变。

相关既有事项：[018](018-o-选中与剪贴板翻译热键.md)、[019](019-o-paste-input-hardening.md)、[021](021-o-clipboard-roundtrip-flake.md)。历史旧实现的往返结果不能代替本项回归。

## R03：新输入能从取消中的旧翻译恢复可操作状态

**复现场景：** 关闭选取后自动翻译 → 手动启动较慢的翻译 A → 完成前通过热键载入文本 B。

**代码依据与根因：** [ResultPanelWindow.axaml.cs](../../src/NaniKore/App/ResultPanelWindow.axaml.cs) 的 `Reset` 原先没有恢复翻译按钮；[App.axaml.cs](../../src/NaniKore/App.axaml.cs) 的旧任务 `finally` 有身份检查，接纳新输入后不再恢复旧请求状态。现已由 `Reset` 调用 `SetTranslationInProgress(false)` 统一恢复操作状态；该缺口已由真实面板自测复现，尚未进行热键真机复现。

**处理与验收：**

- [x] 接纳新输入时重置完整操作状态，B 的手动翻译按钮可用。
- [x] A 的迟到结果与收尾不能覆盖 B 的内容，也不能干扰 B 已开始的新请求（保留既有守卫，代码核对；本次未自动化驱动宿主异步交错）。
- [x] 覆盖“进行中 → 取消 → 新输入 → 手动翻译”的状态转换回归（真实面板状态转换；宿主取消隔离由代码核对）。

**执行记录：**

- `Reset` 在装载新原文前调用现有 `SetTranslationInProgress(false)`，统一恢复规范化、注音、翻译按钮，替换原有两个按钮的单独启用。新输入的可操作状态由面板重置负责，不依赖旧任务收尾；没有修改请求生命周期或增加兼容逻辑。
- 在 [SelfTest.Ui.ResultPanel.cs](../../src/NaniKore/Diagnostics/SelfTest.Ui.ResultPanel.cs) 新增 `TestResultPanelResetDuringTranslation`：分别模拟 A 正在思考和输出译文，直接在真实 Avalonia 面板上 Reset B，独立断言三个按钮启用及旧内容/辅助状态清空，然后通过实际点击事件确认发起的是 B，B 运行时三个按钮禁用、完成后恢复。
- 修复前 `./scripts/dev.ps1 test`：两条 `new input restores all manual actions before A finishes` 检查失败，证明原 `Reset` 无法恢复手动操作。修复后 `./scripts/dev.ps1 test -ShowPass`：退出码 0，`All self-tests passed (1672 checks).`；日志位于 Git 忽略的 `artifacts/r03-self-test.log`。
- 核对宿主：`AcceptNewInput` 调用 `CancelTranslation` 递增 generation、清空旧 CTS 并取消；流增量的实际 UI 回调检查 generation，完成/错误回调检查 `IsCurrentRequest`；`finally` 的 CTS 身份判断和清理在同一 UI 回调内完成。旧 A 因此不能回写 B 或启用运行中的 B；上述守卫本次未修改。
- 本次新增回归未实际启动宿主网络请求、取消真实 A 或调度 A/B 迟到回调；没有进行慢速服务热键手测或 Native AOT 验证。自动化证据覆盖导致缺陷的面板状态转换，宿主异步隔离结论来自代码核对。
- R03 已完成；整份发布加固事项保持 open。稳定输入和请求隔离契约未改变，无需修改 Project Spec。

## R04：收藏失败可见且不导致应用退出

**代码依据：** [ExplanationCardView.axaml.cs](../../src/NaniKore/App/Controls/ExplanationCardView.axaml.cs)、[FavoritesPage.axaml.cs](../../src/NaniKore/App/Settings/FavoritesPage.axaml.cs)。收藏/删除的 UI 事件缺少异常隔离，部分星标在持久化前即变成成功态。异常逃逸有代码依据，现已在自测宿主中实测：损坏库下 `IsFavorited` 抛出的 `SqliteException` 直接冒泡终止进程，退出码 `-532462766`。

**处理与验收：**

- [x] 收藏、删除、编辑与列表读取失败时给出可理解的提示，应用保持可用。
- [x] 落库失败后星标和列表不显示虚假成功；允许恢复条件后重试。
- [x] 用只读、排他锁和损坏数据库验证主要错误路径，不覆盖真实收藏库。

**执行记录（2026-11-11）：**

- 新增 [FavoriteFailure.cs](../../src/NaniKore/Favorites/FavoriteFailure.cs)：按 SQLite 主结果码（5/6 占用、8 只读、11/26 损坏或非数据库、14 无法打开）及权限/IO 异常生成「{动作}失败：{原因}（{恢复建议}）。」。只做提示，不改变服务契约、异常语义与各接口返回值。
- [ExplanationCardView.axaml.cs](../../src/NaniKore/App/Controls/ExplanationCardView.axaml.cs)：星标读取改走 `IsFavoritedQuietly`，库不可读时按未收藏降级，不打断流式渲染；词汇/语法收藏统一到 `ToggleFavoriteAsync`，先落库、成功后才切换星标并发 `FavoriteToggled`，返回 false 或抛异常均只发新增的 `FavoriteFailed` 并保持原星标；删除未命中（条目已被移除）视同完成。
- [SelectionBubblePopup.axaml.cs](../../src/NaniKore/App/Controls/SelectionBubblePopup.axaml.cs)：拆分「读库状态」与「刷星标样式」，`ToggleFavoriteAsync` 失败时星标与气泡保持不变便于直接重试，同样新增 `FavoriteFailed`。[ResultPanelWindow.axaml.cs](../../src/NaniKore/App/ResultPanelWindow.axaml.cs) 把卡片与气泡两处失败事件接到状态栏（错误样式）。
- [FavoritesPage.axaml.cs](../../src/NaniKore/App/Settings/FavoritesPage.axaml.cs)：`Refresh` 返回是否读取成功，失败时保留上一次成功快照并提示，不再把库不可用显示成空列表；删除与编辑失败都捕获异常并给出提示，编辑失败回滚内存字段快照，避免取消编辑后展示未落库内容；刷新按钮只在读取成功时提示「已刷新」。
- 新增 [SelfTest.Ui.FavoriteFailures.cs](../../src/NaniKore/Diagnostics/SelfTest.Ui.FavoriteFailures.cs) 共 25 项检查，故障全部在系统临时目录构造：损坏库文件（26）、只读属性（8）、独占文件句柄（14）。覆盖卡片渲染与词汇收藏/取消收藏、气泡打开与收藏、收藏页损坏库读取、重置库文件后刷新恢复、只读库下删除与编辑失败后清除只读属性重试成功。
- 修复前证据：临时把读/写路径还原为旧行为（星标先置成功态、`IsFavorited` 不捕获）后执行 `./scripts/dev.ps1 test`：2 项失败（`card keeps an unfavorited star when persisting fails`、`card keeps a favorited star when removing fails`），随后 `SQLite Error 26: 'file is not a database'` 未处理、进程退出码 `-532462766`；日志位于 Git 忽略的 `artifacts/r04-evidence/before-fix.log`。
- 修复后 `./scripts/dev.ps1 test`：退出码 0，`All self-tests passed (1697 checks).`（上一轮基线 1672，新增 25 项）；日志位于 `artifacts/r04-self-test.log`。
- `SQLITE_BUSY` 真机争用路径未自动化：Microsoft.Data.Sqlite 默认 busy timeout 为 30 秒，自测中构造 `BEGIN EXCLUSIVE` 会阻塞约 30 秒，因此改用独占文件句柄覆盖「被占用/无法打开」表现；错误码 5/6 的中文映射仅由 `FavoriteFailure` 分支承载。
- 未做真实桌面手测与 Native AOT 发布包验证；未读取或修改实际 `.data/collections.db`。
- R04 已完成；整份发布加固事项保持 open。收藏失败语义已作为稳定事实补入 [Project Spec](../spec/index.md) 的本地知识库条目。

相关能力承诺见 [Epic 001 / Issue 007](../epics/001-o-学习型演进-精讲假名与生词库/issues/007-o-sqlite知识库与设置收藏tab.md)。

## R05：明确翻译请求可以把凭据和图文发往哪里

**代码依据：** [TranslationProviderBase.cs](../../src/NaniKore/Translation/Providers/TranslationProviderBase.cs) 使用默认自动重定向；Claude/Gemini 使用自定义密钥头。跨主机重定向存在转发密钥头的路径，307/308 还可能转发正文。官方默认端点未被证明会触发此行为。

**处理与验收：**

- [x] 关闭自动重定向，或实施明确的同源策略；拒绝不符合策略的跳转并给出可理解错误。
- [x] 仅使用虚构凭据和本地受控端点，验证跨源跳转不泄露自定义密钥头及图文载荷。
- [x] 明确远程明文 HTTP 的处理策略：远程要求 HTTPS、本地回环允许 HTTP；在连接测试和真实翻译中保持一致。
- [x] 保留 OpenAI/Claude/Gemini 及本地兼容端点的正常调用能力。

本项仅涉及翻译客户端，不包含已排除的 OCR HTTP 服务。

**执行记录（2026-11-11）：**

- [TranslationProviderBase.cs](../../src/NaniKore/Translation/Providers/TranslationProviderBase.cs)：生产 `HttpClientHandler` 改为 `AllowAutoRedirect = false`；`SendAsync` 内实现「仅同源跟随」——只自动跟随协议 + 主机 + 端口完全一致的重定向，303 按语义转 GET 并丢弃正文，跨源、https→http 降级一律抛出新增的 `ProviderTransportPolicyException`（中文说明跳转两端与拒绝原因），最多跟随 5 跳防环；同源跟随复用 ByteArrayContent 上传缓冲（所有权转移，不整份复制），请求头（含密钥头）仅在同源时继续携带。另增 `ValidateTransportSecurity`：远程明文 HTTP 拒绝，仅 localhost/127.0.0.1/::1 允许，连接测试与真实翻译同一入口、策略天然一致。
- [UnderstandContracts.cs](../../src/NaniKore/Translation/UnderstandContracts.cs) 新增 `ProviderTransportPolicyException`；[ProviderErrors.cs](../../src/NaniKore/Translation/ProviderErrors.cs) 将其映射为原文透出（消息本身已是可行动中文）。Claude/Gemini 构造函数补上可选 `transport` 注入以便真实网络栈回归。
- 新增 `TestProviderTransportBoundary`（[SelfTest.Translation.cs](../../src/NaniKore/Diagnostics/SelfTest.Translation.cs)，14 项检查）：同源 302 跟随后密钥头与 POST 正文逐字保留；307/308/301/302 跨源到 `attacker.test` 均只发出 1 个请求且抛出点名目标主机的错误（密钥头与图文载荷零转发）；Claude `x-api-key` 与 Gemini `x-goog-api-key` 跨源同样被拦截；同源 303 转 GET 丢正文；重定向环在 6 次请求后中止；远程 `http://provider.test` 拒绝且不发出任何请求；`http://127.0.0.1` 与 `http://localhost` 回环端点照常可调用。全部使用虚构密钥与本地受控假 handler，不访问真实网络。
- `./scripts/dev.ps1 test`：退出码 0，`All self-tests passed (1711 checks).`（上一轮基线 1697，新增 14 项）。日志位于 Git 忽略的 `artifacts/r05-debug2.log`。
- 官方 HTTPS 端点正常不发起跨源重定向，此修复对其无感；第三方兼容端点若依赖跨主机跳转将被明确报错提示检查 Base URL，属预期加固而非回归。Native AOT 发布包未重新构建验证；未进行真实网络手测。
- R05 已完成；整份发布加固事项保持 open。翻译请求传输边界（同源跟随 + 远程 HTTPS）应作为稳定事实补入 Project Spec 的翻译条目。

相关既有事项：[022-x-ff-nvidia-thinking-parameter-400](022-x-ff-nvidia-thinking-parameter-400.md)。

## R06：按实际交付方式补齐授权材料

**已知依据：** [NOTICE](../../NOTICE)、[NaniKore.csproj](../../src/NaniKore/NaniKore.csproj) 与 [LibNMeCab 上游说明](https://github.com/komutan/NMeCab/#ライセンス)。上游声明 GPL/LGPL 双许可；当前 NOTICE 混入 Classpath Exception，并以替换 DLL 解释义务，但 Native AOT 包中没有独立的 LibNMeCab.dll。不能据当前声明直接认定已满足分发条件。

**处理与验收：**

- [x] 核实实际使用版本的授权原文，确定 LibNMeCab 在 AOT 链接下可落实的分发方案；不得仅改文案后视为完成。
- [x] 根据直接及传递依赖补齐需要随包提供的许可文本、版权、免责声明、必要 NOTICE 和来源。
- [x] 补齐日语词典许可原文，使轻量包按需下载和完整包均满足附带要求。
- [x] 明确 OCR 权重的上游来源与再分发许可；补齐 CC-CEDICT 派生词典的归属、许可链接及修改/派生说明。
- [x] 同步 NOTICE 中已漂移的依赖版本，移除与实际依赖或交付方式不符的说明。
- [x] 主人决定仓库自身许可证后落实 `LICENSE`；若暂不授予开源许可，对外使用“公开源码”而不是“开源”。
- [x] 最终发布包实际携带所需授权材料；允许为此调整当前“根目录 7 文件”契约。

仓库授权选择与 AOT 分发方案涉及真实取舍，实施时给出具体选项，不擅自替主人选择许可证。

**执行记录（2026-11-11）：**

- 主人选定仓库许可证为 **GPL-3.0-or-later**，版权署名 `Copyright (C) 2026 NaniKore Contributors`。
- **LibNMeCab 授权实证**：0.10.2 的 NuGet nuspec 为 `GPL-2.0-or-later OR LGPL-2.1-or-later`（SPDX 表达式）。Native AOT 发布将 LibNMeCab 静态编译进 `NaniKore.exe`，发布包根目录逐项核对只有 `NaniKore.exe` + 4 个原生 DLL + `config.template.yaml` + `NOTICE`（+本次新增 `LICENSE`），不存在可单独替换的 LibNMeCab.dll。据此确认分发方案：按其 GPL-2.0-or-later 条款按 GPL-3.0 选用，整件作品随公开仓库提供对应源码（GPL-3.0 第 6 节）。原 NOTICE 中「GPL-2.0 with ClasspathException」与「替换 LibNMeCab.dll 保持可替换性」的表述与授权原文、交付方式均不符，已移除。
- **LICENSE 新建**：GPL-3.0 全文，与 gnu.org 权威文本逐行比对 0 差异（674 行），头部附署名与标准许可提示。
- **NOTICE 重写**：版本漂移修正（Avalonia 12.1.4→12.1.6、ProtectedData 10.0.0→11.0.0）；移除 Microsoft.Win32.SystemEvents（已不在 project.assets.json 依赖图，issue 046 起改走 Avalonia 主题监听）；补齐传递依赖 HarfBuzzSharp 8.3.1.3、SQLitePCLRaw 2.1.12、System.Numerics.Tensors 9.0.0、MicroCom.Runtime 0.11.6；Vecc 生成器标注为编译期参与、不随包分发；OCR 权重明确为 PaddlePaddle PP-OCRv6-small（Apache-2.0，HF 上游仓库链接、原样再分发、SHA-256 校验）；ONNX Runtime 原生 DLL 1.28.0（MIT，实测文件版本 1.28.0.20260724）；IPAdic 的 NAIST 许可全文内联为 NOTICE 第 3.1 节；CC-CEDICT 补齐归属标注、许可链接与修改/派生说明，并附 Unihan Unicode-3.0 链接。
- **IPAdic 随行方案**（主人定）：不做 ModelScope 清单上传、不在仓库另放文本，改为将许可全文内联进根目录 NOTICE——轻量包按需下载词典时 NOTICE 即随附声明，完整包另已有 `deps/phonetics/ja/IPAdic-COPYING.txt`，两种交付形态均满足 IPAdic「拷贝须附带版权声明与条款」的要求。zh 词典（CC BY-SA 4.0）按主人决定仅做 NOTICE 文档归属，不单独落盘归属文件。
- **发布包携带**：[NaniKore.csproj](../../src/NaniKore/NaniKore.csproj) 增加 `LICENSE` 的 None 项（Debug 输出与 publish 输出）；[dev.ps1](../../scripts/dev.ps1) 在 `build -Release` 与 `publish` 两处将 `LICENSE` 与 `NOTICE` 一并复制进发布输出。Project Spec 交付物目录契约由根目录 7 项调整为 8 项（新增 `LICENSE`）；README 的发布包文件树、「7 文件契约」表述与许可证章节同步更新。
- **验证**：`./scripts/dev.ps1 test` 退出码 0，`All self-tests passed (1711 checks).`（与 R05 完成后基线一致；本轮改动不触及运行期代码路径）。未重新构建 Native AOT 发布包；根目录 8 项与 `LICENSE` 随行的产物级复核并入 R10 打包清单改进。
- **边界记录**：关于页未增加许可提示行——GPL-3.0 第 5(d) 条的交互界面法定声明仅约束改编版的转发，本仓库作为权利人对原件分发不受该条约束；随包 NOTICE/LICENSE 已承担分发声明职能。
- R06 已完成；整份发布加固事项保持 open。仓库许可证口径（GPL-3.0-or-later）、LibNMeCab 的 GPL-3.0 选用与 IPAdic/CC-CEDICT 随行口径已作为稳定事实补入 [Project Spec](../spec/index.md) 的授权与分发条目。

## R07：结果窗口适应较小工作区

**代码依据：** [ResultPanelWindow.axaml.cs](../../src/NaniKore/App/ResultPanelWindow.axaml.cs) 的 `PositionNearSelection`。窗口宽高超过工作区时，`Math.Clamp` 上界小于下界会抛异常。

**处理与验收：**

- [x] 窗口大于目标工作区时仍可定位、阅读和操作，不抛异常。
- [x] 验证大屏拉宽后移至小屏、较高 DPI、任务栏压缩工作区的场景。
- [x] 覆盖边界几何回归；负坐标、多屏和混合 DPI 的真机证据按实际条件记录。

**执行记录（2026-11-11）：**

- 将 `PositionNearSelection` 的定位决策抽成纯函数 `ResultPanelWindow.ResolvePanelPlacement`；横向/纵向夹取上界改用 `Math.Max(工作区起点, 工作区终点 - 窗口尺寸)`，窗口大于工作区时上界收敛到下界本身，退化为对齐工作区左上角，不再触发 `Math.Clamp`「上界小于下界」的 `ArgumentException`。正常路径（下方优先、下方不足上移、横向夹取）逻辑不变；窗口保持 `SwpNoSize|SwpNoActivate|SwpShowWindow`，标题栏拖动、左上缩放握柄、内容滚动与 Esc 关闭在超大窗口下仍可达。
- 新增 `TestResultPanelPlacementGeometry`（[SelfTest.Ui.ResultPanel.cs](../../src/NaniKore/Diagnostics/SelfTest.Ui.ResultPanel.cs)）：覆盖常规下方落点、右缘横向夹取、下方不足上移、超高窗口对齐顶部、超宽窗口对齐左边、双向超限落回工作区原点、负坐标副屏几何，并做「工作区 × 窗口尺寸 × 选区（含极端负值/超出右下）」几何矩阵，断言任何组合都不抛异常且窗口始终与工作区相交、放得下时完整落入工作区。修复前的旧 `Math.Clamp` 在这些超限组合会抛异常，矩阵会直接失败。
- 真机验证：`artifacts/r07-placement-verification`（Git 忽略）在真实桌面窗口栈上经由 `PositionNearSelection` 驱动真实 `HWND` 与 `SetWindowPos`，用 Win32 `GetWindowRect` 读回窗口物理尺寸。`dotnet run --project artifacts/r07-placement-verification/R07Verification.csproj -p:ExposeR07Internals=true` 四项全 PASS：常规尺寸落点与决策一致且在工作区内；窗口放大到超过整个工作区时对齐工作区原点、不抛异常；右缘选区仍按原契约夹取；负坐标副屏几何与决策一致。为让该 scratch 工程可复跑而不污染产品，`NaniKore.csproj` 仅在传入 `-p:ExposeR07Internals=true` 时加 `InternalsVisibleTo R07Verification`，正常构建不暴露任何内部类型（无参数构建验证工程会因 `ResultPanelWindow` 不可访问而 CS0122）。
- 真机环境实测：单显示器 1920×1080，系统缩放 **125%**（DPI 1.25），工作区 **1920×1030**（任务栏压缩掉约 50px）。据此「较高 DPI」与「任务栏压缩工作区」由真机覆盖；面板 DIP 960×660 在 125% 下即物理 1200×825，与工作区交集判定、右缘夹取均正确。
- `./scripts/dev.ps1 test`：退出码 0，`All self-tests passed (1719 checks).`（上一轮基线 1711，新增几何回归 8 项）；运行日志见 `artifacts/r07-placement-verification/real-window-run.log`。
- **证据边界**：本机仅一块物理显示器，无真实左侧副屏/多屏，「大屏拉宽后移至小屏」以「窗口放大到超过当前工作区」等价模拟，「负坐标副屏」与「多屏混合 DPI」由纯决策几何矩阵覆盖、并经真机 `SetWindowPos` 路径复核同一决策，但未在第二块物理屏或跨屏拖拽下实测；未重新构建/验证 Native AOT 发布包；未做手动拖拽窗口后点击收藏的真实交互手测。
- R07 已完成；整份发布加固事项保持 open。超大窗口退化对齐工作区左上角属定位契约新增事实，如项目规格需登记可后续补入发布契约条目。

## R08：翻译卡流可结束，设置更新不误伤请求

**已确认的超时缺口：** [TranslationProviderBase.cs](../../src/NaniKore/Translation/Providers/TranslationProviderBase.cs) 使用 `ResponseHeadersRead`；后续 [SseReader.cs](../../src/NaniKore/Translation/SseReader.cs) 正文读取只有宿主取消令牌。`HttpClient.Timeout` 不能据此保证整个流式请求在配置时间内结束。

- [ ] 定义 `timeout_seconds` 的准确语义，决定总期限与无数据期限，文案和实现一致。
- [ ] 受控端点返回 200 响应头后永久不发正文，应用能结束等待、提示并恢复按钮。
- [ ] 正常持续输出的长回复不会被不合理截断；关闭窗口、新输入仍能及时取消。

**待复现的独立风险：** [App.axaml.cs](../../src/NaniKore/App.axaml.cs) 的 `ApplySettings` 重建 Provider，设置窗口尺寸回写也经过该路径。旧 HttpClient 被释放可能中断正在连接的请求，实际表现尚未验证。

- [ ] 用延迟响应端点验证翻译期间保存普通设置、调整设置窗口尺寸后关闭的行为。
- [ ] 仅在确认影响后修复对应生命周期；若不成立，记录证据并关闭该观察项，不为假设增加结构。

## R09：读取失败时保留配置和密钥原件

**代码依据：** [ConfigStore.cs](../../src/NaniKore/Config/ConfigStore.cs) 解析失败后返回默认配置，后续保存可能覆盖原文件；[SecretStore.cs](../../src/NaniKore/Config/SecretStore.cs) 解密失败后返回空映射，再保存新 Key 可能覆盖旧密文。

**处理与验收：**

- [ ] 区分不存在、格式错误、无权限和无法解密，不把读取失败等同于首次使用。
- [ ] 失败后后续保存不会静默销毁原配置或整份旧密文，提供明确的恢复/重置路径。
- [ ] 用临时测试数据验证损坏 YAML、损坏密文和不可读文件，不读取或修改实际凭据。
- [ ] 升级说明准确描述配置语义边界；不承诺未实现的自动迁移，不增加旧逻辑兼容层。
- [ ] 备份说明明确 DPAPI `CurrentUser` 的边界，不承诺换机器或用户后可直接恢复 Key。

## R10：发布包由明确清单构造

**代码依据：** [dev.ps1](../../scripts/dev.ps1) 的 `New-NaniKoreReleaseArchive`。完整包必要依赖主要做存在性检查；持久化输出目录使用排除名单复制，无法保证无额外残留。

主人的最终 ZIP 解压使用验收已通过；本项处理后续构建的可重复性与防错，不重新把现有包记为失败。

- [ ] 复用或统一依赖清单，打包完整包前校验固定哈希，拒绝空文件、截断和错误版本。
- [ ] 按交付白名单或隔离输出构造 ZIP，避免无关旧文件混入；包含 R06 所需授权材料。
- [ ] 核验轻量包/完整包条目及 `.data` 排除规则；生成供用户核对的发行 ZIP SHA-256。
- [ ] 如产生不再使用的文件，依项目约定移至回收站，不直接删除。

## R11：README 先帮助普通用户成功使用

- [ ] 快速开始前置 GitHub Release 下载、轻量包/完整包选择、解压运行和两把默认快捷键；源码开发命令后置。
- [ ] 明确完整包的离线范围是 OCR 与本地注音；翻译/精讲取决于服务配置，视觉模式会发送图片。
- [ ] 统一模板、内置默认值、注释和 README：当前 `paste_auto_translate` 模板为 `false`，注释和部分说明称默认开启，内置默认值也不同。采用经确认的默认行为，并覆盖省略字段/读取失败场景。
- [ ] 修正“覆盖升级不会冲掉任何本机数据”等绝对表述，说明退出应用后升级及备份 `.data` 的方式，并关联 R09 的实际保护行为。
- [ ] 源码发布说明补充 Windows Native AOT 的 C++ 构建工具链要求，区分运行发布包与自行编译。
- [ ] 将许可证、授权材料、平台和水平文本识别边界与实际交付对齐。

## R12：准备可验证的介绍材料

建议定位：**面向 Windows 的本地优先 OCR 与语言学习工具：本机识别屏幕文字、添加读音、收藏生词，也可连接自己选择的模型服务进行翻译和精讲。**

- [ ] 准备结果面板截图和“截图 → 注音 → 翻译/精讲 → 收藏”的短演示，使用可公开的示例内容。
- [ ] 如宣传“快速”，记录机器配置、图片尺寸、资源冷加载/热识别耗时；翻译延迟单独说明服务和网络条件。
- [ ] 避免把 AOT、小包体直接等同于 OCR 速度；避免“全部离线”“绝对安全”等超出能力边界的承诺。
- [ ] 选定首发版本与 Beta/稳定版标识，博客、README 和 Release 说明保持一致。

## 每项完成与整轮收尾

逐项处理时先复核当前代码，再做最小修复和与风险相称的验证。完成后勾选对应验收项，并追加具体命令结果或手测证据；全量自测通过不能代替问题专属回归。若实现改变既有稳定事实，同步相应 Project Spec 或关联 Epic Spec。

本事项保持 open，直到主人明确要求关闭。收尾时汇总 P1 完成证据、P2 的完成或延期决定，更新 [Project Spec](../spec/index.md) 中的发布契约、配置/请求行为与验证边界；需要公开仓库、发布 Release 或提交 Git 时按主人的明确授权执行。
