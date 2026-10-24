---
kind: issue
title: "[快改] 短原文（不足 10 词）全部注音，不再按难词过滤"
type: ff
status: closed
created: 2026-10-25
---

<!-- 快改痕迹：轻。读者只要 30 秒扫完。禁止迷你 Design。 -->

# [快改] 短原文（不足 10 词）全部注音，不再按难词过滤

> **读者：** 以后搜到这条时——「改了啥、怎么信、动没动制度记忆」。  
> **自检（四答即可，可合成短段，勿空标题凑节）：** 做了什么 · 改了哪些文件 · 怎么验证 · 对 `codestable/` 有无影响。

---

主人反馈：点「注音」按钮（或翻译链路落地）时注音会过滤难词，短句常被滤到什么都不注。现在按词数分流——本篇「可注音候选词」不足 10 个时视为短语，停用词表与难词过滤全部让位，每个候选词都注音；词数达到阈值后仍按设置里的「仅注难词」过滤。阈值是 `AnnotationProfile.ShortTextWordThreshold = 10` 一个常量，无新配置项。

- 改动：`src/NaniKore/Phonetics/AnnotationContracts.cs` — `ITextAnnotator` 新增 `CountWords`（各标注器按自己既有分词口径数候选词，不查读音）；`AnnotationProfile` 新增 `ShortTextWordThreshold` 与 `AnnotateAll`，`Load` 增补 `wordCount` 参数（默认 `int.MaxValue` 即长文本口径，金样本自测直接装载时语义不变），`IsSkipped` 在短文本豁免时两个过滤器一并让位。
- 改动：`src/NaniKore/Phonetics/JapaneseFuriganaAnnotator.cs` / `ChinesePinyinAnnotator.cs` / `EnglishIpaAnnotator.cs` — 实现 `CountWords`：ja/zh 数含汉字词面（一次 MeCab 分词），en 数长度≥2 的拉丁词元（纯文本扫描，不依赖词典是否就绪）。
- 改动：`src/NaniKore/Phonetics/TextAnnotatorRegistry.cs` 与 `src/NaniKore/App/ResultPanelWindow.xaml.cs` — 两条生产链路（registry 统一入口 / 结果面板单次异步注音）都按词数建档，口径不漂移；面板侧计数与标注同一次 `Task.Run`，UI 线程只负责落地。manual 状态文案「没有命中难词」改为「没有可标注的单词」（短文本下前者语义不成立）。
- 改动：`src/NaniKore/App/Settings/GeneralPage.xaml` — 「仅注难词」说明补一句短文本豁免；`README.md` 功能列表同步。
- 改动：`src/NaniKore/Phonetics/PhoneticsProbe.cs` — 样本补短句/长句各一，逐条打印「词数 + 是否全注 + 生产口径结果」，并保留「仅难词=关」的原始候选行供黄金样例对照。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Phonetics.cs` — 新增 `TestAnnotationShortTextWordCount`（三语词数金样本 + 英语 9 词全注 / 10 词起回到仅难词的端到端边界）；`TestAnnotationProfileAndDedupe` 补 5 条短文本豁免断言；`TestRegistryLanguageRouting` / `TestRegistryResetAvailability` 的英语断言从 1 词改为 3 词（三词短语现在全注）。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Ui.ResultPanel.cs` + `SelfTest.Ui.cs` — 新增 `TestResultPanelShortTextAnnotateAll`：短语「日本語を勉強する」全注（含被常用汉字表过滤的 日本語），10 词长句仍过滤（先生/日本語/問題 不带读音），纯文本事实源不变；ja 词典未就绪时 SKIP。
- 验证：`scripts/dev.ps1 build -ForceBump` 0 警告 0 错误（版本 0.5.5 → 0.5.5+1）；`--self-test` 连续三跑全绿（626 PASS / 0 FAIL / 1 SKIP，新增 14 条断言全过，仅 1 条因措辞调整替换）；`--phonetics-probe` 实测：短语「日本語を勉強する」词数=2 全注，主人原句「薬の過剰摂取で…睡眠薬。」词数=12 维持仅难词，三语行为一致。
- codestable：无影响（project spec 未记载本地注音的过滤口径，无既定真相失效；注音引擎、源语言下拉、双模切换等既有真相原样不变）。

顺手发现：`codestable/spec/index.md`「尚未实现」仍写着「本地词典分词假名快关（MeCab 等）记录在 Epic 001 悬挂项」，但 issue 008 早已交付本地三语注音引擎——这条既有漂移不在本次范围，留给主人决定何时收拾。
