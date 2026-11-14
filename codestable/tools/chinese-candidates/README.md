# 中文候选隔离对照

本工具服务 Epic 003 / Issue 001，链接生产中文标注器、过滤契约、格式化与 Ruby 字符判定，以及原基线 `Samples.cs`，不引用桌面工程。路径适配仅把生产标注器指向指定只读词典，不修改生产、基线或上游算法。

## 已运行证据：PRE-P1 快照

以下三次运行均为 **PRE-P1 历史快照**。生产代码随后已变化，不能用这些计数描述当前生产状态。

| 运行 | 完整报告（相对仓库根目录） | 结果 |
| --- | --- | --- |
| Debug，LF 资源 | `artifacts/chinese-candidates/20261114-093151-367-debug/comparison/report.txt` | 上游按 `Environment.NewLine` 分割，Windows 下 LF 数据未正确装载；不能作为候选准确性证据 |
| Debug，CRLF 资源 | `artifacts/chinese-candidates/20261114-093329-603-debug/comparison/report.txt` | 已完成，约 1800 行；`PASS=609 FAIL=13 UNCOVERED=15` |
| NativeAOT，CRLF 资源 | `artifacts/chinese-candidates/20261114-093345-548-aot/comparison/report.txt` | 已完成，约 1800 行；`PASS=609 FAIL=13 UNCOVERED=15` |

两份 CRLF 报告的固定集都是 **12 条样本、13 个目标区间：PRE-P1 生产 10/13，候选 12/13**。三个改善是“他还在这里”的还（`huán → hái`）、“罅缝”（`xià féng → xià fèng`）、扩展字后的“龃龉”（生产无法投影，候选 `jǔ yǔ`）。一个退步是“周长大于”中的长大：生产 `cháng dà` 正确，候选误选 `zhǎng dà`，即使候选集合包含正确读音。计入 sanity 的一个目标后，`TARGETS total=14 production-pass=11 candidate-pass=13 improved=3 regressed=1`。

`SUMMARY` 包含输入保真和既有组合输出检查，15 个 `UNCOVERED` 是未核对整句读音的样本数，不能当作整句准确率。报告已有实际编译输入、资源和产物的 SHA-256/大小：Debug 的 `EXECUTABLE` 是 dotnet 宿主，`ASSEMBLY` 才是工具 DLL；AOT 的 `EXECUTABLE` 是原生产物。使用报告内的哈希识别快照，不只看文件名或候选 commit。

**采用判断：固定目标净增 2 个，但存在语义退步、原生 string 入口丢字符和跨边界匹配，证据不足以直接采用。**

## 复现与运行

在仓库根目录的 PowerShell 中准备固定克隆。克隆配置与数据规范化是显式准备步骤，运行脚本不会修改上游输入：

```powershell
git clone -c core.autocrlf=true https://github.com/wolfgitpr/csharp-pinyin.git artifacts/chinese-candidates/csharp-pinyin
$clone = Join-Path $PWD 'artifacts/chinese-candidates/csharp-pinyin'
git -C $clone config core.autocrlf true
git -C $clone checkout --detach 217de636b0223bb4c60b7f0b9d107d288303fed5

# 仅将普通话数据行尾确定性转换为CRLF；保留其余字节、BOM及末尾换行有无，不改源码/算法。
Get-ChildItem -LiteralPath (Join-Path $clone 'Pinyin/dict/mandarin') -Filter '*.txt' -File | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $text = [Text.Encoding]::Latin1.GetString($bytes)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
    [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::Latin1.GetBytes($normalized))
}
git -C $clone diff --exit-code 217de636b0223bb4c60b7f0b9d107d288303fed5 -- Pinyin LICENSE
if ($LASTEXITCODE -ne 0) { throw '候选工作树必须保持Git规范化后无差异。' }

./codestable/tools/chinese-candidates/run.ps1
./codestable/tools/chinese-candidates/run.ps1 -NativeAot
# 可选：指纹与构建时默认资源一致的只读目录
./codestable/tools/chinese-candidates/run.ps1 -DictionaryDirectory 'F:/resources/zh'
```

已有克隆从 `$clone = ...` 开始。`core.autocrlf=true` 允许工作树 CRLF 与提交 LF 规范化后保持 `git diff --exit-code` 干净；报告哈希仍记录实际 CRLF 字节。不要将行尾准备扩展为改词条或改上游逻辑。

脚本复用 `scripts/toolchain.ps1`，从 `DOTNET_ROOT` 等位置寻找 SDK，设置共享 NuGet 缓存后直接构建隔离工程。`scripts/dev.ps1` 固定操作桌面工程，build 还会递增生产配置版本，因此此探针不使用它。

每次创建 `artifacts/chinese-candidates/<UTC>-debug|aot/`，包含 `build.log`、`run.log`、`status.txt`、`comparison/report.txt`，AOT 另有隔离的 `publish/`。独立二进制参数是 `<仓库根目录> <尚不存在的报告目录> [中文词典目录]`，拒绝覆盖报告。退出 `0` 表示全部已设检查通过，`1` 表示明确检查失败，`2` 表示缺依赖、输入漂移或运行异常；上述有效 PRE-P1 对照含已知失败，不是全绿验收。

初始化现在记录 `Environment.NewLine` 与每个候选数据资源的 CRLF/单独 LF/单独 CR 数量；Windows 遇到 LF 数据立即失败，再用 List 入口“银行 → yín háng”哨兵确认词典可用，失败退出 `2`，不把空词典计成准确性。Debug DLL 指纹使用隔离工程输出目录的固定程序集路径；AOT 只记录原生产物，不读取 Assembly.Location。

构建会冻结实际编译/嵌入输入、工程、运行脚本和五个生产中文资源的 SHA-256，运行逐项重算，并检查候选 HEAD、相对固定提交的工作树差异和未跟踪输入。任何漂移均非零退出；旧二进制不能配新生产源码复用。**重新构建并运行会记录新的当前生产快照，不会复现 PRE-P1 生产代码。** 锁和报告含本机绝对路径，不是便携分发包。

P1 后已由父任务重跑 Debug（`artifacts/chinese-candidates/20261114-100412-040-debug/comparison/report.txt`）与 AOT（`artifacts/chinese-candidates/20261114-100425-675-aot/comparison/report.txt`）：两者固定目标均为生产 **11/13**、候选 **12/13**，`610 PASS / 12 FAIL / 15 UNCOVERED`，退出 1。生产“龃龉”恢复，仍有“还”和“罅缝”两项默认音失败；候选“周长大于”仍退步。该工具刻意独立检查有损原生字符串入口，因此总计数不会随生产修复变为全绿。子任务执行权限限制已由父任务实际运行解除，不再是当前阻塞。去掉 Assembly.Location 后再次 AOT 构建/运行成功（预期退出 1），报告 `artifacts/chinese-candidates/20261114-100955-779-aot/comparison/report.txt`，构建日志无 IL3000；固定目标计数不变。

## 比较口径与边界

固定目标直接使用 `Samples.cs` 的期望，不从运行结果生成。俳佪、龃龉沿用 selftest；不行/银行采用词典本调；还按“仍然/归还”义区分；长大按“成长/周长大于”义区分；銀行/银行保留原文繁简；罅缝沿用名词义 `xià fèng`。外部读音与义项来源已按直接词条/字义推断分级记录在 [expectations.md](expectations.md)；“罅缝”未取得专词条证据。𠮷/U+20BB7、𠀀/U+20000 的规范读音及长文整句读音未核对。

生产保留原 rare/filtered/all 策略与首现去重；候选是未过滤、未去重的逐字输出，不把总输出条数当准确率。生产投影仅接受 BMP 长度与音节数相等的词条；不可映射目标记失败/未知，同时保留 spans、MeCab surface/feature。RAW start 由原 surface 按顺序查找，找不到记 -1，不声称是解析器原生偏移。

List 输入按 Unicode 标量拆分并保存 UTF-16 start/length，有效代理对长 2，孤立代理项保持原码元；逐项检查返回 hanzi 和 token 数量，记录 error、默认音及候选集合。报告另列输入 UTF-16 长度、token 数与边界说明。这只是输入形状适配，不补选音或阻断上游匹配。

已实测的两个边界问题：

- 原生 string 入口丢失标点、空白/换行、扩展字及孤立代理项；报告独立检查重建原文，不能用 List 的保真结果掩盖它。
- `ZhPosition` 抽取词典认识的汉字后在压缩列表匹配短语。`sanity-boundary` 中 `银A行`、`银\r\n行` 的行仍被选成 `háng`，说明英文或换行没有阻断“银行”匹配；List 保真不等于选音边界正确。

## 许可与性能含义

外部候选固定为 `wolfgitpr/csharp-pinyin` 的上述提交，仅链接 `Pinyin/*.cs` 和普通话 `dict/mandarin/*.txt`，资源名为 `Pinyin.dict.mandarin.*`，不嵌入粤语。根 `LICENSE` 是 Apache-2.0；普通话 `License.txt` 声明 MDBG CC-CEDICT、CC BY-SA 4.0，源日期 2023-04-16、121646 entries。未来采用/再分发仍需代码许可、数据许可、归属及派生说明。生产 CC-CEDICT/Unihan 生成物只读并记录指纹。

报告包含进程首次构造、30 批 warm 的 min/上中位数/p95/max、生产 Dispose+Reset 重载、候选重新构造及内存快照。首次构造前已做文件哈希，可能预热磁盘缓存；两引擎同进程依次加载，内存是累计值。候选无 Dispose，旧实例回收未核验；这些数据不能用来断言独立峰值内存或资源已释放。真实宿主 UI、闲置计时器、复制/选区/收藏和分发下载未覆盖。
