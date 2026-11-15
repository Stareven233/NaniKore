# 注音词典再生管线

> **计划复核（2026-11-15）：** 本轮仅中日英。中文拟改数据内嵌；日语拟用最小修改的 Misaki 原生 DLL，并从同版 UniDic 再生书面 kana；英语已确定直接采用 Misaki 选音和已收录候选，转换为可读美音 IPA 并通过整词 hover 展示异读；允许发音变体，已知误选/缺项不阻塞本轮，生产适配尚未实施。原生架构与数据释放已隔离验证，5 语另立探索。**本文件正文仍描述当前生效的旧管线，切换实施后再重写，不能据本次穿刺更新生产哈希或宣称远端分发已完成。** 决策见 [talks/004](../talks/004-本地三语注音优化与后续能力规划.md)，最新证据见 [注音适配穿刺](misaki-annotation-spike.md)。

本地注音引擎的词典二进制从公开数据源再生的完整流程。本文档面向维护者；运行期行为（按语言下载、哈希校验、软降级）见 `src/NaniKore/Phonetics/PhoneticDependencies.cs` 与 `deps/README.md`。

## 1. 目录契约

```text
deps/phonetics/            # 仓库内唯一真实源；ModelScope 仓库 Noetuu/nanikore-deps 的 phonetics/ 严格镜像
├─ ja/  en/  zh/           # 运行期产物（gitignore，保留目录占位）
├─ _source/                # 公开数据源原件（gitignore）
└─ _build/zh/              # MeCab 编译中间产物（gitignore）
```

`deps/phonetics/{ja,en,zh}/` 的每个文件都必须与 `src/NaniKore/Phonetics/PhoneticDependencies.cs` 中
`PhoneticDependencyManifest` 登记的 SHA-256 一致（哈希即版本；不符时设置页提示更新、运行期软降级）。

## 2. 数据源与许可

| 语言 | 来源 | 获取 | 许可 |
| --- | --- | --- | --- |
| ja | IPAdic（taku910/mecab 官方词典） | NuGet `LibNMeCab.IpaDicBin` 0.10.2（nupkg 内 `content/IpaDic/`） | IPAdic 许可（文件随附 COPYING，见 NOTICE） |
| en | CMUdict 0.7b | https://raw.githubusercontent.com/cmusphinx/cmudict/master/cmudict.dict | CMUdict 许可（BSD 风格，见 NOTICE） |
| zh | CC-CEDICT 汉英词典 | https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz → `resources/phonetics/cc-cedict/` | CC BY-SA 4.0（见 NOTICE） |
| zh | pypinyin 0.55.0（词组表 + 单字频率默认） | pypinyin wheel → `resources/phonetics/pypinyin/` | MIT（数据派生自 CC-CEDICT 等，见其 METADATA） |
| zh 补字 | Unihan `Unihan_Readings.txt`（kMandarin） | https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip → `resources/phonetics/unihan/` | Unicode 许可（见 NOTICE） |

判据词表（`src/NaniKore/Phonetics/Data/`）不在此管线：学年別漢字配当表第1・2学年（文部科学省 240 字，2026-10-04 起由 2136 字收紧）、
通用规范汉字表一级字表（3500 字，2026-10-04 起取代此前来源不明的 6489 字混合表）、三语停用/高频策展表
均为随程序集内嵌的静态资源，单独维护。表内 `#` 注释行只承载来源/许可/口径元信息，
`EmbeddedData` 装载时按行跳过——切勿把例字写进注释行（曾因此把「龃龉」误收进判据集合）。

## 3. 日语（ja）：无编译，直接采用 IPAdic 编译态

1. `dotnet run --project src/NaniKore -- --inspect-models` 之外无需任何步骤：把 nupkg 解出的
   `sys.dic / unk.dic / matrix.bin / char.bin / COPYING` 复制到 `deps/phonetics/ja/` 即完成。
2. 复制 `IPAdic-COPYING.txt` 到同目录，与 NOTICE 一起构成许可归属。

## 4. 英语（en）：CMUdict → SQLite IPA 多读音词典

只重建英语（不触碰中文产物，避免无关哈希漂移）：

```powershell
dotnet run --project src/NaniKore -- --build-en-data
```

（与中文一起重建仍用 `--build-phonetics`。）

管线（`PhoneticDictionaryBuilder.BuildEnglishDatabase`）：

1. 逐行读 `_source/cmudict.dict`，跳过 `;;;` 注释、`#` 行尾来源标签与单字母缩写；`WORD(n)` 的编号读音**作为候选保留**（不再丢弃）；
2. 词面统一小写；ARPAbet → IPA 由 `ArpabetIpaConverter` 转换，音节切分、重音归位与长音符补全
   统一委托 `IpaTranscription`（应激位 1/2 转写为 `ˈ`/`ˌ` 并放到音节首，音节点 `.` 分隔音节，
   重读 `AH` 记 `ʌ`；约定见下“口径备注”）；
3. 写入多读音表（`words(word TEXT, variant INTEGER, ipa TEXT, PRIMARY KEY(word, variant)) WITHOUT ROWID`），`variant=1` 为默认读音；
4. 落地 `deps/phonetics/en/ipa_dict.db`（约 126,026 词面 / 135,139 条读音，含编号备选）。

运行期 `EnglishIpaDictionary`：`Lookup` 取 `variant` 最小者（默认读音，口径不变）；`LookupAll` 返回该词全部候选（升序），供后续多音查看。屈折形式大多不在 CMUdict 中，运行期由 `EnglishLemmatizer`（词干还原候选）兜底查询。英语多音 UI（橙色/右键列表）尚未做，本轮只落数据。

## 5. 简体中文（zh）：pypinyin + CC-CEDICT + Unihan → 进程内拼音数据

中文不再使用 NMeCab 与 `mecab-dict-index`：`ChinesePinyinEngine` 在进程内做词组最长匹配。
源料统一放 `resources/phonetics/`（`pypinyin/`、`cc-cedict/`、`unihan/`），单独重建：

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

运行期 `ChinesePinyinEngine.Segment`：代理项边界切段 → 词表最长匹配（词组→单字默认）→ 逐字默认音 + 候选，
全程保留原文 UTF-16 偏移；`AnnotationSyllable.Candidates` 携带候选（首项为本句默认）。缺失/损坏数据软降级为纯文本。

## 6. 哈希回填（哈希即版本）

任一产物变化后，`--build-phonetics` 结束时会打印九个文件的 SHA-256 清单行
（`相对路径="HASH"`；漏看也可以用 `python scripts/sync-deps.py phonetics --print-hashes`
随时重算）。把它们回填到 `PhoneticDependencyManifest` 的 `JapaneseFiles /
EnglishFiles / ChineseFiles`，然后用通用同步脚本上传到 ModelScope `Noetuu/nanikore-deps`
的 `phonetics/` 子树（下载器取 `phonetics/<lang>/<file>` 直链，用 `?download=true`）：

```powershell
python scripts/sync-deps.py phonetics --dry-run   # 预检：列文件 + 自动核对源码清单哈希，不联网
python scripts/sync-deps.py phonetics             # 核对通过后上传
```

`phonetics` 已绑定 `PhoneticDependencyManifest`，同步前自动核对，无需手填 `--verify`；
其余用法与令牌配置见 `deps/README.md`「同步到 ModelScope」。

未匹配哈希时：设置页显示“缺失/待更新”，`ITextAnnotator.IsAvailable` 为 false，
标注链路整体软降级为纯文本，翻译主链路不受影响（见 `SelfTest.Phonetics.cs` 金样本）。

## 7. 排查工具

```powershell
dotnet run --project src/NaniKore -- --phonetics-probe   # 打印词典定位、判据规模、三语标注器实例输出
dotnet run --project src/NaniKore -- --self-test         # 全量无 UI 自测（含注音金样本，失败明细写 artifacts/self-test-failures.log）
```

## 8. 口径备注（实测，改代码前先读）

- **中文已弃 NMeCab**：改为进程内 `ChinesePinyinEngine`（pypinyin + CC-CEDICT + Unihan 合并表）。读音不再来自 MeCab `Feature`，而是词表最长匹配 + 单字默认；输入仍在代理项边界分段，补充平面字保留纯文，避免破坏后续已知词的 UTF-16 偏移。旧的 `sys.dic` 等 MeCab 产物已不再被中文链路读取。
- **IPAdic 的振假名取 `読み`（片假名）而非 `発音`**：`発音` 是音便表记（ちゅうちょ→ちゅーちょ），
  取错会出现长音符；`JapaneseFuriganaAnnotator` 统一转写平假名。
- **停用词表 vs 常用字符表的边界**：zh 判据为通用规范汉字表**一级字表 3500 字**（2026-10-04 由一·二级 6500
  收紧——6500 把龃龉、餮、魑这类二级字全罩住，与「仅注难词」的直觉相悖）。一级表下：龃龉、魑魅（魑）、
  饕餮（餮）、俳佪等表外/二级字词正常注音；**尴尬、徘徊仍不注**——尴/尬/徘/徊按国标均在一级字表内，
  若要覆盖它们需换成语料词频表（AnnotationProfile 方案 B 接口已预留）。ja 侧同理：此前常用漢字表 2136 字把
  薬、投、与及過剰摂取、致死量等词全罩住导致几乎不注音，现已收紧为**学年別漢字配当表第1・2学年（240 字）**——
  仅排除最常见简单的基础字（日、本、人、行、来、見、食、先生等），小3及以上的进阶汉字（薬、投、与等）及初高中
  难词（過剰摂取、致死量、睡眠薬、6錠等）均获得注音；叠字符号（々等）依附前字判定，不单独作为生僻字。
- **UTF-8 控制台**：诊断 CLI 会切 UTF-8 输出（`Program.UseUtf8Console`）；重定向时失败明细以
  `artifacts/self-test-failures.log`（UTF-8）为准。
- **en 词典落地用同路径重建**（先删后建），不做“临时文件 + Move”：SQLite 连接释放与 Windows 文件锁
  的交互在杀毒软件实时扫描下不稳定，移动会间歇性 `ERROR_SHARING_VIOLATION`。
- **词典式 IPA 由 `IpaTranscription` 统一产出**（`ArpabetIpaConverter` 与运行期读取共用）：
  音节切分按「每个元音核一个音节 + 辅音在核间分配」，辅音归属两条口径——重读短元音后首辅音收尾
  （sister `ˈsɪs.tɜːr`、apple `ˈæp.əl`、banana `bə.ˈnæn.ə`），长元音/双元音或非重读音节后按最大
  onset 原则尽量多划给后一音节且起首簇必须合法（treason `ˈtriː.zən`、photograph `ˈfoʊ.tə.ˌɡræf`）；
  重音位前移到音节首，音节点 `.` 分隔，`IY/UW` 带长音符（`iː/uː`），重读 `AH` 记 `ʌ`。
  **已发布的历史条目是旧 wire format**（重音贴在元音前、无音节点、`IY` 不带 ː，如 treason 存的是
  `trˈizən`），因此 `EnglishIpaDictionary.Lookup` 读出后必须过 `IpaTranscription.Normalize` 归一——
  ipa_dict.db 是「哈希即版本」的分布式产物，不能随代码一起重建换哈希；该归一对新口径条目幂等。
  **2026-11-14（P4）已重生 ipa_dict.db 为多读音表并回填新哈希，运行期这一遍对新条目即空转。**
