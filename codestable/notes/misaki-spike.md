# MisakiSharp 穿刺笔记（2026-11-15）

可复用的实测结论与踩坑。探针源码（均仅本地、不发布）：
- `codestable/tools/misaki-spike/` — 各语输出/内部数据/资源格式。
- `codestable/tools/misaki-native/` — 把 MisakiSharp 编成原生 DLL（C ABI 导出）。
- `codestable/tools/misaki-native-client/` — 不引用 MisakiSharp 的 AOT 客户端，运行时 LoadLibrary 验证。

## 它是什么

`MisakiSharp 2.2.0`（NuGet，Apache-2.0，Lyrcaxis）：hexgrad/misaki 的 C# 原生移植，Kokoro TTS 的 G2P 引擎。netstandard2.0，内嵌 8 语数据。**它是给语音合成用的，输出口语化音素串，不是学习词典。**

## 关键结论速览

- **AOT 能加载原生 DLL**：把 MisakiSharp 编成 `NativeLib=Shared` 原生 DLL，AOT 主程序运行时 `LoadLibrary` + P/Invoke 可用（托管程序集不可运行时加载）。
- **纯便携无 VC++ 依赖**：MisakiNative.dll 导入表只有 `api-ms-win-crt-*`（Win10+ 内置 UCRT）+ KERNEL32/ADVAPI32/bcrypt/USER32/ole32；**无 VCRUNTIME140/MSVCP140，不需 VC++ Redist**。
- **体积由内嵌资源构成**：官方 DLL 66MB≈内嵌资源 65.99MB；按语言裁剪后：日 17.9 / 中 8.1 / 英核心 8.05 / espeak 系（en回退+5语）31.9 MB。裁剪成「日+英(无espeak)」≈26MB 资源。
- **中文无多音候选**：单字键 20924、**多读=0**；对外只吐注音符号/IPA（TTS）。
- **英语多音在数据里**：词典按「词性=音素」存多读，可直接取（见下）。

## 运行探针

```powershell
cd codestable/tools/misaki-spike
dotnet run -c Debug                                  # 各语输出 + 内部数据 + 资源格式 + cand_report.txt
dotnet publish -c Release -r win-x64 -p:Aot=true     # AOT 可行性

cd ../misaki-native
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:NativeLib=Shared   # 出 MisakiNative.dll(~70MB)

cd ../misaki-native-client
dotnet publish -c Release -r win-x64                  # 出小客户端(~1.4MB)
copy ..\misaki-native\bin\Release\net11.0\win-x64\publish\MisakiNative.dll bin\Release\net11.0\win-x64\publish\
bin\Release\net11.0\win-x64\publish\MisakiNativeClient.exe   # LoadLibrary 动态加载 + 跨边界取数据
```

实测：客户端 1.42MB，MisakiNative.dll 69.91MB；LoadLibrary err=0；zh/ja/en 跨边界返回正确。
性能（热态）：中文 100 句 10–22ms，日语 100 句 3–10ms。冷启动走 Lazy。

## 各语中间产物

### 中文（`ChineseG2P`，static 类）
- `Phonemize`→注音符号；`PhonemizeLegacy`→带调 IPA。**都是 TTS 最终产物，且已变调**。
- 内部 `modernHanziToPinyin`（私有静态字段，43.3 万条）：词组→逐字拼音（带调）。**变调前**。单字键 20924、**多读=0**（`行→xing2`、`长→zhang3`、`还→hai2` 各只有一个音）。
- **限制**：static 类 → 无法用 `UnsafeAccessor`（C# 不允许 static 类作参数类型），只能反射 + `[DynamicDependency]`；且数据本身没有候选。底座是降维后的 pypinyin。
- 结论：**中文不切 Misaki，保留自研引擎**。

### 日语（`JapaneseTagger.Parse`，public static）
- 返回 `List<(Surface, Reading, CharType, IsUnk)>`：`Reading` 片假名（转平假名得振假名），如 `日本→ニッポン`、`睡眠→スイミン`、`過剰→カジョー`。基于 UniDic 3.1.0。
- 数字标 `IsUnk=Y`、无读音（界面跳过）。`Phonemize` 的最终音素已口语化（は→βa），不能当界面注音。
- 限制：无音调核；多读需枚举 unidic 词条（trie + 矩阵耦合深，性价比低）。

### 英语（`EnglishG2P.Phonemize`）
- 返回 `(Phonemes, List<MToken>)`：每 token 有 原文 `Text` + `Phonemes` + `Rating`，按 token 对齐原文。
- 词性/时态消歧强：`read` 过去/现在、`record` 动词/名词 实测全对。
- **候选在数据里**（`en_lexicon_us.tsv.gz`，`##GOLD`/`##SILVER`，行格式 `word\tKEY=phonemes\tKEY=phonemes`，KEY 为词性/`DEFAULT`）：如 `record DEFAULT=ɹˈɛkəɹd VERB=ɹəkˈɔɹd`、`read DEFAULT=ɹˈid ADJ/VBD/VBN/VBP=ɹˈɛd`、`close/present/object/project/desert/use/wind/live/minute/content/produce` 均多读。注意 `lead=lˈid` 只一条（lead/led 拼写同形不在数据里）。
- 坑：数字/货币/缩写展开（`$50`→fifty dollars、`Dr.`→doktər），token 原 `Text` 不变，按 token 对齐即可。

### 5 语（`EspeakG2P`，es/fr-fr/hi/it/pt-br）
- espeak 蒸馏，单音素串，无逐词对齐、无候选。质量一般（hi 约 53%）。

## 内嵌资源清单（压缩字节，2026-11-15 实测）

| 语言组 | 资源 | 压缩 MB |
|---|---|---|
| espeak 系 | enus/engb_espeak + es/fr/hi/it/pt_espeak | 31.9 |
| 日 | ja_lexicon + ja_matrix_compact + ja_words + ja_char_unk | 17.9 |
| 中 | zh_g2p_v2 + zh_g2p + zh_posseg_hmm + zh_finalseg_hmm | 8.1 |
| 英核心 | en_lexicon_us/gb + en_tagger + en_tokenizer | 8.05 |

裁剪方式：Mono.Cecil 构建期剥掉不需要的 `<EmbeddedResource>`（不 fork 源码，升级重跑）

## 踩坑清单

1. 别把 `Phonemize` 当界面注音——口语化、变调、展数字。取中间产物。
2. 对齐用词元 `Text`/原文偏移，绝不用处理后长度。
3. `UnsafeAccessor` 绑不了 static 类；日语用 public 的 `Parse`，中文走自研。
4. AOT 必须 `InvariantGlobalization=false`，私有反射成员要 `[DynamicDependency]`。
5. 原生 DLL 已实测无 VC++ 依赖，仅系统 UCRT。
6. Lazy 单例 + 后台线程 + 锁；内存按语言懒加载（首次用到某语才建表）。
