---
kind: issue
title: "[快改] 英语注音两条自测缺词典时假失败，补 Skip 保护"
type: ff
status: closed
created: 2026-10-12
---

<!-- 快改痕迹：轻。读者只要 30 秒扫完。禁止迷你 Design。 -->

# [快改] 英语注音两条自测缺词典时假失败，补 Skip 保护

> **读者：** 以后搜到这条时——「改了啥、怎么信、动没动制度记忆」。  
> **自检（四答即可，可合成短段，勿空标题凑节）：** 做了什么 · 改了哪些文件 · 怎么验证 · 对 `codestable/` 有无影响。

---

`deps/phonetics/en/ipa_dict.db` 缺失时（`deps/*` 整体 gitignore，词典按需下载），自测报 2 条假失败：`手选英语：拉丁词元按英语注音`、`重置可用性后英语注音不受影响`。两条断言硬性要求 `Count == 1`，却没有金样本测试（ja/zh/en 三条）都有的 `IsAvailable → Skip` 保护；而 `TextAnnotatorRegistry.Annotate` 对缺词典的语言设计上就软降级（不产生区间），产品行为正确，红字是「环境没装词典」而非代码回归。本改动给两条补上同口径保护，并实证闭环。

- 改动：`src/NaniKore/Diagnostics/SelfTest.Phonetics.cs` — `TestRegistryLanguageRouting` 与 `TestRegistryResetAvailability` 的英语段加 `GetAnnotator("en")` + `IsAvailable` 守卫，未就绪即 `Skip`（文案说明原因）；顺手去掉 `GetAnnotator("en")!` 的 null 宽限。
- 改动：`deps/phonetics/{ja,zh,en}` — 按 `PhoneticDependencyManifest` 的 ModelScope 直链下载 9 个文件（63.7 MB）并逐个 SHA-256 校验通过（本机原先三语词典都在，疑似被清理；此为环境修复，非代码改动，`deps/` 已 gitignore）。
- 验证：词典在位 → `dev.ps1 test` 全绿（原 2 条失败转 PASS，3 条金样本从 SKIP 转真跑真过）；临时移走 en 词典复跑 → 2 条转 SKIP、金样本 SKIP、`All self-tests passed`，随后还原词典再跑一次仍全绿。
- codestable：无影响（仅测试健壮性 + 本机依赖落地，未触碰 spec 既定口径）。

顺手发现（可选）：`SelfTest.Input.cs` — 不在本次范围：`TestClipboardSnapshotRoundTrip`（真实剪贴板 round-trip）是**既有 flake**，未改动 HEAD 上 8 跑 4 败、本改动后 1/8，与本次改动无关；干净进程 100 次紧凑循环 0 失败、GDI 仅 8 个句柄，均排除代码缺陷与资源耗尽。失败形态恒为「别名折叠」`[UnicodeText, System.Drawing.Bitmap, NaniKore.SelfTest]`，**待判定是 WinForms 同进程枚举假象还是产品缺陷**（若系统剪贴板真丢标准 `CF_BITMAP`，还原后别的程序粘不出图）——已单独立项 `codestable/issues/021-o-clipboard-roundtrip-flake.md`，含证据、未决问题与下一步（Win32 `EnumClipboardFormats` 地面真相探针，须写在工作目录内）。
