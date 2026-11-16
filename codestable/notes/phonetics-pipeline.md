# 注音数据再生管线

本地注音数据的二进制从公开数据源再生的完整流程。本文档面向维护者；运行期行为（按语言下载、哈希校验、软降级）见
`src/NaniKore/Phonetics/`（中文 `ChinesePinyinData.cs`，日英 `Native/`）与 `deps/README.md`。

## 1. 目录契约

```text
deps/phonetics/            # 仓库内唯一真实源；ModelScope 仓库 Noetuu/nanikore-deps 的 phonetics/ 严格镜像
├─ misaki/{ja,en}/        # 日英原生语言包（scripts/phonetics/Build-MisakiPacks.ps1 产物）
└─ zh/                    # 中文拼音数据 zh_pinyin.txt.gz（--build-zh-data 产物）
```

三方严格镜像：仓库 `deps/` ↔ ModelScope `phonetics/` ↔ 应用目录 `deps/phonetics/`。没有版本号别名目录，哈希即版本。
每条数据的哈希登记在宿主侧清单里：日英原生包 → `Native/NativePhoneticManifest.cs`（与包内 `pack.json` 双向核对），
中文拼音表 → `ChinesePinyinData.cs`。

## 2. 数据源与许可

| 语言 | 来源 | 获取 | 许可 |
| --- | --- | --- | --- |
| ja / en | MisakiSharp 2.2.0（+ 日语 UniDic 3.1.0） | NuGet `MisakiSharp` 2.2.0 + 固定提交的最小补丁，见 `scripts/phonetics/misaki-packs.json` | Apache-2.0 / BSD-3-Clause（见 NOTICE 3.2） |
| zh | CC-CEDICT 汉英词典 | https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz → `resources/phonetics/cc-cedict/` | CC BY-SA 4.0（见 NOTICE 3.1） |
| zh | pypinyin 0.55.0（词组表 + 单字频率默认） | pypinyin wheel → `resources/phonetics/pypinyin/` | MIT（数据派生自 CC-CEDICT 等，见其 METADATA） |
| zh 补字 | Unihan `Unihan_Readings.txt`（kMandarin） | https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip → `resources/phonetics/unihan/` | Unicode 许可（见 NOTICE 3.1） |

判据词表（`src/NaniKore/Phonetics/Data/`）不在此管线：学年別漢字配当表第1・2学年（文部科学省 240 字）、
通用规范汉字表一级字表（3500 字）、三语停用/高频策展表均为随程序集内嵌的静态资源，单独维护。
表内 `#` 注释行只承载来源/许可/口径元信息，`EmbeddedData` 装载时按行跳过——切勿把例字写进注释行
（曾因此把「龃龉」误收进判据集合）。

## 3. 日英原生语言包（misaki/）

构建链在 `scripts/phonetics/`：`Build-MisakiPacks.ps1` 固定上游 nupkg 与提交、套 `patches/misakisharp-2.2.0.patch`、
按资源白名单编译 NativeAOT DLL，`MakePackManifest.py` 写 `pack.json`，`Verify` 段核对包字节/哈希/金样本。
日语书面假名由同版 `UniDic 3.1.0` 的 `sys.dic` 再生（`RegenerateKana.py`：保留原字段，追加第 20 字段 `kana`）。
构建与实测口径见 [日英原生注音语言包](phonetics-packs.md)；本项只做宿主侧适配，DLL 无改动时不重新生成包字节。

## 4. 简体中文（zh）：pypinyin + CC-CEDICT + Unihan → 单份合并表

`ChinesePinyinEngine` 在进程内做词组最长匹配，数据放 `deps/phonetics/zh/` 按需下载。源料统一放 `resources/phonetics/`
（`pypinyin/`、`cc-cedict/`、`unihan/`），单独重建：

```powershell
dotnet run --project src/NaniKore -- --build-zh-data
```

管线（`ChinesePinyinDataBuilder.Build`）：

1. 载 `pypinyin/phrases_dict.json`（词组 → 逐字默认读音）与 `pinyin_dict.json`（码位 → 候选读音，首项默认）作基座；
2. 解析 `cc-cedict/...txt`：只收纯汉字词面；拼音声调数字 → 声调符号（`PinyinToneConverter`）。
   CC 词条**只补 pypinyin 未覆盖的词面**（pypinyin 优先），并收单字读音作兜底；
3. 词级等长对齐构建 繁→简 单字映射（查音前归一，展示仍用原文）；
4. `unihan/Unihan_Readings.txt` 的 `kMandarin` 兜底 pypinyin/CC 都没覆盖的 BMP 单字；
5. 写 `deps/phonetics/zh/zh_pinyin.txt.gz`（TSV+gzip，全量排序保证确定性）：
   `S 码位 候选1,候选2` / `C 码位 兜底读音` / `P 词面 字1音 字2音 …` / `T 繁 简`，首行 `NaniKore.zh.pinyin.v1`。
   生成结束会打印字节数与 SHA-256，回填到 `ChinesePinyinData.Sha256`（自测与下载器都按它校验）。

运行期 `ChinesePinyinEngine.Segment`（`TryLoad` 读文件，读完即关；缺失/版本不符/解压失败直接软降级为纯文）：
代理项边界切段 → 词表最长匹配（词组→单字默认）→ 逐字默认音 + 候选，全程保留原文 UTF-16 偏移；
`AnnotationSyllable.Candidates` 携带候选（首项为本句默认）。

## 5. 哈希回填与上传（哈希即版本）

`--build-zh-data` 结束会打印产物字节数与 SHA-256；任一产物变化后都要把新哈希回填到清单，再上传：

```powershell
python scripts/sync-deps.py phonetics --print-hashes   # 重算白名单内全部文件哈希（按各清单口径输出）
python scripts/sync-deps.py phonetics --dry-run        # 预检：列文件 + 自动核对源码清单，不联网
python scripts/sync-deps.py phonetics                  # 核对通过后上传
```

`phonetics` 绑定 `ChinesePinyinData.cs` 与 `Native/NativePhoneticManifest.cs`，同步前自动核对，无需手填 `--verify`；
上传只走白名单（`misaki/*` 与 `zh/zh_pinyin.txt.gz`），令牌与其余用法见 `deps/README.md`「同步到 ModelScope」。

未匹配哈希时：设置页显示「缺失/待更新」，`ITextAnnotator.IsAvailable` 为 false，标注链路整体软降级为纯文本，
翻译主链路不受影响（见 `SelfTest.Phonetics.cs` 金样本）。

## 6. 排查工具

```powershell
dotnet run --project src/NaniKore -- --phonetics-probe   # 打印数据定位、包完整性、判据规模与三语标注器实例输出
dotnet run --project src/NaniKore -- --self-test         # 全量无 UI 自测（失败明细写 artifacts/self-test-failures.log）
```

## 7. 口径备注（实测，改代码前先读）

- **中文早弃 NMeCab**：进程内 `ChinesePinyinEngine` 词表最长匹配 + 单字默认，输入仍在代理项边界分段，
  补充平面字保留纯文，避免破坏后续已知词的 UTF-16 偏移。
- **停用词表 vs 常用字符表的边界**：zh 判据为通用规范汉字表**一级字表 3500 字**（2026-10-04 由一·二级 6500 收紧——
  6500 把龃龉、餮、魑这类二级字全罩住，与「仅注难词」的直觉相悖）。一级表下：龃龉、魑魅（魑）、饕餮（餮）、
  俳佪等表外/二级字词正常注音；**尴尬、徘徊仍不注**——尴/尬/徘/徊按国标均在一级字表内，若要覆盖它们需换成
  语料词频表（AnnotationProfile 方案 B 接口已预留）。ja 侧同理：常用漢字表 2136 字会把薬、投、与及過剰摂取、
  致死量等词全罩住，现收紧为**学年別漢字配当表第1・2学年（240 字）**——仅排除最常见简单的基础字
  （日、本、人、行、来、見、食、先生等），小3及以上进阶汉字（薬、投、与等）与初高中难词
  （過剰摂取、致死量、睡眠薬、6錠等）均获得注音；叠字符号（々等）依附前字判定。
- **UTF-8 控制台**：诊断 CLI 会切 UTF-8 输出（`Program.UseUtf8Console`）；重定向时失败明细以
  `artifacts/self-test-failures.log`（UTF-8）为准。
- **日语书面假名取 UniDic 第 20 字段 `kana`**，不取 `pron`：`pron` 是音便表记（ちゅうちょ→ちゅーちょ），
  取错会出现长音符；`pron=9/kana=20` 的字段位置由 `scripts/phonetics/misaki-packs.json` 固定。
- **英语 IPA 用 Misaki 选音 + 词典候选**，宿主只做「可读美音 IPA」与整词候选折叠（`EnglishReadingCandidates`）：
  允许 `ɾ/ʔ` 等发音变体，仅差 `ˈ/ˌ` 的条目视为同一读音。read/lead 一类误选与缺项是上游已知限制，
  不引入第二套 CMUdict 数据链。
