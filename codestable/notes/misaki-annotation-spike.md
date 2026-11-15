# Misaki 界面注音适配、裁剪与生命周期穿刺

> 2026-11-15。结论：**日语书面字段、原生包、ABI、词表释放可行；英语词典式 IPA 与语境选音未达到生产切换条件。** 本次只新增隔离探针和文档，未改生产 provider、配置版本、词典哈希或 ModelScope。
> 决策：[talks/004](../talks/004-本地三语注音优化与后续能力规划.md)；执行：[Epic 003](../epics/003-o-本地三语注音准确性与结构化交互升级/spec.md)；源码与复跑：[探针 README](../tools/misaki-annotation-probe/README.md)。

## 固定输入与复跑

- Windows x64，`DOTNET_ROOT` 的 .NET SDK 11.0.100，Python 3.14。
- MisakiSharp 2.2.0，NuGet 元数据提交 `beb91a9f06ebe5e25e595b5a3c990ee616ad75e5`；源文件 SHA-256 写入 `source-manifest.json`，原件留在 `.temp/.../upstream/`。
- Mono.Cecil 0.11.6：只在 `.temp` 副本中移除非白名单资源，包缓存不改。
- UniDic 3.1.0 压缩包：<https://cotonoha-dic.s3-ap-northeast-1.amazonaws.com/unidic-3.1.0.zip>，来源索引为 [unidic-py/dicts.json](https://github.com/polm/unidic-py/blob/master/dicts.json)。下载 525,943,040 bytes，SHA-256 `638718C4C63625AB300DE4C92C67925D54C0E9E3830009EAA992F29819D59C43`。
- `sys.dic` SHA-256：`F019F95838242CD614953A25201AD0B623B9C1CBCA90DE2507DF4510DB1B192C`。这是本次观测指纹，不是上游签名验证。

```powershell
./codestable/tools/misaki-annotation-probe/Invoke-Probe.ps1 -Stage All
```

可分 `Prepare / Native / Run` 执行。最终 `Native` 与 `Run` 均退出 0；所有下载、解包、编译、测量日志和 JSON 报告落 `.temp/misaki-annotation-spike/`。原始资源、增补词典、修改源码和原生包均不进入桌面发布。完整输出与哈希见 `summary.json`、`source-manifest.json`、`kana-report.json`；报告中 `dictionary_sha256` 为连续 64 位值。

## 结果矩阵

| 变体 | Debug 客户端 | AOT 客户端 | 断言口径 |
| --- | ---: | ---: | --- |
| 原版日语裁剪 | 50/50 | 50/50 | 复现发音表记、对齐、错误码；不具备释放接口 |
| 原版英语核心 | 195/195 | 195/195 | 无回退仍能处理词典词，约定过滤与 ABI |
| 原版英语含回退 | 199/199 | 199/199 | 含未登录词与回退请求 |
| 修改版日语 | 51/51 | 51/51 | 书面假名、旧对象回收、重建、并发 |
| 修改版英语 | 213/213 | 213/213 | 原文指令关闭、空白/代理项处理、整词候选和释放 |
| 修改版日英同进程 | — | 264/264 | 两个自包含 NativeAOT DLL 共存 |
| invariant AOT 客户端 + 非 invariant 日英 DLL | — | 264/264 | 主 exe 不必随 DLL 一律关闭 invariant |

总计 1,944 次断言通过。**这是架构与约定行为矩阵，包含已知缺陷复现；不代表下文英语质量门槛通过。** Debug/AOT 指调用端；语言 DLL 均为 Release NativeAOT。不是 Avalonia 生产宿主、UI 操作或干净系统部署验收。

## 日语：恢复书面假名已通过

官方 `JapaneseTagger.Parse` 文档就注明返回 `pron`，生成器 `generate_ja_dict.py` 只保存原始第 9 列。原版实测：`東京→トーキョー`、`躊躇→チューチョ`、`は→ワ`。取公开 tagger 并不能自动避开 TTS 表记。

同版 `dicrc` 明确 `pron=9`、`kana=20`。首次试验沿用旧版第 17 列时读到大量 `*`，该产物已移入 `.temp/.../invalid-field17/`，不作为证据；最终脚本解析并断言实际字段定义，不能照搬 UniDic 2.1.2 的列号。

最终再生脚本逐条匹配：词面、条目数量/顺序、左右连接 ID、成本和原始 `pron`。**674,791 个词面、878,989 条词条，身份失配 0。** 在保留原字段的第五列追加 `kana`，190,973 条的 `pron` 与 `kana` 不同；新词典 gzip 10,144,093 bytes。

| 词面 | 原始 pron | 增补 kana |
| --- | --- | --- |
| 東京 | トーキョー | トウキョウ |
| 躊躇 | チューチョ | チュウチョ |
| 今日 | キョー | キョウ |
| 凍り付く | コーリツク | コオリツク |
| は | ワ | ハ |
| へ | エ | ヘ |

最小修改版让 Parse 返回 `kana`，分词和矩阵算法保持原样。Debug/AOT 输出书面假名样例正确，重复词、空白、emoji 后的词能映射回原文 UTF-16 区间。

**边界：** 不把表格里的助词变成生产注音目标；生产仍按现有日语规则只给含汉字词面注音。此次未接送假名剥离、难词/停用过滤、10 词阈值、复制/选区/收藏，也未证明 Misaki 压缩连接矩阵与完整 UniDic 在所有文本分词相同。全量身份匹配证明数据来源对得上，不等于全量分词质量验证。

## 英语：部分可适配，质量门槛未通过

### 编码与词典式 IPA

逐词 `Phonemes` 仍使用 Kokoro 编码：`price→pɹˈIs`、`tomorrow→təmˈɑɹO`；词典也使用这种编码。`ProbeIpa.cs` 显式映射双元音、合字、元音与重音后交给现有 `IpaTranscription` 排版。它只做 en-US 试验，不是生产转换器，也不能直接套给 en-GB。

关闭 `flapsToT` 能避免进一步将 `ɾ→T`、`ʔ→t`，但**词典内已经有闪音/喉塞音，关开关不能恢复原始 /t/ 或 /d/**。严格转换器拒绝这些输入，不猜读音。全量 en-US 366,846 条非空条目变体中：332,221 可转换，34,625 拒绝，约 9.44%；计数含大小写增长条目及 POS 变体，不是词面覆盖率。转换成功也没有人审准确率保证。

`water→wˈɔɾəɹ`、`better→bˈɛɾəɹ`、`city→sˈɪɾi` 均被严格转换器拒绝。安全跳过证明错误不会进入 UI，**不能证明常用词注音达标**。英语接入前必须决定如何从未丢失区分信息的来源取得词典式音位，并验证全量符号与质量样本；不能统一将闪音猜成 /t/。

### 语境选音反例

| 文本 | 实测 | 判断 |
| --- | --- | --- |
| They will record a new record tomorrow. | 动词/名词重音不同 | 本例通过 |
| I read a book yesterday, but please read it again. | /red/ 与 /riːd/ | 本例通过 |
| I read every day. | /red/，POS=VBP | **失败**，现在时应 /riːd/ |
| They read it yesterday. | /red/，POS=VBP | 本例读音对，但不能据此称时态识别已完成 |
| He can lead the way with a lead pipe. | 两个 lead 均 /liːd/ | **失败**，金属义 /led/ 不在该词候选里 |

因此撤回早期“read/record 实测全对→词性/时态消歧强”的推广结论。词性表不是任意语义消歧器；`Rating` 是词源等级，不是经校准的语境正确率。

### 补充：与当前生产 CMUdict 的同句对照

直接只读查询现有 `deps/phonetics/en/ipa_dict.db`，并在 `.temp/cmu-comparison/` 隔离工程中链接生产 `EnglishIpaDictionary`、`EnglishIpaAnnotator`、`AnnotationContracts`、`IpaTranscription` 和 `RubyTextParser`。仅把依赖路径固定到仓库词典，候选记录沿用生产字段；注音、过滤、默认查询和去重走生产实现，没有修改主程序。词典 SHA-256：`CA3B1DC874AB3CC3E5BC9274274DD4137F189F4ACCC67C865505FECDCA8CA5CA`。

| 同一输入/目标 | Misaki + 严格适配器 | 当前生产 CMUdict 链 | 结论 |
| --- | --- | --- | --- |
| I read every day. | read=/red/，错误 | read=/red/，错误 | CMU 收录 /riːd/，但默认取编号 1 |
| They read it yesterday. | /red/，正确 | /red/，正确 | CMU 固定默认碰巧符合本句，不能证明时态选择 |
| I read a book yesterday, but please read it again. | /red/、/riːd/，本例正确 | 首个 /red/；第二个按同读音去重，不注 | 生产链没有识别两次 read 的读音差别 |
| He can lead the way with a lead pipe. | 两次均 /liːd/，铅管错误 | 首个 /led/，带路错误；第二个去重不注 | CMU 候选 /led/、/liːd/ 齐全，但没有语境选择 |
| They will record a new record tomorrow. | 动/名词重音均选对 | 首个动词 `rə.ˈkɔːrd`；第二个名词去重不注 | CMU 收录名词候选，却没有按词性切换 |
| The water is better in the city. | 三个目标均因闪音被适配器跳过 | 三词均能注出，但 IPA 元音细节有问题，见下文 | CMU 保留 /t/，解决音位来源问题；当前转换还需修正 |

这里的“第二个去重不注”是实际区间输出，不能写成“第二个显示错误读音”。底层查询仍对每次出现返回同一默认项；当前 UI 没有利用全部候选来修正它。

归一化后的 CMU 默认/候选为：

- `read`：`ˈred`、`ˈriːd`；`lead`：`ˈled`、`ˈliːd`。
- `record`：`rə.ˈkɔːrd`、`ˈrek.ɜːrd`、`rɪ.ˈkɔːrd`；编号只表示来源顺序，没有词性/词义标签。
- `water`：`ˈwɔː.tɜːr`；`better`：`ˈbet.ɜːr`；`city`：`ˈsɪt.iː`。

**不能将最后三项当成无瑕疵的词典式 IPA 答案。** 现有 `ArpabetIpaConverter` 把 ER（含 ER0）一律映射为 `ɜːr`、IY（含 IY0）一律映射为 `iː`；`IpaTranscription` 也把 `i` 一律加长。非重读卷舌元音通常应写作 /ər/ 或 /ɚ/，city 的词尾通常用 /i/；应固定同一美音词典口径并在生成与读取两侧保持一致，不能只替换一个输出字符串。这里的问题属于本项目转换链，不能归因于 CMUdict 原始 ARPAbet 缺少重音信息。

另须澄清：`ɾ` 本身是合法 IPA；Misaki 的 water/better/city 有发音输出，漏注来自本次严格适配器对词典式音位口径的限制。不能将这一现象表述成“Misaki 不会读常用词”。

### 补充：最小修补的回归实验与可行方向

`.temp/misaki-fix-tests/` 引用未修改的 MisakiSharp 2.2.0，仅在隔离进程内修改词典对象，不改缓存、资源文件或生产代码。实测：

| 试验修补 | 修好的样本 | 新增错误 |
| --- | --- | --- |
| 将 read 的 VBP 项改成 /riːd/ | I read every day. | They read it yesterday. 以及长句首个过去时 read 均变成 /riːd/；原 tagger 都给 VBP |
| 为 lead 加 NN/NOUN=/led/，动词保留 /liːd/ | 带路与铅管的对照句 | She has the lead role. 中 lead 同样被标为 NN，错误选 /led/ |

因此不存在本轮已经验证的“改两条词典就全部解决”的方案。可继续验证的路线为：

1. **音位数据与格式：** 将 CMU 原始 ARPAbet 等保留 /t/、/d/ 和重音区别的资料作为正式候选来源，修正非重读元音与未知符号处理；或从 Misaki 尚未丢失区别的上游阶段重建音位数据。若采用前者，应设计为新 provider 的正式数据链，不追加旧 provider 回退。只有已经发生合并的 `ɾ` 串无法靠统一字符映射恢复 /t/ 或 /d/。
2. **候选与词性：** 给异读建立明确的词性/词义对应，结合 tokenizer/tagger 在完整候选中选择；CMU 变体编号本身不是这种对应关系。record 的名/动词可用此类机制，但仍要质量回归。
3. **时态与词义：** read 需要修正或补充上下文时态判断；lead 需要区别金属义、领先/主演义，不能只看 NN。局部句法/搭配规则可以覆盖明确范围；若要求广泛语境准确性，需要继续穿刺更强的异读消歧模型或分类器，不能预先承诺选型、体积或准确率。
4. **不确定输入：** 必须明确无法唯一判断时的展示政策。例如无上下文的 `I read it.` 本身可指现在或过去；完整候选可以保留，但不能声称引擎能恢复文本未提供的信息。展示/跳过规则尚未裁决，不把它当作已获批的范围调整。

本次两个隔离实验均退出 0，JSON 分别保存在 `.temp/cmu-comparison/result.json`、`.temp/misaki-fix-tests/result.json`。当前工作区可用以下命令复跑；这仍是调查证据，不是已修复的质量门槛或正式金样本：

```powershell
& "$env:DOTNET_ROOT/dotnet.exe" run --project .temp/cmu-comparison/Compare.csproj --no-restore
& "$env:DOTNET_ROOT/dotnet.exe" run --project .temp/misaki-fix-tests/Fixes.csproj --no-restore
```

### 候选与原文处理

- `record` 的两条词性候选可读取，经 IPA 转换、去重后以本句选中项为首。`DEFAULT` 并不总是当前读音。
- `They/will` 的上下文弱化或重音变化，会与原始词典条目组成伪多读；生产须区分真正异读与句重音变化。当前探针仅保证首项和精确去重，**候选语义还未通过**。
- 官方 `Phonemize` 将 `[record](/zzz/)` 解释成读音指令，还会 TrimStart；修改版关闭这条 TTS 预处理。tab/换行词元在 TTS 重组中可能粘到下词，探针补丁保留分隔关系。
- 上游会把 emoji 拆成半代理项，JSON 会将其替换成 U+FFFD；英语输出跳过这些不认领的词元，后续原文切片仍精确。生产区间仍须检查代理项边界。
- 数字和货币仍可参与原引擎分析，但输出过滤掉注音；原文不改写。明确词条 `Dr.`、`NASA` 可展示；样本中未明确收录的 `U.S.`、`ZZQX` 跳过。原文缩写存在并不自动等于数据明确收录。
- `Bob's` 底字沿用现有去所有格策略；词组、屈折候选的来源和默认读音必须在正式 provider 中逐项核对，不能复制词干候选冒充该词形读音。

## 裁剪与原生体积

| 原生文件 | Bytes | MiB |
| --- | ---: | ---: |
| MisakiJaRaw.dll | 19,906,048 | 18.98 |
| MisakiJaPatched.dll | 21,765,632 | 20.76 |
| MisakiEnCoreRaw.dll | 11,839,488 | 11.29 |
| MisakiEnRaw.dll | 23,805,952 | 22.70 |
| MisakiEnPatched.dll | 23,736,320 | 22.64 |

日语裁剪白名单只留 `ja_lexicon / ja_char_unk / ja_matrix_compact`，不需要 `ja_words`；英语核心保留美/英词典、tokenizer 与 tagger，含回退包再保留 `enus/engb_espeak`。本轮没有五语包。

三种裁剪的官方托管副本均成功 NativeAOT，并经独立客户端调用。正式路线仍需最小源码修改，单用 Cecil 不能增加书面字段或解决生命周期。上述体积包含探针 ABI 和诊断功能，不能承诺生产体积或将独立客户端体积当作主 exe 增量。

五个文件的 **PE 普通导入表**只见系统 DLL / UCRT / Windows API 集，不含 VCRUNTIME/MSVCP；详见 summary 的逐包 imports。该检查不是动态加载依赖全覆盖，也没有在无开发工具的干净 Windows 上部署，正式便携验收留给 014。

## ABI、并发与生命周期

探针 ABI 为 `probe_abi / probe_run / probe_release / probe_free`。输入 UTF-8 指针加字节长度，输出 JSON 指针加长度，结果区间使用原文 UTF-16。错误以状态码和消息返回，客户端 finally 调同 DLL 的 free。坏参数和无效 UTF-8 返回非零，异常不跨边界。

每语一把 Lock，所有解析和数据释放共用该锁。修改版日语将不可重置 Lazy 和静态表改为可重建；英语清空实例、tagger Lazy、tokenizer 规则/正则与 espeak 缓存。使用 WeakReference + 显式 GC 确认旧对象不再存活，重建后 tokens 完全一致，六个并发操作包含一次释放也通过。未做持续压力测试或故障注入。

最新独立 AOT 客户端观测：

| 修改包 | 首请求 | 热态 100 请求 | 释放后重建 | GC 后存活堆（前→后） |
| --- | ---: | ---: | ---: | ---: |
| 日语 | 1428.62ms | 11.82ms | 1280.98ms | 179,191,096→68,648 bytes |
| 英语 | 621.95ms | 19.23ms | 594.19ms | 116,981,000→613,296 bytes |

首请求不含客户端绑定 DLL 的时间；是已读过文件后的独立进程观测，不是冷磁盘/SLA。存活堆不等于进程工作集，本轮没有承诺工作集回落。**生产只解除引用，不强制 GC/裁工作集。** 首加载和回收后重建必须进后台，并在真实宿主评估等待、取消和代际回写。

[微软文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)明确 NativeAOT 库不支持卸载；本轮未调用 FreeLibrary。DLL 常驻和数据闲置释放是两件事。包加载后的更新采用暂存、重启生效；本轮未实现下载、文件替换或设置 UI。

## 后续门槛

- **可开始：** 正式日语再生/包构建、宿主生命周期骨架、中文内嵌。
- **英语切换阻断：** 不可逆音素来源、现在时 read、金属义 lead、常用词漏注、句重音伪候选；011 先形成可复跑质量修复，再接 UI。
- **仍待集成：** 正式 ABI 清单/验证与更新状态机、真实 UI、词数/过滤/去重、复制/选区/收藏、生产闲置计时、精简/完整 ZIP、无开发环境离线部署、许可与 ModelScope 发布。
- 主 project spec 和旧再生正文继续描述当前生产事实；没有将本次隔离证据误写为生产完成。
