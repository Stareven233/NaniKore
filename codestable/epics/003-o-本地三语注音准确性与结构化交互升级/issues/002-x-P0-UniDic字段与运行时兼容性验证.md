---
kind: issue
title: "P0：UniDic 字段与运行时兼容性验证"
type: feature
status: closed
created: 2026-11-14
---

# P0：UniDic 字段与运行时兼容性验证

> **读者：** 负责判断本地 UniDic 2.1.2 能否替换当前日语 IPAdic 的开发者。
> **归属：** 本文件所在路径即归属 `Epic 003`。

## 目标与关闭结论

使用 `resources/unidic-lite@unidic-mecab-2.1.2_bin/` 的原版二进制做隔离验证，明确 LibNMeCab 对 UniDic 的打开结果、原始字段、分词与送假名差异、Debug/NativeAOT 加载，以及资源、许可和运行成本。验证不覆盖生产 IPAdic，不改 ModelScope 清单；生产切换和日语异读 UI 不在本 Issue 范围。

**结论：P0 验证完成，可以关闭。** LibNMeCab 0.10.2 的通用入口可加载 UniDic 2.1.2；当前探针在 Debug 与非 invariant NativeAOT 上得到一致结果。允许后续进入生产适配设计，**尚不满足直接替换生产词典的条件**：短文本阈值漂移、部分词元过滤后的底字歧义、宿主 globalization 与资源分发契约均有明确前置要求。

此前把这些生产迁移前置项一并作为本 Issue 不关闭的理由，混淆了隔离验证和生产切换范围。本次已补齐验证证据，并将前置项回写 Epic 的「日语生产切换前提」；不在关闭时降低生产验收标准，不创建额外兼容/回退链。

## 可交付探针与复跑

初版位于被忽略的 `pierce/p0-unidic/`。关闭时已将源码移动至随仓库保存的 `codestable/tools/unidic-probe/`，解决只有本机临时源码才能复跑的问题；旧目录仅留构建产物。本工具不属于桌面发布包。

工程只依赖 `LibNMeCab 0.10.2`，直接链接生产 `AnnotationContracts.cs`、`RubyTextParser.cs` 和判据表；真实执行过滤、去重及 Ruby 解析。字段到注音仍由隔离模拟承担，不引用生产注音器、下载器、哈希清单或资源管理器，不写入 `deps/`。

在仓库根目录执行：

```powershell
. ./scripts/toolchain.ps1
$probeDotnet = Get-NaniKoreDotnetExecutable
& $probeDotnet run --project codestable/tools/unidic-probe/P0UniDic.csproj -- --dict resources/unidic-lite@unidic-mecab-2.1.2_bin --ipa-dict deps/phonetics/ja --json > artifacts/p0-unidic-policy-debug.json
& $probeDotnet publish codestable/tools/unidic-probe/P0UniDic.csproj -c Release -r win-x64 -p:PublishAot=true --self-contained true -o artifacts/unidic-probe-aot
./artifacts/unidic-probe-aot/P0UniDic.exe --dict resources/unidic-lite@unidic-mecab-2.1.2_bin --ipa-dict deps/phonetics/ja --json > artifacts/p0-unidic-policy-aot.json
```

当前 Debug、AOT 均构建/运行成功，退出码 0；发布未报告 warning/error。两者逐项比较 `Samples`、`GoldChecks`、`ContractChecks`、`PolicyChecks` 均完全一致，包括 8 个固定文本的 **44 个原始节点**。

| 检查 | Debug | NativeAOT | 证明什么 |
| --- | --- | --- | --- |
| 送假名金样本 | 4/4 | 4/4 | 书面读音字段适配可维持这四个预期结果 |
| 字段/边界检查 | 7/7 | 7/7 | 字段语义、CSV、缺值及旧展示链路歧义可复现 |
| 双词典策略检查 | 9/9 | 9/9 | 过滤、去重、阈值规则可复用，同时记录分词差异 |

`known-gap/partial-token-ruby-base` 和 `segmentation-threshold-drift` 是**已知差异的复现断言**，通过不表示这些行为可发布。检查失败返回 1，不会只有日志失败却仍以 0 退出。未提供 IPAdic 对照参数时策略集合为空，不能宣称完整检查通过。用法详见 [探针 README](../../../tools/unidic-probe/README.md)。

## 字段与送假名结果

UniDic 原始 `Feature` 是含引号转义的 CSV，不是 `node-format-unidic` 的展示列；`GetFeatureAt(0)` 只返回首列 `pos1`。按原包 `dicrc` 映射：

| 字段 | 索引 | 已验证语义 |
| --- | --- | --- |
| `lemma` | 7 | 词元基本形，如 `食べる` |
| `pron` | 9 | 当前词形发音，如 `タベ`；可有音便长音 |
| `pronBase` | 11 | 基本形发音，如 `タベル` |
| `kana` | 17 | 当前词形的书面假名，如 `タベ` |
| `aType` / `aConType` | 23 / 24 | 重音型与连接类型；`食べ` 为 `2 / C1`，`摂取` 的带引号多值为 `1,0 / C1` |

真实已知词元可读取 26 列。未知词可能缺列，缺列和 `*` 均视为缺值，不制造读音；`これはiPhoneです` 保留纯文本。重音型仅验证读取，不实现重音计算、显示或语境消歧。

振假名优先 `kana`，缺失才取 `pron`，统一转平假名，再应用前后送假名切分、底字合法性检查及生产过滤/去重规则：

| 原文 | 预期与实测 |
| --- | --- |
| `食べた` | `食[た]べた` |
| `買い出しに行く` | `買い出[かいだ]しに行[い]く` |
| `行きます` | `行[い]きます` |
| `躊躇した` | `躊躇[ちゅうちょ]した` |

`躊躇` 的 `kana=チュウチョ`、`pron=チューチョ` 证明书面字段不能误取为发音字段。此结果证明 UniDic 字段可正确适配，**未证明相对已正确读取 IPAdic「読み」的生产实现有整体准确率提升**；不得沿用前轮对质量收益的过强表述。

`MeCabIpaDicTagger` 也能打开 UniDic，但其专用属性错位：`食べ` 的 `Reading=食べる`、`Pronounciation=食べ`，对应的实际字段是 lemma/orth。该入口仅记为 `pass-opened-meaning-unverified`，不得用于生产字段映射。

## 分词与策略差异

UniDic 的 `食べた` 分为 `食べ` + `た`，`買い出しに行く` 分为 `買い出し` + `に` + `行く`。候选词数按含汉字词面统计，先计数再执行 `AnnotationProfile.Load`，去重按完整词面与剥离送假名后的读音登记。

| 样例 / 策略 | IPAdic | UniDic | 判断 |
| --- | --- | --- | --- |
| `食べた` 重复 9 次 / 10 次 | 9 词全注但只标首现；10 词过滤为纯文本 | 相同 | 阈值与去重规则可复用 |
| `躊躇した。躊躇した` | `躊躇[ちゅうちょ]した。躊躇した` | 相同 | 同词同音去重可复用 |
| 总开关关闭 | `躊躇した` 不注音 | 相同 | 短文本豁免不绕过总开关 |
| 现有药物长句 | 12 词；`睡眠薬[すいみんやく]` | 13 词；`睡眠[すいみん]薬[やく]` | 同走长文本策略，词元与注音边界不同 |
| 阈值漂移句 | 9 词，全注，含 `食[た]べた` | 10 词，按难词过滤，不标 `食べた` | 已复现用户可见变化 |

药物长句：`薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬。`

阈值漂移句：`先生は日本語で問題を説明している。出来る。躊躇した。食べた。食べた。食べた`

**展示歧义已用生产解析器复现：** 强制长文本且 `rareOnly=false` 时，IPAdic 整词 `日本語` 输出 `日本語[にほんご]`；UniDic 拆成 `日本` + `語`，前者被停用表过滤，只剩 `語` 的注音，序列化为 `日本語[ご]`。真实 `RubyTextParser.Parse` 将其回溯为 `Text=日本語, Ruby=ご`，区间被扩大。后续必须让明确区间保持到渲染端，不能以插入空格、隐藏分隔符或改变原文来掩盖问题。本轮未运行真实 UI。

## 运行时与成本

当前探针 NativeAOT 使用 `InvariantGlobalization=false`，加载、分词、字段、策略和释放后重载均通过，exe 为 **3,063,296 bytes**。初版未链接策略的 exe 约 2.22 MB，不再作为当前探针体积。

| 当前单次观测 | Debug | NativeAOT |
| --- | --- | --- |
| 初次打开 | 80.92 ms | 17.37 ms |
| 再次 Parse | 0.0652 ms | 0.0166 ms |
| 释放后重载并 Parse | 2.65 ms | 2.38 ms |
| 工作集：打开前 / Parse 后 / 重载后 | 21,401,600 / 32,710,656 / 29,794,304 bytes | 10,391,552 / 16,244,736 / 13,324,288 bytes |

这些是本机单次独立进程观测，不是冷磁盘基准、产品性能阈值或完整词典内存占用承诺。

历史 negative 结果：同资源在 invariant globalization 探针中报 `not supported encoding dictionary. utf8 ... sys.dic`。历史生产宿主 NativeAOT 的 `--self-test` 在既有 IPAdic 下退出 0，只证明原宿主契约；没有将 UniDic 接入生产。本轮仅补跑小型探针 AOT，不重跑桌面 AOT、全量自测或压测。

## 资源、许可与指纹

来源：<https://unidic.ninjal.ac.jp/unidic_archive/cwj/2.1.2/unidic-mecab-2.1.2_bin.zip>。本地 README 声明是原版未修改的 2.1.2 二进制；本轮记录本地指纹，未重新联网验证压缩包真实性。完整解压目录约 260 MB，既有压缩包记录约 46.3 MB。

探针要求五个运行文件（含 `dicrc`），不是对引擎绝对最小集合的裁剪证明：

| 文件 | Bytes | SHA-256 |
| --- | ---: | --- |
| `sys.dic` | 187680870 | `122C4C91F026BF4B65BBE2DFD8B9F8EEB6AB56D8EB2A7287A68BCA75708BA513` |
| `unk.dic` | 5475 | `E3B92803FEEB6C2712C796B905317EC192A59729D2C1E92B2728D454169541C4` |
| `matrix.bin` | 71544726 | `88FB99EFE1075D9B8B40E01BB2751E4095F1612618F2490AF776EB5ED39190A9` |
| `char.bin` | 262496 | `DD31396563D8924645B80FD3C9AA7B13CA089D7748F25553A1D6BC3F9B511AE8` |
| `dicrc` | 1444 | `FCD17F35752DE1417859A15E2E1F043D666DA03F36DC632E8C0ED46887883A59` |

原包 `COPYING` 明确允许 GPL、LGPL、BSD 三选一；选择 BSD 时保留 `Copyright (c) 2011-2013, The UniDic Consortium`、完整 BSD 条款与免责声明，不得借作者名义背书。后续完整包和按需下载都必须随附声明；建议随资源保留原 `COPYING`、`BSD`，并在产品 `NOTICE` 保留归属和条款。此为切换契约，本轮不修改生产许可或资源包。

## 毕业与后续边界

已回写 `../spec.md` 的「日语 P0 结论与生产切换前提」：字段映射、运行时、策略差异、性能口径、资源与许可要求，并勾选本 Issue。Project Spec 不把候选写成当前生产真相。

生产切换仍需在 Epic 下单独推进：

1. 采用通用 Tagger 与 UniDic 字段映射，不保留 IPAdic 生产兼容或自动回退链。
2. 明确候选词计数与 10 词阈值的产品口径；固定上述差异句后再验收，不能把分词引起的注音变化称为等价。
3. 保留结构化底字区间到渲染端，覆盖部分词元被过滤后的相邻汉字，回归复制、选区与收藏。
4. 在真实宿主中落地非 invariant NativeAOT，并验证离线加载、懒加载、闲置释放与重载；独立探针不能替代宿主集成证据。
5. 更新完整包/下载清单、哈希、`dicrc` 和 UniDic 许可随行，再验证离线与干净目录部署；ModelScope 上传另需明确授权。

本 Issue 不实现以上生产工作，不自动创建新的切换 Issue，不改生产词典、下载清单、哈希、ModelScope 或 UI。按项目约定不提交 Git，仅给出建议 Commit Message。
