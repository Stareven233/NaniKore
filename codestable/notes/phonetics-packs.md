# 日英原生注音语言包：构建链与实测

> 2026-11-15。对应 [Epic 003 / 008](../../epics/003-o-本地三语注音准确性与结构化交互升级/issues/008-o-原生语言包正式重打包与数据再生.md)。
> 本文只记录正式构建链的产物与实测。**日语 provider 已于 010 切换到原生包**（英语仍 CMUdict，011 切换；中文仍外部数据，012 内嵌），
> 宿主接入从 009 开始。
> 穿刺结论与旧口径失败证据见 [注音适配穿刺](misaki-annotation-spike.md)。
> 速查：为什么日语词表要再生（体积代价）见「日语词表为什么来自 UniDic 再生」；
> 裁剪与构建步骤见「语言包是怎么裁与怎么编的」；升版本要动哪几处见「将来怎么升级」。

## 入口与固定输入

```powershell
./scripts/phonetics/Build-MisakiPacks.ps1 -Stage All      # Prepare → Native → Verify
```

固定项全部写在 `scripts/phonetics/misaki-packs.json`，任一项不符立即失败：

| 项目 | 值 |
| --- | --- |
| MisakiSharp | 2.2.0，提交 `beb91a9f06ebe5e25e595b5a3c990ee616ad75e5`，Apache-2.0 |
| nupkg / 程序集 SHA-256 | `C590870B…15CB50` / `8D20DB97…FD09B` |
| 补丁 | `scripts/phonetics/patches/misakisharp-2.2.0.patch`，SHA-256 `46818E35…E11863` |
| UniDic | 3.1.0 压缩包 525,943,040 bytes，SHA-256 `638718C4…D59C43`；`sys.dic` `F019F958…B192C` |
| ABI / 架构 | 1 / win-x64 |

补丁只做必需修改：日语词条增加 `Kana` 并从词表第五列读取、`Parse` 返回书面假名；英语关闭 TTS markdown
读音指令与前导空白裁剪、空白词元并回前词、新增 `Candidates()`；五个静态缓存改为可重建并新增 `Release()`。
`git apply --check` 与逐文件哈希双重把关，上游锚点漂移即停。NuGet 原件与包缓存只读不改。

## 产物

| 包 | 文件 | 字节 | SHA-256 |
| --- | --- | ---: | --- |
| ja | `deps/phonetics/misaki/ja/NaniKore.Phonetics.Ja.dll` | 21,767,680（20.76 MiB） | 见同目录 `pack.json` |
| en | `deps/phonetics/misaki/en/NaniKore.Phonetics.En.dll` | 8,868,864（8.46 MiB） | 见同目录 `pack.json` |

字节数在重复构建中稳定；**SHA-256 以 `pack.json` 为准**——NativeAOT 产物不承诺二进制可复现，每轮重跑只覆写清单与包文件。

- 英语包不含 espeak 回退表（未收录词不猜读音），因此比穿刺里含回退的 22.64 MiB 小很多。
- 两包只嵌入白名单资源：ja = 再生 `ja_lexicon.tsv.gz` + `ja_char_unk.tsv.gz` + `ja_matrix_compact.bin.gz`；
  en = `en_lexicon_us.tsv.gz` + `en_tokenizer.tsv.gz` + `en_tagger.bin.gz`。`ja_words`、`zh_*`、`es/fr/hi/it/pt_*` 均未进包。
- 每包附 `pack.json`（字节、SHA-256、ABI、架构、导出、PE 普通导入表、资源哈希、上游与数据版本）与
  `licenses/`（MisakiSharp `LICENSE`/`NOTICE`、`DERIVED.md`，日语另附 UniDic `BSD`/`COPYING`/`dicrc`）。

## 日语书面假名再生

`scripts/phonetics/RegenerateKana.py` 从同版 `sys.dic` 再生，逐条核对身份：

- 674,791 词面 / 878,989 词条，身份失配 0；
- 190,973 条 `pron` 与 `kana` 不同（東京 `トーキョー→トウキョウ`、躊躇 `チューチョ→チュウチョ`、
  今日 `キョー→キョウ`、は `ワ→ハ`、へ `エ→ヘ`）；
- 4,474 条 `kana` 为 `*`（符号、颜文字等）写空串，ABI 返回 `reading: null`，**不回退发音字段**；
- 输出 gzip 10,143,927 bytes，SHA-256 `C738285D…12990139`（mtime=0，可重复生成）。

## 英语 IPA 适配（G2 口径）

`scripts/phonetics/native/KokoroIpa.cs` 做字符级编码转换，经构建期 `tools audit-ipa` 全量审计：
en-US 词典 **366,846 条读音全部可转换，0 拒绝**（旧严格转换器曾拒绝 34,625 条）。

- 闪音 `ɾ`、喉塞音 `ʔ` 作为合法美音变体原样保留，不猜回 `/t/`、`/d/`；
  `water→ˈwɔːɾ.ər`、`better→ˈbeɾ.ər`、`city→ˈsɪɾ.iː` 均能注出。
- 未收录字符一律返回 null（判为不可展示），不静默吞字符。
- 已知上游限制不变：`I read every day.` 选 `/red/`、`lead pipe` 缺 `/led/`；hover 只展示词典明确收录的读音。
- 所有格只给底字注音，读音取底字本身的词典条目（不把带 z 的整词发音挂到底字上）。

## ABI v1 与验收

```c
int32_t nk_phonetics_abi_version(void);
int32_t nk_phonetics_annotate(const uint8_t* utf8, int32_t byte_length, uint8_t** json, int32_t* json_length);
int32_t nk_phonetics_release(uint8_t** json, int32_t* json_length);
void    nk_phonetics_free(uint8_t* buffer);
```

状态码 `0` 成功 / `1` 参数非法 / `2` 非法 UTF-8 / `3` 解析失败；输出偏移一律原文 UTF-16；异常不穿越边界；
解析、释放、重建共用每语一把锁；`release` 只解除引用，不强制 GC、不卸载 DLL。
输出 JSON 用 `JavaScriptEncoder.Create(UnicodeRanges.All)`，CJK 与 IPA 原样输出。

`scripts/phonetics/verify` 对两个包跑 **413 项断言，0 失败**：哈希/字节/架构/ABI/四个导出、
空指针与非法长度、非法 UTF-8、空文本、每词元 UTF-16 区间与原文切片一致、无 Kokoro 残留编码、
英语候选首项=本句读音且去重、数字/货币 null、markdown 不注音、日语书面假名金样本、
释放后重建结果完全一致、8 路并发含一次释放。

| 包 | 冷加载（首调用） | 热态 20 次 | 释放后重建 |
| --- | ---: | ---: | ---: |
| ja | 约 1.4–1.5 s | 约 2.3 ms | 约 1.3–1.4 s |
| en | 约 0.65–0.68 s | 约 3.7–4.3 ms | 约 0.6–0.67 s |

冷加载在独立客户端进程内测量，不含 DLL 文件系统读取与真实宿主启动；秒级耗时必须放后台（009）。

PE 普通导入表只有系统 DLL 与 UCRT：`KERNEL32/ADVAPI32`（英语另有 `USER32`、`api-ms-win-core-synch`）、
`api-ms-win-crt-*`、`bcrypt`、`ole32`；不含 VCRUNTIME/MSVCP。这是静态证据，干净系统部署验收留给 014。

## 宿主接入骨架（009）

`src/NaniKore/Phonetics/Native/` 已落地 ABI v1 客户端、包身份清单、状态机、下载安装与闲置释放；日语 provider 在 010 已切到该链路，英语仍 CMUdict（011 切换）。

- 清单：`NativePhoneticManifest` 记录库字节、SHA-256、ABI、架构、四个导出与随包许可哈希；`deps/phonetics/misaki/<lang>/pack.json` 与之双向核对，任何一项不符即拒绝装载。
- 客户端：先校验文件与 PE（win-x64）再按绝对路径 `NativeLibrary.TryLoad`，绑定后比对 ABI 与导出；输出交回同 DLL 释放，模块永不卸载。
- 生命周期：每语一个惰性资源，闲置回收只调 `nk_phonetics_release`；新版本落 `.staging-<lang>`，已加载则等重启提升。
- 设置页新增「注音语言包（本地原生）」分组，状态覆盖缺失/需更新/下载中/失败/就绪/重启生效。
- 实测：Debug 与 NativeAOT 宿主自测均 1911 项通过；`NaniKore.exe` 23,062,528 → 23,261,184 B（+198,656）。详见 [issue 009](../epics/003-o-本地三语注音准确性与结构化交互升级/issues/009-o-原生ABI下载与资源生命周期接入.md)。

## 日语 provider 切换（010）

- `TextAnnotatorRegistry.CreateDefault()` 的 `ja` 槽位直接取 `NativePhoneticPackHost` 的原生包标注器，不再新建旧 IPAdic 标注器；旧 `JapaneseFuriganaAnnotator`（LibNMeCab）已留档到 `.temp/010-日语/`，包引用与旧 ja 词典卡片、NOTICE 3.1 随旧 provider 在 014 清理。
- 宿主侧切分 `src/NaniKore/Phonetics/JapaneseRuby.cs`：UniDic 给整词片假名，界面要只覆盖汉字的振假名——词首词尾送假名切回底字之外（`食べ`→`食[た]べ`、`凍り付く`→`凍り付[こおりつ]く`），内部送假名留在底字里（`買い出し`→`買い出[かいだ]し`），底字只允许汉字+假名且必须含汉字；片假名转平假名后输出。
- 金样本（Debug 与 NativeAOT 均通过）：`東京→とうきょう`、`躊躇→ちゅうちょ`、`今日→きょう`、`凍り付く→こおりつ`；送假名 `食べた→食[た]べた`、`お祝い→お祝[いわ]い`、`買い出し→買い出[かいだ]し`、`行きます→行[い]きます`；未知词/数字/拉丁/纯假名不注音；emoji（代理项对）、换行与全角空白按原文 UTF-16 定位且区间不重叠。
- 分词差异（记录、不追求与旧口径等价）：`日本語` → `日本 + 語`；`睡眠薬` → `睡眠 + 薬`；`行く` 等词面仍是整词，过滤与去重继续按整词词面裁决。
- `NativeTextAnnotator` 的包目录可注入，使「设置页状态」与「真实加载」永远指向同一处（注入目录缺失时即真降级为纯文本，不抛异常）。
- 实测：`./scripts/dev.ps1 test` 与 `--self-test` 均 1922 项通过（NativeAOT 宿主同样 1922）；日语冷加载 ~1.33 s、热态 20 次 0.4–0.6 ms。详见 [issue 010](../epics/003-o-本地三语注音准确性与结构化交互升级/issues/010-o-日语书面振假名接入.md)。

## 日语词表为什么来自 UniDic 再生，而不是 misaki 自带

一句话：**是同一份词典，只把上游生成器丢掉的一列补回来**。misaki 的 `ja_lexicon.tsv.gz` 本来就是 UniDic 转的，但 `generate_ja_dict.py` 只存第 9 列 `pron`（发音表记），丢了第 20 列 `kana`（书面假名），于是 `東京→トーキョー`、`は→ワ`。假名要表达「怎么写」不是「怎么念」，猜不出来，只能有数据。

| 词面 | misaki 自带（pron） | 界面需要（kana） |
| --- | --- | --- |
| 東京 | トーキョー | トウキョウ |
| 今日 | キョー | キョウ |
| 凍り付く | コーリツク | コオリツク |

体积账（唯一变动的是词表，另两份资源原样照抄）：

| 资源 | misaki 自带 | 本包 | 差 |
| --- | ---: | ---: | ---: |
| `ja_lexicon.tsv.gz` | 8,286,957 | 10,143,927 | **+1.86 MB** |
| `ja_char_unk.tsv.gz` / `ja_matrix_compact.bin.gz` | 885 / 10,018,877 | 同 | 0 |
| 日语 DLL | 18.98 MiB | 20.76 MiB | **+1.86 MB（+9%）** |

代价可接受：多出的就是 87.9 万条词条各一列假名（gzip 后 +22%）；包按需下载、不进主 exe，精简包不带。收益是同一条词条同时给分词与 kana，不会出现「分词给了 東京、另挂的 kana 表却查不到」的分叉。

曾考虑但**未实测**的省体积方案：原版词表 + 运行时另挂一张「词面→kana」表。2026-11-15 决定保持现状，不再为此改动已验收的链。

## 语言包是怎么裁与怎么编的

```powershell
./scripts/phonetics/Build-MisakiPacks.ps1 -Stage All      # Prepare → Native → Verify
```

- **裁剪 = 资源白名单 + 源码白名单**，都写在 `scripts/phonetics/misaki-packs.json`，不在代码里硬编码。ja 只留 `ja_lexicon / ja_char_unk / ja_matrix_compact`（不要 `ja_words`）；en 只留 `en_lexicon_us / en_tokenizer / en_tagger`（**不含 espeak 回退表**，未收录词宁可不读），`zh_*` 与 `es/fr/hi/it/pt_*` 一律不进包。
- **Prepare**：NuGet 原件与包缓存只读；`git apply --check` 先验再打补丁；只导出白名单资源；日语词表换成再生版；跑 `tools audit-ipa` 全量审计（转不了的字符要能点名，不许静默丢）。下载只在缺失时发生（UniDic 压缩包 500 MiB 不会每次重拉）。
- **Native**：每包一份独立源码目录，只编本包需要的上游文件（ja 只编 `JapaneseTagger.cs`），加我们固定的 `Exports.cs`（C ABI）与 `KokoroIpa.cs`，用 `NativeAOT + Shared` 出 DLL；`InvariantGlobalization=false`（词典表是 UTF-8）。
- **产物落 `deps/phonetics/misaki/<lang>/`**：DLL + `pack.json` + `licenses/`；`.lib` 等副产品留在 `.temp/misaki-pack`，旧产物移入 `.temp/misaki-pack/previous`（不删）。
- **Verify**：413 项断言（哈希/字节/架构/ABI/四个导出/UTF-16 对齐/候选/释放重建/并发）。

## 将来怎么升级

1. **改 `misaki-packs.json`**：升 MisakiSharp 要同时换 version / commit / nupkg 与 assembly 哈希 / 逐文件源码哈希，并重做 `patches/misakisharp-2.2.0.patch`（锚点漂移就直接失败，不自动修补）。
2. **升 UniDic**：换 `dictionary.version/source/sysDic.sha256`，重跑 `RegenerateKana.py`（mtime=0，可重复生成）。**字段序号以同版 `dicrc` 为准**，不要照搬 3.1.0 的 `pron=9/kana=20`（早期照旧版第 17 列读到一堆 `*` 的产物已作废）。
3. **重跑构建链** → 得新 DLL 字节数与 SHA-256。
4. **回填 `NativePhoneticManifest.Packs`**：DLL 字节/SHA，许可文件变过也要同步。**只换 DLL 不改清单会被判为「需更新」并拒绝装载**。
5. **跑验收**：`./scripts/phonetics/verify`（413 项）+ `./scripts/dev.ps1 test`（Debug 与 AOT 宿主），再看日语金样本与分词变化是否可接受。
6. **上传 ModelScope 并更新哈希**（014 之后）：未上传前不要写「分发完成」。

NativeAOT 产物不承诺二进制可复现，所以每轮都覆盖 `pack.json`，并以哈希而非版本号判定身份。

## 未覆盖

- 英语 provider 未切换：结果面板的整词候选与 hover 仍在 011；日语单读（无候选）。
- 旧 ja 词典下载卡片仍与原生包卡片并存（契约内的过渡态），014 随旧 provider 一起下线。
- 真实网络下载与断网重试未验证；**未上传 ModelScope**，`scripts/sync-deps.py` 与 `scripts/dev.ps1 publish -full` 的依赖清单仍是旧路径（014 处理）。
- 未做干净系统 AOT 部署，也未在真实 UI 上练习重启替换与复制/选区/收藏回归。
