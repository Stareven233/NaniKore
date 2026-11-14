# 纯数据中文词组选音方案 — 证据报告

本工具服务 Epic 003（中文默认选音），本轮**只做证据评估，不改生产代码、词典或源码**。
对应 Issue 001「P0 三语基线」与 Issue 004「P1 中文结构化」中尚未解决的「还 / 罅缝」默认选音问题。

## 结论先行

- **pypinyin（纯词组词典 + 单字频率默认，无 ML）** 配合 **OpenCC t2s 繁转简归一查音、原文展示**，
  在固定 13 个核心中文目标上达到 **12/13**；关键是它**读对「还/罅缝」且不破坏「周长大于」**。
- 它唯一的核心失败 `俳佪` 是稀有变体字 `佪` 默认读 `huí`（应为 `huái`）——但 `huái` **在其候选集合里**。
- 现有生产 MeCab/CC-CEDICT 恰好互补：`俳佪`、繁体等读对，但 `还(这里)`、`罅缝` 读错（见 Issue 001/004）。
- 因此**可靠方案 = 合并词组词典**：pypinyin 词组/频率数据 ∪ 现有 CC-CEDICT 字词读音（含稀有变体、繁体），
  以 t2s 归一查音、始终用原文 code point 的 UTF-16 区间展示。该合并可覆盖 13/13；完整落地需重建中文 MeCab 词典，
  属下一轮范围（需 `mecab-dict-index` 与 CC-CEDICT 源料，当前 `deps/phonetics/_source` 不在本地）。
- pypinyin `heteronym=True` 天然给出每字候选集合，直接满足 Epic 的「完整候选传递 / P2 多音查看」数据前提。

## 复跑

不改全局环境，隔离安装纯 Python 依赖后运行（控制台只输出 ASCII 摘要，中文写 UTF-8 报告）：

```powershell
# 复用一个已存在的隔离依赖目录（含 pypinyin/opencc 即可；此目录名沿用历史，与本方案无关）
$pkgs = (Resolve-Path 'artifacts/chinese-g2pw/python-packages').Path
$env:PYTHONPATH = $pkgs
& E:/python/Python314/python.exe codestable/tools/chinese-phrase-eval/evaluate.py `
  --packages $pkgs --output artifacts/chinese-phrase-eval/report.json
# 期望输出：mode_raw core 11/13；mode_t2s core 12/13；退出码 1（核心未全过，表示存在明确偏差，非全绿验收）
```

- 固定期望 `CORE_CASES` 直接镜像 `codestable/tools/phonetics-baseline/Samples.cs` 的 `zh-*` 目标（13 个 span），
  不由运行结果反向生成；`EXTRA_CASES` 为额外语义反例，不计核心分。
- 脚本记录 `pypinyin` 版本与 `phrases_dict.json`/`pinyin_dict.json` 的 SHA-256，便于核对。

本次运行（2026-11-14）：
- 引擎：`pypinyin 0.55.0` + `opencc-python-reimplemented`（t2s）。
- `phrases_dict.json` SHA-256 `a45ff140a6b631ca9c82127b280a2f414e0aba6bb2824a0e9d1e77fff359c665`。
- `pinyin_dict.json` SHA-256 `5f294c01e6c6c0a1c8e329c79335a3f8e0b27d06bf1de7a99244b765892d1e5b`。

## 核心 13 目标结果（t2s 模式）

| 目标 | 文本 | 期望 | pypinyin+t2s | 结果 |
| --- | --- | --- | --- | --- |
| zh-paihuai | 俳佪 | pái huái | pái **huí** | ❌（`huái` 在候选中） |
| zh-juyu | 龃龉 | jǔ yǔ | jǔ yǔ | ✅ |
| zh-bank | 不行就去银行 | bù xíng / yín háng | 一致 | ✅ |
| zh-hai | 他还在这里。 | hái | hái | ✅ |
| zh-huan | 把书还给我。 | huán | huán | ✅ |
| zh-grow | 孩子长大了。 | zhǎng dà | zhǎng dà | ✅ |
| zh-long | 这里的周长大于十米。 | cháng dà | cháng dà | ✅（**不破坏周长大于**） |
| zh-traditional | 銀行 | yín háng | yín háng | ✅（t2s→银行） |
| zh-simplified | 银行 | yín háng | yín háng | ✅ |
| zh-xiafeng | 罅缝 | xià fèng | xià fèng | ✅ |
| zh-repeat-mixed | 龃龉\\r\\nHello 123! … | jǔ yǔ | jǔ yǔ | ✅ |
| zh-extension | 𠮷𠀀龃龉 | jǔ yǔ（UTF-16 4,2） | jǔ yǔ | ✅ |

`mode_raw`（不做 t2s）为 11/13，仅多失一项繁体 `銀行`（`yín xíng`），说明 **t2s 归一查音**能补繁体。

### 额外语义反例（不计核心分）

| 文本 | 期望 | 实际 | 说明 |
| --- | --- | --- | --- |
| 请把钱还我。 | huán | **hái** | ❌ 硬边界：动词「还」+代词宾语，词组表未覆盖，属已知限制 |
| 他终于还钱了。 | huán | huán | ✅ |
| 她正在缝补衣服。 | féng | féng | ✅ |
| 墙上的缝隙很大。 | fèng | fèng | ✅ |

## 为何是「合并」而非单选一个引擎

单字候选集合（`heteronym=True`）显示每个核心目标的正确读音都在候选内：

| 字 | 默认 | 候选 |
| --- | --- | --- |
| 佪 | huí | huí, **huái** |
| 还 | hái | **hái, huán**, fú |
| 缝 | fèng | **fèng, féng** |
| 行 | xíng | **xíng, háng**, héng, xìng, hàng |
| 长 | zhǎng | **zhǎng, cháng** |

- `还/缝/长/行` 的语境默认已由 pypinyin 词组表覆盖，故固定句读对。
- `佪` 是词组表未收录的稀有变体，单字默认取 huí；但 `huái` 在候选内，需要「来源偏好」二次选择——
  现有 CC-CEDICT 把 `佪` 读 `huái`（生产 selftest 已通过）。**这正是合并的价值**：pypinyin 补常见多音语境，
  CC-CEDICT 补稀有字/繁体/整词，两者合并不互斥。

合并后生产设计（下一轮，非本轮）：
1. 以 pypinyin 词组表 + 单字频率默认，和 CC-CEDICT 字词读音（含稀有变体/繁体）生成统一中文读音表；
   同面冲突时词组优先、稀有变体以 CC-CEDICT/规范来源为准。
2. 查音前对文本做 t2s（仅用于查音），**展示/复制/收藏/选区仍用原文 code point 与 UTF-16 区间**，不改写原文。
3. `AnnotationSpan/AnnotationSyllable` 携带候选集合，供 P2 hover/多音查看，不再从 `底字[读音]` 反推结构。

落地需重建中文 `deps/phonetics/zh/sys.dic`（`mecab-dict-index` + CC-CEDICT/Unihan 源料，当前源料本地缺失），
blast radius 较大，本轮不做。

## 边界与未覆盖

- 仅评估「默认读音」；未做姓名、自定义词典、长文本、语种混读、变调。`请把钱还我` 类动词短语句仍可能选错默认。
- `𠮷`/`𠀀` 等扩展字自身读音未核验（仅验证不破坏后续 `龃龉` 区间）。整句读音未逐字核对。
- t2s 假设简繁逐 code point 等长；若某短语转换改变字数会退回原文并在报告记录（当前样例未触发）。
- 性能/内存未测；pypinyin 数据合计约 3.3 MB，比现中文 sys.dic 约 9.8 MB 更小，但未做宿主/AOT/冷启动基准。
- 本报告不是宿主验收、不是生产切换授权。

## 相关

- 固定期望来源：[Samples.cs](../phonetics-baseline/Samples.cs)；读音分级依据：[chinese-candidates/expectations.md](../chinese-candidates/expectations.md)
- 现状失败与候选对照：[Issue 001](../../epics/003-o-本地三语注音准确性与结构化交互升级/issues/001-o-P0-三语基线与验证工具.md)、[Issue 004](../../epics/003-o-本地三语注音准确性与结构化交互升级/issues/004-o-P1-中文结构化区间与逐字对齐.md)
