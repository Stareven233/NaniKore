# 注音词典再生管线（tools/phonetics）

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
| zh | CC-CEDICT 汉英词典 | https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz | CC BY-SA 4.0（见 NOTICE） |
| zh 补字 | Unihan `Unihan_Readings.txt`（kMandarin） | https://www.unicode.org/Public/UCD/latest/Unihan.zip | Unicode 许可（见 NOTICE） |

判据词表（`src/NaniKore/Phonetics/Data/`）不在此管线：常用漢字表 2010（文化庁 2136 字）、
通用规范汉字表一·二级（6500 字）、三语停用/高频策展表均为随程序集内嵌的静态资源，单独维护。

## 3. 日语（ja）：无编译，直接采用 IPAdic 编译态

1. `dotnet run --project src/NaniKore -- --inspect-models` 之外无需任何步骤：把 nupkg 解出的
   `sys.dic / unk.dic / matrix.bin / char.bin / COPYING` 复制到 `deps/phonetics/ja/` 即完成。
2. 复制 `IPAdic-COPYING.txt` 到同目录，与 NOTICE 一起构成许可归属。

## 4. 英语（en）：CMUdict → SQLite IPA 词典

一条命令即完成（无外部工具依赖）：

```powershell
dotnet run --project src/NaniKore -- --build-phonetics
```

管线（`PhoneticDictionaryBuilder.BuildEnglishDatabase`）：

1. 逐行读 `_source/cmudict.dict`，跳过 `;;;` 注释、`WORD(2)` 变音条目与单字母缩写；
2. 词面统一小写；ARPAbet → IPA 由 `ArpabetIpaConverter` 转换（应激位 1/2 转写为 `ˈ`/`ˌ`，
   紧跟其后元音；约定见下“口径备注”）；
3. 写入 `Data Source=...` 的单表（`words(word TEXT PRIMARY KEY, ipa TEXT) WITHOUT ROWID`）SQLite；
4. 落地 `deps/phonetics/en/ipa_dict.db`。

多音词只取 CMUdict 主读音（美式口径在生成期定音，不随运行期切换）；
屈折形式大多不在 CMUdict 中，运行期由 `EnglishLemmatizer`（词干还原候选）兜底查询。

## 5. 简体中文（zh）：CC-CEDICT + Unihan → MeCab 词典

需要 `mecab-dict-index.exe`（源码编译工具；Windows 二进制由社区发布，如 Pusnow/mecab-ko-msvc 的 release）：

```powershell
dotnet run --project src/NaniKore -- --build-phonetics --dict-index <path-to-mecab-dict-index.exe>
```

管线（`PhoneticDictionaryBuilder.BuildChineseDictionary`）：

1. 解析 CC-CEDICT（正则取 简体 / 繁体 / `[拼音]`），只收纯汉字词面；拼音声调数字 → 声调符号
   （`PinyinToneConverter`，含 ü/v 归一、轻声去数字）；
2. Unihan `kMandarin` 补充 CC-CEDICT 未覆盖的汉字单字默认读音；
3. 写 `_build/zh/main.csv`：`词面,0,0,成本,拼音`，成本 = 多字词 100 / 单字 3000（长词面优先）；
   同一词面的多个读音各自成条（`行 háng / xíng`），由 MeCab 按上下文自选；
4. 写骨架（`WriteMeCabSkeleton`）：`matrix.def`（1x1 + 非零项 `0 0 1`）、`char.def`（仅 BMP 区间，
   MeCab 的 char_property 要求区间端 < 0xFFFF，CJK 扩展 B 段不可写）、`unk.def`（三类默认 20000）、
   `dicrc`（**必须有 `cost-factor = 800`**，mecab-dict-index 缺它直接报
   `cost factor needs to be positive value`）；
5. 调 `mecab-dict-index -d _build/zh -o _build/zh -f utf-8 -t utf-8`；
6. 复制 `sys.dic / unk.dic / matrix.bin / char.bin / dicrc` 到 `deps/phonetics/zh/`。

全部门户文件用无 BOM UTF-8（骨架 `Utf8NoBom`）：BOM 会让 dicrc/CSV 首行解析失败
（表现为 `no such file or directory: dicrc`）。

## 6. 哈希回填（哈希即版本）

任一产物变化后，`--build-phonetics` 结束时会打印九个文件的 SHA-256 清单行
（`相对路径="HASH"`）。把它们回填到 `PhoneticDependencyManifest` 的 `JapaneseFiles /
EnglishFiles / ChineseFiles`，然后上传/同步到 ModelScope `Noetuu/nanikore-deps`
（`phonetics/<lang>/<file>` 直链，下载器用 `?download=true`）。

未匹配哈希时：设置页显示“缺失/待更新”，`ITextAnnotator.IsAvailable` 为 false，
标注链路整体软降级为纯文本，翻译主链路不受影响（见 `SelfTest.Phonetics.cs` 金样本）。

## 7. 排查工具

```powershell
dotnet run --project src/NaniKore -- --phonetics-probe   # 打印词典定位、判据规模、三语标注器实例输出
dotnet run --project src/NaniKore -- --self-test         # 全量无 UI 自测（含注音金样本，失败明细写 artifacts/self-test-failures.log）
```

## 8. 口径备注（实测，改代码前先读）

- **LibNMeCab 的 `node.Feature` 是「词面\t素性」整体**：拼音/读音取最后一个制表符后的内容
  （`ChinesePinyinAnnotator.ExtractFeature`）。`node.GetFeatureAt(0)` 返回的是整串。
- **IPAdic 的振假名取 `読み`（片假名）而非 `発音`**：`発音` 是音便表记（ちゅうちょ→ちゅーちょ），
  取错会出现长音符；`JapaneseFuriganaAnnotator` 统一转写平假名。
- **停用词表 vs 常用字符表的边界**：一·二级 6500 字覆盖面比直觉大（饕餮、尴尬、徘徊均在表内），
  仅难词模式下它们会被过滤；真正“表外”的词才注音（金样本用 `俳佪`）。
- **UTF-8 控制台**：诊断 CLI 会切 UTF-8 输出（`Program.UseUtf8Console`）；重定向时失败明细以
  `artifacts/self-test-failures.log`（UTF-8）为准。
- **en 词典落地用同路径重建**（先删后建），不做“临时文件 + Move”：SQLite 连接释放与 Windows 文件锁
  的交互在杀毒软件实时扫描下不稳定，移动会间歇性 `ERROR_SHARING_VIOLATION`。
