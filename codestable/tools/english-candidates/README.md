# CMUdict 英语候选验证（Epic 003 / Issue 003）

隔离控制台工具，链接生产解析器、候选索引及 IPA 转写。输入是本地源文件；不联网、不调用词典生成器或下载器、不写生产 SQLite。`ReadOnlyPaths.cs` 仅补齐被链接文件的 OCR 宿主类型。构建产物不随桌面程序发布。

仓库根目录执行：

```powershell
# 首次获取验证输入；已有文件可直接离线复跑，不覆盖生产 deps。
New-Item -ItemType Directory -Force artifacts/english-candidates | Out-Null
Invoke-WebRequest https://raw.githubusercontent.com/cmusphinx/cmudict/master/cmudict.dict -OutFile artifacts/english-candidates/cmudict.dict
dotnet run --project codestable/tools/english-candidates/EnglishCandidates.csproj -- artifacts/english-candidates/cmudict.dict
```

源为 CMUdict 官方仓库 `cmusphinx/cmudict` 的 `cmudict.dict`，许可见该仓库 LICENSE 与本项目 NOTICE。本工具不将源数据提交或再分发。2026-11-14 获取的快照为 3,618,488 字节，SHA-256：

```text
81917843C7F44CE2B094AC63873C2C7A4CF802040792C455BA3CA406891C3D22
```

远端 master 会变化；复现实测时先比对上述指纹，变化时重新记录输入版本，不能沿用旧计数。退出码 `0` 表示全部检查通过，`1` 为断言失败，`2` 为缺少输入；文件或构建异常同样失败。

本次 Debug 实测：15 checks，0 failures；135,166 源行中，沿用多字母词过滤后 135,139 条读音完整进入索引，126,026 个词面，9,113 条编号备选，8,446 个多读音词。每个词面恰有一个编号 1 默认项；转换无空结果、来源 `(word, variant)` 无冲突、源行尾标签未进入 ARPAbet。`lead/read/wind/bass` 各两项，`record` 实为三项，`abandoned` 有直接屈折词条。

例如源默认 `lead` → `ˈled`，备选 → `ˈliːd`；`record` 默认 → `rə.ˈkɔːrd`，另有 `ˈrek.ɜːrd`、`rɪ.ˈkɔːrd`。这些是源顺序，不是词义、词性或口音分类；固定默认无法根据句子选择。fixture 的 record 两项仅用于模型测试，不能代表真实源的完整候选数。

单进程观测解析及建索引约 700.20 ms，随后托管已分配内存约 76.2 MB、工作集约 132.9 MB；包含源行数组/分组和工具统计，未强制 GC，不是生产 SQLite 成本或冷启动阈值。正式 SQLite、屈折回退准确性、候选 UI、分发以及真实宿主 NativeAOT 均未覆盖。
