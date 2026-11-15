# MisakiSharp 穿刺笔记（2026-11-15）

> **同日复核与最终取舍：** 最新结论与复跑入口见 [界面注音适配穿刺](misaki-annotation-spike.md)。原版日语 `Reading` 是 `pron`，英语逐词结果是 Kokoro 编码，均需适配；裁剪/生命周期已补测通过。英语已决定直接采用 Misaki，允许美音发音变体，通过整词 hover 展示已收录候选，已知误选/缺项不阻塞本轮；适配和 UI 尚待实现。以下早期命令、体积与热态数字仅作历史记录，不是生产验收；新验证统一使用 `misaki-annotation-probe` 并将产物放 `.temp`。

可复用的实测结论与踩坑。探针源码（均仅本地、不发布）：
- `codestable/tools/misaki-spike/` — 各语输出/内部数据/资源格式。
- `codestable/tools/misaki-native/` — 把 MisakiSharp 编成原生 DLL（C ABI 导出）。
- `codestable/tools/misaki-native-client/` — 不引用 MisakiSharp 的 AOT 客户端，运行时 LoadLibrary 验证。

## 它是什么

`MisakiSharp 2.2.0`（NuGet，Apache-2.0，Lyrcaxis）：hexgrad/misaki 的 C# 原生移植，Kokoro TTS 的 G2P 引擎。netstandard2.0，内嵌 8 语数据。**它是给语音合成用的，输出口语化音素串，不是学习词典。**

## 关键结论速览

- **AOT 能加载原生 DLL**：把 MisakiSharp 编成 `NativeLib=Shared` 原生 DLL，AOT 主程序运行时 `LoadLibrary` + P/Invoke 可用（托管程序集不可运行时加载）。
- **纯便携无 VC++ 依赖**：MisakiNative.dll 导入表只有 `api-ms-win-crt-*`（Win10+ 内置 UCRT）+ KERNEL32/ADVAPI32/bcrypt/USER32/ole32；**无 VCRUNTIME140/MSVCP140，不需 VC++ Redist**。
- **早期资源体积估算**：官方 DLL 66MB≈内嵌资源 65.99MB；日 17.9 / 中 8.1 / 英核心 8.05 / espeak 系（en回退+5语）31.9 MB。这不是裁剪后原生包大小；正式的五种原生探针体积见新记录。
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
- 返回 `List<(Surface, Reading, CharType, IsUnk)>`：`Reading` 是片假名发音字段 `pron`，如 `過剰→カジョー`。直接转平假名仍不是书面振假名；同版 UniDic 的 `kana=20` 须另行再生，不能从长音符推回拼写。
- 数字标 `IsUnk=Y`、无读音（界面跳过）。`Phonemize` 的最终音素已口语化（は→βa），不能当界面注音。
- 限制：无音调核；多读需枚举 unidic 词条（trie + 矩阵耦合深，性价比低）。

### 英语（`EnglishG2P.Phonemize`）
- 返回 `(Phonemes, List<MToken>)`：有 `Text/Phonemes/Rating`，但音素仍含 `A/I/O` 等 Kokoro 编码；`Rating` 不是语境正确率。
- 早期 `read/record` 样例通过不能推广为消歧完成；新增 `I read every day.` 错读 `/red/`、`lead pipe` 错读 `/liːd/`，详见新记录。
- **词性候选在数据里**（`en_lexicon_us.tsv.gz`，`##GOLD/##SILVER`，`word\tKEY=phonemes`）：如 `record DEFAULT=ɹˈɛkəɹd VERB=ɹəkˈɔɹd`、`read DEFAULT=ɹˈid ADJ/VBD/VBN/VBP=ɹˈɛd`。候选不是完整异读字典；`lead=lˈid` 只有一条，缺金属义 `/led/`。
- 数字/货币/缩写已展开，markdown 还可能作为读音指令处理、原文前导空白被裁掉；须先关闭指令并验证原文对齐，不能仅凭 `Text` 名称认定所有输入可安全定位。

### 5 语（`EspeakG2P`，es/fr-fr/hi/it/pt-br）
- espeak 蒸馏，单音素串，无逐词对齐、无候选。上游 hi 约 53.6% 是与 espeak 输出的一致性，不是人审准确率。已拆出本轮三语交付。

## 内嵌资源清单（压缩字节，2026-11-15 实测）

| 语言组 | 资源 | 压缩 MB |
|---|---|---|
| espeak 系 | enus/engb_espeak + es/fr/hi/it/pt_espeak | 31.9 |
| 日 | ja_lexicon + ja_matrix_compact + ja_words + ja_char_unk | 17.9 |
| 中 | zh_g2p_v2 + zh_g2p + zh_posseg_hmm + zh_finalseg_hmm | 8.1 |
| 英核心 | en_lexicon_us/gb + en_tagger + en_tokenizer | 8.05 |

资源对照可用 Mono.Cecil 剥除；正式适配还需最小源码补丁、书面数据再生和生命周期接口，不再坚持“只剥资源、不改源码”。

## 踩坑清单

1. 中间产物也可能口语化或含模型编码；逐字段验证书面语义和显示格式，不能只靠入口名称判断。
2. 对齐用词元 `Text`/原文偏移，绝不用处理后长度。
3. 中文继续自研；日语原版 public Parse 不满足书面假名，需最小源码和数据修改。
4. 语言 DLL 使用 `InvariantGlobalization=false`；新矩阵已证明主 AOT 客户端可维持 invariant。真实宿主仍待集成。
5. 原生 DLL 已实测无 VC++ 依赖，仅系统 UCRT。
6. 原版 Lazy/静态缓存不提供闲置释放；修改版已验证数据可释放重建，DLL 不卸载，生产接入仍待完成。
