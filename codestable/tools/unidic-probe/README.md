# UniDic 2.1.2 隔离验证探针

用于 Epic 003 / Issue 002 的可重复验证。只读取显式传入的词典目录，不引用生产注音器、下载器或资源清单，不写入 `deps/`，不属于桌面应用交付物。

## 输入与运行

在仓库根目录使用 .NET 11 SDK。测试资源来自官方原版二进制：

<https://unidic.ninjal.ac.jp/unidic_archive/cwj/2.1.2/unidic-mecab-2.1.2_bin.zip>

本地默认位置为 `resources/unidic-lite@unidic-mecab-2.1.2_bin/`，也可传入其他解压位置。词典不入库，不自动下载；IPAdic 对照使用已有 `deps/phonetics/ja/`。探针要求 UniDic 目录含 `sys.dic`、`unk.dic`、`matrix.bin`、`char.bin`、`dicrc`，不会因此认定这就是完整再分发包；版权材料也必须保留。

```powershell
. ./scripts/toolchain.ps1
$probeDotnet = Get-NaniKoreDotnetExecutable
& $probeDotnet run --project codestable/tools/unidic-probe/P0UniDic.csproj -- --dict resources/unidic-lite@unidic-mecab-2.1.2_bin --ipa-dict deps/phonetics/ja --json
```

独立 NativeAOT（Windows x64，需本机 C++ 链接工具链）：

```powershell
& $probeDotnet publish codestable/tools/unidic-probe/P0UniDic.csproj -c Release -r win-x64 -p:PublishAot=true --self-contained true -o artifacts/unidic-probe-aot
./artifacts/unidic-probe-aot/P0UniDic.exe --dict resources/unidic-lite@unidic-mecab-2.1.2_bin --ipa-dict deps/phonetics/ja --json
```

这是独立工具的命令，不构建桌面宿主、不递增产品版本、不运行全量自测。桌面应用构建/测试仍使用 `scripts/dev.ps1`。

## 结果如何读

- 不传 `--text`：运行固定样例、4 个送假名金样本及 7 个字段/边界检查；传 `--ipa-dict` 时再运行 9 个双词典策略检查。
- `--text <文本>`：只观察该文本；不执行固定金样本和字段检查，仍可配合 `--ipa-dict` 独立运行固定策略检查。
- 不传 `--json` 则输出可读文本。退出码 `0` 表示本次执行的断言符合预期，`1` 表示加载/断言失败，`2` 表示参数错误。未执行的检查集合为空，不代表通过了完整验证。
- `known-gap/partial-token-ruby-base` 和 `segmentation-threshold-drift` **断言已知迁移差异仍可复现**，不是验收这些行为可发布。完整判断见 [Issue 002](../../epics/003-o-本地三语注音准确性与结构化交互升级/issues/002-x-P0-UniDic字段与运行时兼容性验证.md)。
- 原始 `Feature` 按带引号的 CSV 解析，`*` 和缺列视为缺值。UniDic 模拟适配取 `kana=f[17]`，缺失再取 `pron=f[9]`，转平假名并切送假名。IPAdic 仅为对照，读取其 `f[7]/f[8]`；这不构成生产双引擎兼容或自动回退。
- 直接链接生产过滤/去重、Ruby 解析及内嵌判据。节点转注音仍是隔离模拟，未运行生产词典哈希检查、惰性加载管理或真实 UI。
- NativeAOT 固定 `InvariantGlobalization=false`。历史 invariant 构建在该 UTF-8 词典上加载失败；此探针成功不等于生产宿主已完成相应修改。
- 性能是单次加载、再次 Parse、释放后重载和进程工作集观测；不是冷磁盘基准、性能阈值或产品总内存承诺。

## 许可与分发边界

原包 `COPYING` 允许 GPL、LGPL、BSD 三选一；采用 BSD 时应保留 UniDic Consortium 2011–2013 版权、BSD 全文与免责声明，不借作者名义背书。未来完整包及按需下载均须随附对应声明；本工具只引用本地数据，不修改生产 `NOTICE` 或下载清单。字段、性能、文件指纹及生产切换阻碍已回写所属 Epic 与关闭 Issue。
