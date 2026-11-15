---
kind: issue
type: feature
title: "Misaki 英语注音与整词候选接入"
status: done
created: 2026-11-15
---

# 011 Misaki 英语注音与整词候选接入

> **结论（2026-11-15）：英语 provider 已切到原生 Misaki 语言包，G2（可读美音 IPA）与 G5（整词候选与 hover）按本轮口径验收通过。**
> 直接采用 Misaki 本句选音与已收录候选；read 现在时误选、lead 金属义缺项、未收录缩写的逐字母拼名如实记为已知限制。
> 008 构建链的英语包已按新 Kokoro→IPA 口径产出（audit 0 拒绝），本次未改动包字节、**不需要再生包**；
> 最终分发字节、干净系统 AOT 与 ModelScope 上传仍归 014。

输入为 [穿刺报告](../../../notes/misaki-annotation-spike.md)、固定 Misaki 源码/资源与现有区间契约。

## 本轮交付与边界

采用 Misaki 的语境默认读音，不再以全面修好 read/lead 时态、词义选择为关闭条件。候选只承诺保留引擎选中项和词典已收录读音，不承诺穷尽所有正确读音；上游误选/缺项作为已知限制记录，后续按反馈维护。本轮不增加第二套 CMUdict 来源、额外消歧模型或旧 provider 回退。

## IPA 适配（G2）

- [x] 将 Kokoro 编码显式转换成可读美音 IPA；允许 `ɾ/ʔ` 等发音变体，不猜回 /t/ 或 /d/，water/better/city 均有有效注音。音节与重音排版不得因复用旧 CMU 格式器而丢失原符号或强行改变元音。
      `scripts/phonetics/native/KokoroIpa.cs` 逐字符映射后交 `IpaTranscription.Normalize`；`water[ˈwɔːɾ.ər]`、`better[ˈbeɾ.ər]`、`city[ˈsɪɾ.iː]` 已进金样本。
- [x] 枚举词典及引擎生成读音的符号，验证转换不静默丢字符；无效/未知编码跳过对应注音并记录原因。旧严格探针的 34,625 条拒绝不是新方案验收结果，更新适配后重跑。
      新口径复跑：366,846 条读音全部转换成功、**0 拒绝**、`unknownSymbols` 为空（命令见「实测」）。
- [x] 固定转换、对齐和候选金样本；read 现在/过去时、record 动/名词、lead pipe/lead role 均保留对照，区分适配回归与接受的上游误选/缺项。Rating 不作为语境正确率。
      金样本在 `SelfTest.Phonetics.English.cs`（`TestEnglishNativeAnnotation` / `TestEnglishNativeWholeWordCandidates`），误选/缺项写成显式断言而不是「通过」。

## Provider 与整词候选（G5）

- [x] 数字/货币不注，缩写仅用明确收录读法；Dr./NASA 的成功不代表任意缩写都能自动展开。数字与 `$` 不进底字区间；`Dr.[ˈdɑːk.tər]`、`NASA[ˈnæs.ə]` 有读音；未收录全大写串只拿到引擎逐字母拼名（见已知限制）。
- [x] 关闭 TTS markdown 指令与原文改写；覆盖前导空白、tab/换行、emoji/半代理项、重复词、所有格、连字符、缩写和混排的 UTF-16 对齐。
      `  [record](/zzz/) record\nread`、`😀 read read.\tNASA\r\nread`、`Bob's device` 均按原文字节位置注音，`ToPlainText(Compose(...))` 恒等。
- [x] 本句读音排首位，其他候选转换后按明确规则去重；词干候选不得冒充当前屈折形式的读音。
      引擎候选首项即本句读音；宿主 `EnglishReadingCandidates.Collapse` 再按「精确重复 + 仅重音不同」折叠，候选只来自同一词面的词典条目。
- [x] hover 展示本句选中项和其他已收录读音；read 的 /red/、/riːd/ 均可查看。lead 缺 /led/ 如实记录，不凭空生成候选。仅去除可确认由句重音变化造成的重复项，保留 record 这类区分词义的重音差别。
- [x] 最小扩展整词候选契约与 hover 消费，不套入中文逐字列；颜色、默认项和复制/选区/收藏语义有真实 UI 验收。
- [x] 保留过滤、10 词阈值与去重政策，覆盖 Debug/AOT、离线及闲置重建；记录真实冷/热表现。

## 交付内容

- `Phonetics/EnglishReadingCandidates.cs`（新）：整词候选折叠（首项=本句读音、剔除精确重复与仅差 `ˈ/ˌ` 的句重音变体）。
- `AnnotationContracts.cs`：新增 `WholeWordReadings(Start, Length, Candidates)` 与 `AnnotationSpan.WholeWord` 可选字段——整词候选的最小契约扩展，与中文逐字 `Syllables` 互斥，中文链路零改动。
- `Native/NativeTextAnnotator.cs`：英语词元在区间校验后挂整词候选；字面方括号区段沿用旧英语口径不注音；停用/难词过滤、10 词阈值与同词同音去重仍走 `AnnotationContext`。
- `TextAnnotatorRegistry.Create*`：`en` 槽位改取 `NativePhoneticPackHost` 的原生包（与日语同一套宿主、闲置释放与重启更新），注册表不持有原生槽位。
- `App/Controls/RubyInlineControl.cs`：整词候选按「一条覆盖整词的读音组」渲染——不进逐字列，注音行在多读时用与中文多音字相同的提示色，hover 面板复用同一模板逐条列出读音并对首项高亮。
- `App/Controls/RubyDocumentBuilder.cs`：把 `AnnotationSpan.WholeWord.Candidates` 传到控件。
- 旧 `EnglishIpaAnnotator`（CMUdict 逐词标注器）移出生产装配，文件留档 `.temp/011-英语/`；旧 `deps/phonetics/en/ipa_dict.db` 数据与设置页旧卡片按契约保留到 014。

## 实测

命令：`./scripts/dev.ps1 build`（0 警告 0 错误）、`./scripts/dev.ps1 test`、`./scripts/dev.ps1 build -Release` + `artifacts/win-x64/NaniKore.exe --self-test`、
`./scripts/phonetics/Build-MisakiPacks.ps1 -Stage Verify`、以及新口径符号审计（见下表最后一行）。

| 项 | 结果 |
| --- | --- |
| 自测 | Debug 1957 项、NativeAOT 宿主 1957 项，全部通过（010 时 1922 项，本次净增 35 条英语 IPA/候选/UI 断言） |
| 语言包验收 | `Verify` 413 项断言 0 失败；ja 21,767,680 bytes、en 8,868,864 bytes（字节与哈希未变，仍是 008 产物） |
| G2 符号审计 | `Tools.dll audit-ipa resources/en_lexicon_us.tsv.gz` → `converted=366,846, rejected=0, unknownSymbols={}`（旧严格口径为 33.2 万通过 / 3.46 万拒绝） |
| 冷/热 | `en cold=647–1356ms warm20=2.6–35ms rebuild=627–1093ms`（冷态随机器负载波动）；日语同轮 `ja cold=1338–2261ms warm20=0.4–0.6ms rebuild≈1.3–2.3s` |
| 内存 | 托管堆 42–44 MB、工作集 434–522 MB（AOT 宿主自测轮 434–447 MB） |
| 美音 IPA | `The[ðə] water[ˈwɔːɾ.ər] is[ɪz] better[ˈbeɾ.ər] in[ɪn] the city[ˈsɪɾ.iː].` |
| 本句选音 | `record[rə.ˈkɔːrd] a new record[ˈrek.ərd]`；`read[ˈred] … read[ˈriːd]`（同句两处不同读音都注） |
| 候选 | `record` 两处各 2 条候选、首项随本句重排；`They/will` 这类只差句重音的伪多读不挂候选；20,000 词抽样里 19,821 条单读、175 条双读、4 条三读（最大 3） |
| 原文对齐 | `😀 read[ˈriːd] read[ˈred].\tNASA[ˈnæs.ə]\r\nread[ˈriːd]` 与原文字节位置一致、区间不重叠、纯文本还原恒等 |
| UI | 真实 Avalonia 控件断言：整词不生成逐字列、多读提示色、hover 列出本句读音与备选且仅首项高亮、复制/选区语义不变；渲染冒烟 `artifacts/p2-english-whole-word.png` |
| 缺包/离线 | 指向空目录的宿主状态为「缺失」，标注器不可用、区间为空、`CountWords` 为 0、不抛异常，不回退旧 provider |

## 已知限制（如实记录，不阻塞关闭，也不写成已解决）

| 现象 | 实测 | 定性 |
| --- | --- | --- |
| `I read every day.` | `read[ˈred]` | 上游词性标注把现在时当 VBP 取 /red/；候选里仍能查到 /riːd/ |
| `She has the lead role.` / `lead pipe` | 均 `ˈliːd` | 金属义 /led/ 不在该词候选里，如实缺项，不凭空生成 |
| `U.S.` / `ZZQX` | `ˌjuː.ˈes` / `ˌziː.ˌziː.ˌkjuː.ˈeks` | 未收录缩写只拿到引擎逐字母拼名，不作为语境能力 |
| 功能词多读 | `the` 候选 `ðə`/`ðiː` | 强弱读是真实差异，会保留；短文本全注时功能词也可能显示提示色 |

## 未覆盖

- 真实网络下载、干净系统 AOT 部署与 ModelScope 上传仍归 014；本次未上传，包字节与 008 一致（未再生）。
- 设置页旧 en 词典下载卡片仍与原生包卡片并存（契约内过渡态），随旧 provider 在 014 下线。
- 未在真实桌面人工逐句过一遍长文混排；UI 断言集中在自测的真实控件实例与渲染冒烟上。
- 不承诺英语注音的语境正确率；`Rating` 类词源等级不能当准确率指标。
