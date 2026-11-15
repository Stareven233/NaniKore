# 日英原生注音语言包：构建链与实测

> 2026-11-15。对应 [Epic 003 / 008](../../epics/003-o-本地三语注音准确性与结构化交互升级/issues/008-o-原生语言包正式重打包与数据再生.md)。
> 本文只记录正式构建链的产物与实测；**生产 provider 尚未切换**，主程序仍用 IPAdic / CMUdict / 外部中文数据（009 起接入）。
> 穿刺结论与旧口径失败证据见 [注音适配穿刺](misaki-annotation-spike.md)。

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

## 未覆盖

- 主程序尚未接入：下载/校验/状态机/闲置释放/重启更新在 009，日语与英语 provider 在 010/011。
- 未做真实宿主 UI、复制/选区/收藏回归，未做干净系统 AOT 部署。
- NativeAOT 产物字节不承诺可复现；`pack.json` 记录的是本次实测值，重跑只覆写清单与包文件。
- 未上传 ModelScope；`scripts/sync-deps.py` 与 `scripts/dev.ps1 publish -full` 的依赖清单仍是旧路径（014 处理）。
