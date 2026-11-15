---
kind: epic
title: "本地三语注音准确性与结构化交互升级"
status: open
created: 2026-11-14
---

# 本地三语注音准确性与结构化交互升级

> **读者：** 推进注音升级的开发者。本文是该 Epic 唯一活规格：只记现在为真的事实、目标与可复跑证据。
> **决策与计划：** [talks/004](../talks/004-本地三语注音优化与后续能力规划.md)；**穿刺证据：** [notes/misaki-spike](../notes/misaki-spike.md)。
> **2026-11-15 方向定稿：** 中文保留自研引擎（内嵌随包）；日/英 + 未来 5 语走 MisakiSharp，编成**原生 DLL 按语言裁小包、ModelScope 按需下载、懒加载**；只取管线中间产物、按原文偏移对齐；复用现有 span/candidates/hover 契约。

## 这条线要改变什么

把日语、英语的读音来源迁到 MisakiSharp（日语 unidic 3.1.0 振假名；英语词性/时态消歧 + 词性多候选），中文维持自研引擎；把注音词典分发从“按语言下载原始词典”改为“按语言下载裁剪后的 Misaki 原生 DLL 包”，主程序不膨胀、注音仍可选。守住本地离线、中间产物、原文偏移对齐、Debug/AOT、只管读音不管释义。

## 当前真相（截至 2026-11-15，代码尚未切换）

本地链路仍为：纯原文 → 单语言标注器 → `AnnotationSpan(Start, Length, Reading, Syllables)`（含 `Candidates`，首项默认）→ 按 UTF-16 区间构建 Ruby。中文走自研 `ChinesePinyinEngine`（**保留**）；日语仍 IPAdic（`JapaneseFuriganaAnnotator`）；英语仍 CMUdict 单词典（`EnglishIpaDictionary`，候选库 issue 007 已落）。分发仍 `PhoneticDependencies` 按语言下载 ja/en/zh 词典 + SHA-256。

穿刺已完成（`tools/misaki-spike`、`tools/misaki-native`、`tools/misaki-native-client`）：AOT 主程序可运行时 `LoadLibrary` 原生 DLL；MisakiNative.dll 只依赖 Windows 内置 UCRT，纯便携；内嵌资源 66MB 可按语言裁剪为日~18MB/英~8MB/5语~22MB；中文单字多读=0（无候选）；英语多音按词性存在数据里可直接取。详见 notes/misaki-spike。

## 三语目标

| 语言 | 读音来源 | 候选/hover | 分发 |
|---|---|---|---|
| 中 | 自研引擎（保留） | 自研提供 | 数据 ~1.5MB 内嵌随包 |
| 日 | Misaki `JapaneseTagger.Parse`（unidic 3.1.0） | 单读，需要时补 unidic 倒排 | MisakiJa.dll ~18MB 按需下载 |
| 英 | Misaki `Phonemize` 默认读音 | Misaki 词典词性多读 | MisakiEn.dll ~8–20MB 按需下载 |
| 5 语 | Misaki `EspeakG2P` | 无候选 | Misaki5Lang.dll ~22MB 按需下载 |

## 现在推什么（可执行路线，按序）

1. **重打包链** — Cecil 剥资源脚本 + 按语言出原生 DLL（ja/en/5语）+ 版本哈希 + NOTICE。
2. **主程序接入骨架** — P/Invoke 客户端 + 各语 Lazy 单例 + 后台/锁 + 设置页多语包下载 UI + AOT 烟测。
3. **日语接入** — `JapaneseTagger.Parse` 出振假名，保留 span 契约与原文偏移；送假名/长音/复合词/未登录回归。
4. **英语接入** — 默认读音 + 词性多候选；数字/缩写对齐回归。
5. **中文内嵌收口** — 自研数据由下载改为随包内嵌，核对与现网一致、候选/hover 复用。
6. **5 语接入** — EspeakG2P 单读音。
7. **分发简化与收尾** — 移除旧 ModelScope ja/en/zh 注音下载；离线/AOT 回归；更新 `spec/index.md` 与 `notes/phonetics-pipeline.md`；关闭旧 Epic/issue。

## Issues

**历史（旧计划，已闭合；结论以本规格与 talks/004 为准）：**

- [x] `issues/001-x-P0-三语基线与验证工具.md`
- [x] `issues/002-x-P0-UniDic字段与运行时兼容性验证.md` — unidic 结论并入 Misaki 日语路径。
- [x] `issues/003-x-P0-英语异读候选保留验证.md` — 英语候选形状验证（Misaki 词典已自带词性多读，旧 CMUdict 候选库作废）。
- [x] `issues/004-x-P1-中文结构化区间与逐字对齐.md` — span/逐字对齐（**复用**）。
- [x] `issues/005-x-P1-中文选音引擎与候选传递.md` — 自研中文引擎（**保留**）。
- [x] `issues/006-x-P2-中文多音提示色与hover候选菜单.md` — 多音 hover（**复用**）。
- [x] `issues/007-o-P4-英语候选正式数据落地.md` — 英语候选 SQLite；被 Misaki 词性多候选取代，作废。

**待推进（按路线建 issue，编号续 008 起）：**

- [ ] 008 重打包链：Cecil 剥资源 + 按语言原生 DLL + 哈希 + NOTICE。
- [ ] 009 主程序接入骨架：P/Invoke + Lazy 单例 + 后台/锁 + 设置页多语包下载 + AOT 烟测。
- [ ] 010 日语接入（tagger 振假名）。
- [ ] 011 英语接入（默认读音 + 词性多候选）。
- [ ] 012 中文内嵌收口。
- [ ] 013 5 语接入（espeak 单读音）。
- [ ] 014 分发简化与收尾（移除旧下载、spec/pipeline 更新、关闭）。

## 质量承诺

- **功能适宜性：** 金样本区分已验证/失败/未覆盖；不把“取到读音”说成语境消歧完成；候选按语言如实标注来源。
- **性能效率：** 记录原生 DLL 下载体积、冷启动（首加载+建表）、稳态、内存；内存按语言懒加载。
- **交互能力：** 不从 `底字[读音]` 反推结构；候选菜单标默认项、颜色非唯一信息。
- **可靠性与兼容性：** 只取中间产物、按原文偏移对齐；Debug/AOT/离线可复现；纯便携（仅系统 UCRT）。
- **可维护性：** 重打包脚本可重跑升版；数据来源、许可（UniDic BSD、misaki/Apache、phonemizer GPL、num2words LGPL 等）、哈希可追溯。

## 边界

暂不做：中文切 Misaki、音调核展示、手动纠音/个人偏好、口语连读变调；TTS/OCR API 仅备忘。

**合并回 project spec 的候选：** 最终采用的读音来源、原生 DLL 包形态/体积、许可、已验证运行边界。旧计划失败/保留原因留 Epic 与 notes。

## 相关材料

- [talks/004 决策与计划](../talks/004-本地三语注音优化与后续能力规划.md)
- [notes/misaki-spike](../notes/misaki-spike.md)
- [notes/phonetics-pipeline](../notes/phonetics-pipeline.md)
- `codestable/tools/misaki-spike/`、`misaki-native/`、`misaki-native-client/` — 穿刺源码
- `src/NaniKore/Phonetics/` — 现状实现
