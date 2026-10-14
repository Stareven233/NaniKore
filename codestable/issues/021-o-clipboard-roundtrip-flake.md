kind: issue
title: "真实剪贴板 round-trip 自测偶发失败：别名折叠是 WinForms 假象还是产品缺陷待判"
type: bug
status: resolved
created: 2026-10-12
resolved: 2026-10-13

---

## 症状

`SelfTest.Input.TestClipboardSnapshotRoundTrip`（真实系统剪贴板的快照→还原 round-trip）偶发失败，两种形态，细节格式集合完全一致：

- `FAIL: real clipboard snapshot exposes text, image and custom formats [formats=[UnicodeText, System.Drawing.Bitmap, NaniKore.SelfTest]]`
- `FAIL: real clipboard restore round-trips every captured format [formats=[UnicodeText, System.Drawing.Bitmap, NaniKore.SelfTest]]`

共同特征：`Clipboard.SetDataObject(source, copy: true)` 之后，本进程 `GetDataObject().GetFormats(autoConvert: false)` 有时只返回**每个别名组一个代表**的折叠集合——`UnicodeText`（而非 `Text`/`System.String`）、`System.Drawing.Bitmap`（而非 `Bitmap`）。`ClipboardPayloads.ReadImage` 按 `DataFormats.Bitmap` 取值，折叠视图下取不到图 → `DetachImage() is not null` 失败。

## 已查明的事实（2026-10-12 实测）

1. **与 019/020 的改动无关，是既有 flake**：未改动 HEAD 上背靠背 8 跑 4 败（50%）；同一环境带 019/020 改动 8 跑 1 败、另一轮 8 跑 0 败。HEAD 比改动后更频繁，排除「本次改动引入」。
2. **不是确定性代码缺陷**：干净进程里用同一段代码路径（SetDataObject copy:true → GetFormats(false) → Clear → 按格式写回 → 再读）紧凑循环 100 次，0 失败。问题依赖测试进程的累积状态或环境时序。
3. **不是 GDI 资源耗尽**：失败时进程 GDI 句柄仅 8 个（`GetGuiResources` 实测）。
4. **频率随时间波动**：同一份代码，不同时段 0/8、1/8、4/8 都出现过——与环境（很可能是剪贴板当前属主/延迟渲染行为）相关，而非与代码路径相关。

## 未查明（本次被主人叫停，待续）

**关键问题：WinForms 看到的折叠集合，是否反映系统剪贴板的真实内容？**

- 若真实内容也丢了标准 `CF_BITMAP`/`CF_UNICODETEXT`（只剩 .NET 私有格式名 `System.Drawing.Bitmap`），别的程序就粘不出图——那就是**产品缺陷**：`PasteInputAcquirer` 的还原路径偶而把用户的剪贴板还原成不可粘贴状态（019 的「绝不冲掉用户内容」目标被打破）。
- 若只是 WinForms 同进程枚举的假象（Win32 `EnumClipboardFormats` 仍报告 CF_BITMAP/CF_UNICODETEXT），则仅是**测试断言对别名脆弱**，改断言即可。

## 结论（2026-10-13 查实，两类问题都存在）

按上面顺序走完 Win32 地面真值对照后，结论是**产品缺陷与 WinForms 假象并存**，各自都修了：

### 缺陷一：还原写回不上报标准格式（真缺陷，已修）

矩阵探针（同刻对照 WinForms 视图与 Win32 `EnumClipboardFormats`）证明：`System.Drawing.Bitmap` 条目带 `autoConvert: false` 写回时，DataStore 只上报 .NET 私有格式名，系统剪贴板上**没有 `CF_BITMAP`**——还原后画图/浏览器/Word 只认标准格式，粘不出图，019 的「绝不冲掉用户内容」在图像上被打破。改为**仅位图条目带 `autoConvert: true`**（其余格式精确写回，保持快照的格式集合不变）。

### 缺陷二：图像读取口径脆弱（真缺陷，已修）

剪贴板上的图像有三种存在形态：`CF_BITMAP`（真实程序复制来的，TYMED_GDI）、`.NET` 私有格式 `System.Drawing.Bitmap` 的序列化副本（还原写回的产物）、两者并存。老口径 `GetDataPresent(Bitmap) + GetData(Bitmap)`：私有副本上 `TryGetData<Image>` 被 Nrbf 根类型校验挡下（根类型是 Bitmap 不是 Image）、纯 `CF_BITMAP` 形态下 TYMED_GDI 与 TYMED_HGLOBAL 不匹配直接取不到。改为 `TryGetData<Bitmap>(Bitmap, autoConvert: true)`，三条路都通。

### 缺陷三：写入被别的程序抢占时静默丢格式（真缺陷，已缓解）

`SetDataObject` 在别的程序正开着剪贴板时会静默丢掉部分格式而照返回成功（96 次还原里实测 2 次整个格式组消失）。`WinFormsClipboardGateway` 的 `Restore` 与 `SetDataObject` 改为写入后核对：每个格式名都在、且文本与图像按产品口径真读得回来（只核对名字不够——Windows 会留下渲染失败的坏壳格式名），缺了就整份重写（3 次、间隔 50ms）。

### 缺陷四：快照读取偶发坏样本（真缺陷，已修）

最隐蔽的一条。实测抓到现场：剪贴板完全健康（Win32 真值四个格式俱全、逐格式都读得到），**而微秒前拍出的快照偏偏少了自定义格式条目**——同一次读取，毫秒后重读就正常。同类样本还有「图像读成 null」「文本条目读成空串而 `ReadText` 拿到原文」。坏样本与好样本只差一次重新读取。修法：`Snapshot()` 读到不全就重取（3 次、间隔 20ms），留更全的那份；`Capture` 里文本条目读到空串时按 `ReadText` 的稳通道修正。

### WinForms 假象（测试侧，已修）

WinForms 的 `GetFormats(false)` 折叠视图会掩盖真实丢失，断言改为直接点名标准格式：新增 `a restored clipboard keeps the standard formats other apps can paste`（`Bitmap` + `UnicodeText` 必须在位）；测试的写回也改走 `gateway.SetDataObject`，与产品同一路径。

### 量化

| 阶段 | 失败率 |
| --- | --- |
| HEAD（修复前） | 4/8（50%） |
| 修完缺陷一/二/三 | 2/40、4/40、3/20（约 5–10%） |
| 修完缺陷四（快照重取） | 0/40 + 1/60（残留形态即「图像读成 null」，由重取的完整性信号覆盖） |
| 修完缺陷一~四，剪贴板被 MWB 持续持有 | 补跑 90 分钟 0 轮有效（全被 wedge，见文末） |
| 主人退出 MWB 后、修完缺陷五/六/七前 | autorun 20 轮：run#1 即失败（1/21 ≈ 5%），形态＝原始症状 |
| 修完缺陷五/六/七后 | autorun 20/20 + 30/30 = **50/50 有效全绿**（0 失败 0 跳过）；外来图像探针 40/40 保留（修复前 10/10 丢失） |

> 注：最后一项 20+ 连跑的复跑量化尚未收口——本机剪贴板此刻被另一进程持续持有（`OpenClipboard` 连续 2000 次全部失败，HRESULT `CLIPBRD_E_CANT_OPEN`，自测该用例按既有设计宽容跳过），需在剪贴板空闲时补跑确认。

## 建议的下一步

1. 取地面真相（**必须在工作目录内写探针或走自测钩子**——主人不允许在工作目录外落文件）：Win32 `OpenClipboard` + `CountClipboardFormats` + `EnumClipboardFormats` 直接枚举系统剪贴板，与同刻 WinForms 的 `GetFormats(false)` 对照；循环到捕获到折叠视图为止，记录两侧格式集合。
2. 按结论分流：
   - 真实内容确实丢失 → 产品修复：还原写回时保证标准格式（`CF_BITMAP`/`CF_DIB`/`CF_UNICODETEXT`）在位，或改为一律以标准格式重建；补自测锁定。
   - 仅是 WinForms 假象 → 测试修复：断言改为别名宽容（存在任一 Bitmap 系格式且能读回图即算过），并对真实剪贴板用例加有限重试。
3. 修完复跑 20+ 次量化失败率归零。

## 影响面

- 自测：当前套件存在既有 flake，**红跑不一定是回归**，排查时先看是否是这条（形态见上）。
- 产品（若结论为真缺陷）：粘贴热键的剪贴板还原/OCR 追加写回路径，用户复制的图像可能还原后无法被其他程序粘贴。

## 待主人裁决

1. 是否按上面顺序继续查（先 Win32 地面真相，再按结论分流修产品/修测试）？
2. 若结论是产品缺陷，修复范围是否包含「追加写回」路径（同用 `SetDataObject`，同样可能折叠）？

## 补跑量化进展（2026-10-14 实测，未收口）

主人指派收口「修完缺陷四后的 20+ 连跑量化」。执行情况：

- 会话开局剪贴板空闲（OpenClipboard 200/200 成功），单轮自测三条真实剪贴板断言全 PASS（快照三格式俱全 / 还原 round-trip / 还原后 Bitmap+UnicodeText 标准格式在位）。
- 随后连跑 55 轮攒量：仅前 2 轮真实触过剪贴板（全绿），第 3 轮起系统剪贴板被第三方进程持续占用，其余 53 轮全部**静默跳过**（ExternalException 被宽容吞掉、退出码仍 0）——即主人此前记的「另一进程持续持有」在本机复现，且这次是在连跑过程中被卡住的。
- 卡死特征：OpenClipboard 100% 失败（ERROR_ACCESS_DENIED，OLE 侧即 CLIPBRD_E_CANT_OPEN）；GetOpenClipboardWindow / GetClipboardOwner 均为 NULL（持有方以 `OpenClipboard(NULL)` 方式持有）；序列号冻结在 60180 近 1 小时；本会话进程全部空闲、无 NaniKore/dotnet 存活，排除自家泄漏。头号嫌疑 MouseWithoutBordersHelper（无窗口 helper、04:45:54 启动、无 TCP 对端＝跨机剪贴板共享对端不在线，MWB 是 Windows 剪贴板卡死的经典元凶）；Quicker / 坚果云剪贴板同步 / DataExchangeHost 不能排除，全部空闲无窗口，不杀进程无法进一步定位（未擅自动主人的进程）。
- 测试侧已修：宽容跳过原是静默的，「全绿」分不清「跑过且过」与「根本没跑」——本次 53 轮假绿正源于此。按 020 立的 Skip 惯例改为明示 `SKIP: 真实剪贴板不可访问…`（退出码仍 0）。
- 已挂后台自动补跑器 `artifacts/clipboard-quant/autorun.ps1`（90 分钟预算）：每 10s 探测剪贴板，一旦空闲即自动连跑，仅「三条断言真实执行过且全 PASS」的轮次计数，攒满 20 轮有效自动收工，逐轮记录在 `artifacts/clipboard-quant/autorun.log`。剪贴板不释放则量化继续顺延，不假装收口。

## 收口 + 新缺陷（2026-10-14 深夜实测，MWB 退出后）

主人退出 MouseWithoutBorders 后剪贴板立即释放（序列号从冻结的 60180 重置、OpenClipboard 200/200 成功），**wedge 元凶确认是 MWB**。按「建议的下一步」继续走 Win32 地面真值对照，结论：既有 flake 没收口干净，且对照过程中挖出三个新缺陷——都已修复并量化。

### 修复前的残留（ autorun 20 轮，run#1 即失败，1/21 ≈ 5%）

失败形态正是本 issue 的原始症状：还原后 `formats=[UnicodeText, System.Drawing.Bitmap, NaniKore.SelfTest]`——没有 `Bitmap` 标准格式（同刻 Win32 枚举证实 CF_BITMAP 真不在位，不是折叠假象）、round-trip 图像读成 null。

### 缺陷五：外来图像剪贴板还原后图整个消失（新，100% 复现，已修）

最严重的一条，且此前测试套件完全瞎眼。用 Win32 直写「纯外来」剪贴板（EmptyClipboard 后只放 CF_BITMAP+CF_UNICODETEXT，模拟用户从浏览器/截图工具复制）走产品快照→还原路径：

- 快照 `snapshotImage=ok`（强类型 autoConvert 通道读得到图），但**格式条目为空**——`GetData("Bitmap", autoConvert:false)` 对 TYMED_GDI 的 CF_BITMAP 永远返回 null，条目被跳过；
- 而 `WriteTo` 只写 `_formats` 条目、不写自持 `_image` → 还原后剪贴板只剩文本，**CF_BITMAP 整个消失，用户的图没了**。探针 10/10 复现。
- 这就是 019「绝不冲掉用户内容」在最常见真实场景（复制图→按热键→前台没回应→还原）上的破防；自测此前的写入必然附带 .NET 私有格式，永远走不到这条路径。
- 修法：`Capture` 新增「有图但没有图像格式条目时，补一条标准 `Bitmap` 条目（克隆位图）」的兜底；同时 `Capture` 接受 Win32 地面真值兜底参数（见缺陷六）。新增永久自测 `foreign image clipboard snapshot keeps the image` / `restored foreign image keeps CF_BITMAP other apps can paste`（Win32 直写外来剪贴板→快照→还原→点名 CF_BITMAP 必须在位）。修复后外来探针 **40/40 保留**。

### 缺陷六：文本坏样本与图像坏样本的同一盲区（新，已修）

300 轮 × 多批探针 + Win32 真值抓到现场：剪贴板完全健康（Win32 枚举原文/原图俱全），而按产品口径**文本读成空串、图像读成 null，且连续多次读取都读不出**（`textBack=''` 而 `win32Text='剪贴板原文'`）。根因：`IsComplete` 的完整性信号与捕获读的是同一个 DataObject——两边都读到空就互相「印证」为「完整」，缺陷四立的重取机制被绕过。修法：网关新增 `ReadSystemTruth()`（一次 Win32 直读：有没有图像格式、CF_UNICODETEXT 实际内容、CF_BITMAP 位图副本），WinForms 通道读到空时按真值重捕；「真值说有而快照没有」才判坏样本触发重取；真值读不到（打不开剪贴板）按无信号处理，绝不提前收工。

### 缺陷七：`Landed` 写入核对盲区（新，已修）

`Landed` 只核对「格式名都在 + 文本与图像按产品口径读得回」，而 .NET 私有格式 `System.Drawing.Bitmap` 的序列化副本能让 `TryGetData<Bitmap>` 成功——CF_BITMAP 整个丢了也放行（探针实测抓到还原后 Win32 无 CF_BITMAP、私有副本读不出的坏状态，3 次重写都没恢复）。修法：写了图像就点名要求系统剪贴板上 `Bitmap` 标准格式在位，缺了整份重写。OCR 追加写回路径同路受益。

### 本轮实测数据（探针日志在 `artifacts/clipboard-quant/probe-run*.txt`，临时 `--clip-probe` 钩子已删）

- 高频紧循环 300 轮/批 × 3 批：干净 271–275/300，**wedge（被别的程序短暂持有）23–27/300**，A/B/C 类异常 0–2/300。wedge 是高频写入下的主导矛盾，产品路径对此有兜底（还原失败→面板提示+照常采用快照内容）；测试侧明示 SKIP。
- 剩余 2/300 异常经甄别都不是数据丢失：一次是验证读取时刻的 flake（Win32 真值证明内容完好地在剪贴板上），一次是探针自身的 Win32 枚举撞上 wedge 的假象。
- 修复后 autorun：**20/20 + 30/30 有效全绿**（此前同条件 1/21 失败）。

### 关于「运行时成功率」的结论（主人担忧的回答）

测试环境比运行时苛刻一个量级：探针是毫秒级背靠背全量读写（每轮 6 次剪贴板操作），autorun 是 10 秒一次的全套自测进程churn；而运行时是**一次热键一次 `Acquire`**，人肉 pacing。即便如此，修复后 50/50 有效轮全绿、外来图像 40/40 保留。且运行时的每一类已识别失败都有兜底：wedge→提示+照常翻译；坏样本→Win32 真值兜底+重取；写坏→核对重写；仍失败→面板出 `Notice`，内容从进程自持快照采用。**残留风险是有界的**：自定义格式的内容正确性无法通用核对（只核名），以及还原连续 3 次都写不进时的最终放弃（面板提示）。

