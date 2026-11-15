# Misaki 界面注音与原生包验证

仅为穿刺，不属于桌面生产工程。使用固定 MisakiSharp 2.2.0 / 提交 `beb91a9f06ebe5e25e595b5a3c990ee616ad75e5`、Mono.Cecil 0.11.6、UniDic 3.1.0。结论见 [穿刺记录](../../notes/misaki-annotation-spike.md)。

## 复跑

从仓库根目录执行，要求 `DOTNET_ROOT`、`NUGET_PACKAGES`、Windows x64 NativeAOT C++ 工具链与 Python。MisakiSharp 2.2.0 应已还原到共享包缓存；首次 Prepare 下载源码、Cecil 和约 502 MiB 的 UniDic 压缩包，需要网络。所有下载、展开、编译、日志、报告均位于 `.temp/misaki-annotation-spike/`，不改 NuGet 原件与生产词典。

```powershell
./codestable/tools/misaki-annotation-probe/Invoke-Probe.ps1 -Stage All

# 可按依赖逐段复跑；Prepare 完成后才能 Native，Native 完成后才能 Run。
./codestable/tools/misaki-annotation-probe/Invoke-Probe.ps1 -Stage Prepare
./codestable/tools/misaki-annotation-probe/Invoke-Probe.ps1 -Stage Native
./codestable/tools/misaki-annotation-probe/Invoke-Probe.ps1 -Stage Run
```

该脚本是隔离探针专用入口；桌面生产构建/自测仍使用 `./scripts/dev.ps1 build/test`。探针直接调用 SDK，防止常规构建递增产品版本或把试验依赖接入生产。

## 检查范围

- `Inspect.cs`：从 NuGet 原件导出资源；Cecil 只在 `.temp` 中改副本，生成日语、英语核心、英语含回退三组。扫描 en-US 所有读音的字符集与严格转换覆盖。
- `prepare.py`：核对 UniDic 3.1.0 `dicrc` 的 `kana=20`；逐条验证原版 Misaki 词条与同版 `sys.dic` 身份后追加书面假名。对固定提交打最小源码补丁，加入生命周期接口、候选访问和原文处理。
- `Native.csproj`：生成五种原生 DLL；前三种引用裁剪的官方程序集，后两种编译最小修改源码及白名单资源。DLL 为非 invariant。
- `NativeExports.cs`：版本/字节长度/错误码/释放 ABI，输出原文 UTF-16 偏移。语言锁保护解析与数据释放。所有结果由同 DLL 释放，不卸载 NativeAOT DLL。
- `Client.cs`：Debug 客户端和 AOT 客户端逐包运行；再验证日英同进程、invariant AOT 宿主。检查原文切片、候选首项、数字货币跳过、收录缩写、坏参数/UTF-8、旧对象回收、重建一致与并发释放。
- `summarize.py`：生成 `summary.json`，记录原生文件字节、SHA-256、PE 普通导入表、每组检查数和冷/热/重建耗时。

`ProbeIpa.cs` 只是 en-US 严格转换试验，**不属于生产转换器**。闪音/喉塞音拒绝转换，避免猜回 /t/ 或 /d/。语料中 `read`、`lead` 反例及常用词漏注记录在 JSON；矩阵通过只说明约定行为和架构成立，不代表英语质量门槛通过。

释放测量调用显式 GC 并使用 WeakReference 检查旧实例，是可回收性实验；生产不得据此加入强制 GC 或工作集裁剪。冷加载在独立进程测量首次请求，不包含系统 DLL 文件加载时间；没有冷磁盘或真机 UI SLA。

## 报告

`baseline.json`、`trim-report.json`、`kana-report.json`、`source-manifest.json`、`Misaki*-jit/aot.json`、`combined-aot*.json`、`summary.json`。任一构建或约定断言失败返回非零。只有最终 Run 成功的报告用于验收；早期试验产物不能混作当前证据。

源码修改由脚本在临时目录生成，不把上游代码复制进生产仓库。原件及许可位于 `.temp/.../upstream/` 和 `.temp/.../unidic/`；正式分发还须完成 NOTICE、许可证原文与派生数据归属，探针不执行 ModelScope 上传。
