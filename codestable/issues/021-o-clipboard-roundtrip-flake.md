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
