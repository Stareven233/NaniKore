---
kind: issue
type: feature
title: "原生 ABI、下载与资源生命周期接入"
status: open
created: 2026-11-15
---

# 009 原生 ABI、下载与资源生命周期接入

依赖 008 的可验证日语包与清单；英语 IPA 适配和整词候选由 011 完成，不阻止本项骨架工作。输入为现有后台注音/IdleResource/下载链及 [穿刺 ABI](../../../tools/misaki-annotation-probe/README.md)，产物为真实宿主的受控加载、生命周期和设置状态。

完成条件：

- [x] 固化 ABI 版本、长度单位、错误码和同 DLL 内存释放；加载前校验受控绝对路径、哈希、架构与清单，绑定后在引擎初始化前检查 ABI 版本和必需导出，禁止同名 DLL 搜索。
- [x] 按语种惰性加载，解析/释放/重建共用锁；首次加载、词数与注音在后台，取消和旧代请求不得回写 UI。
- [x] 将数据释放接到现有闲置策略，解除词表、模型、tokenizer 和回退缓存引用；不卸载 NativeAOT DLL、不强制 GC/裁工作集。
- [x] 明确失败重试与关闭顺序，覆盖并发首建、解析中回收、重复释放、重建失败后重试和取消等待。
- [x] 设置页内置中文就绪；日英有缺失、下载中、进度、失败、就绪与重启生效状态。首次安装未加载包可即时使用；已加载包的新版本暂存并重启替换，不覆盖正在使用的 DLL。
- [x] 缺包/哈希错/ABI 错/缺导出时保留纯文本并显示原因，翻译主链路可用；不回退旧注音引擎。
- [x] 在真实 Debug/AOT 宿主验证闲置后重建、离线与并发，测量 exe 增量。先保持主程序现有 invariant 配置，用集成结果决定是否调整。

验证：`./scripts/dev.ps1 test`（Debug 宿主 1911 项断言 0 失败 0 跳过）、`./scripts/dev.ps1 build -Release`（NativeAOT 通过，发布产物 `artifacts/win-x64/NaniKore.exe` 同命令 `--self-test` 跑出同样的 1911 项通过）。临时报告：`.temp/009-native-host/report.md`。

## 实施记录

代码在 `src/NaniKore/Phonetics/Native/`：

| 文件 | 职责 |
| --- | --- |
| `NativePhoneticManifest.cs` | 包身份清单（库字节/SHA-256/ABI/架构/导出/许可哈希）、`deps/phonetics/misaki/<lang>/` 与 `.staging-<lang>/` 定位、ModelScope 直链 |
| `NativePhoneticLibrary.cs` | ABI v1 客户端：包校验（哈希/字节/PE win-x64/`pack.json` 双向核对）→ 绝对路径 `NativeLibrary.TryLoad` → 导出与 ABI 比对 → UTF-8 进/JSON 出 → 同 DLL `nk_phonetics_free`；进程内模块缓存，永不卸载 |
| `NativeTextAnnotator.cs` | 单实例标注器：惰性加载、失败原因落地、词元→区间（原文切片校验）、日语只认含汉字词面、英语只认 ≥2 拉丁字母 |
| `NativePhoneticPackHost.cs` | 每语槽位 + 状态机（缺失/需更新/下载中/失败/就绪/重启生效）、闲置策略、暂存包提升、应用级单例 |
| `NativePhoneticPackInstaller.cs` | 下载安装：许可→库文件→`pack.json` 顺序落暂存目录，逐文件 SHA-256 + 清单校验，未加载即时替换、已加载只暂存 |

关键约定：

- 校验顺序固定为「文件齐 → 字节数 → SHA-256 → `pack.json` 字段 → PE machine=0x8664 → dlopen → 四个导出 → ABI 版本」，任何一步失败都只让该语言退化为纯文本，并在设置页该语言行内写明原因。
- 加载只使用 `Path.GetFullPath` 后的绝对路径；模块句柄按路径缓存，释放只调用 `nk_phonetics_release`（解除词表/模型/tokenizer 引用），不 `FreeLibrary`、不强制 GC、不裁工作集。
- 槽位复用现有 `IdleResource<T>`：注音进行中（`Use` 持有锁）时闲置回收用 `TryEnter` 直接跳过，取消/失败结束会按最新策略重新排期。
- 新版本一律先落 `.staging-<lang>`；`IsLanguageLoaded(lang)` 为真时只保留暂存目录并置 `RestartPending`，`ApplyStagedPacks()` 在下次启动把它们提升为正式包（校验不过整份丢弃）。
- 「已是最新」不重复下载：正式包校验通过且没有暂存目录时直接返回。

设置页新增「注音语言包（本地原生）」分组（`NativePackJa` / `NativePackEn` / `NativePackStatus`），按钮文案与状态一一对应（下载/更新/下载中…/重试/已就绪/重启生效），失败原因钉在对应语言行内。

**两套下载入口并存是过渡态，不是最终形态**：旧 `ja`/`en` 词典卡片仍服务当前生产 provider（IPAdic / CMUdict），现在删掉会让主人无法下载程序正在使用的词典；定下的收口点是 010/011 切换 provider 之后、014 清理时把旧卡片下线（旧外置词典文件归档到 `.temp`），每个语言只留一条语言包入口；中文卡片在 012 改内嵌后变成只读的「内置就绪」。详见 epic `spec.md` 运行与分发契约与 014 完成条件第 7 条。

## 实测

| 项目 | 结果 |
| --- | --- |
| Debug `NaniKore.dll` | 1,788,928 → 1,850,880 B（+61,952） |
| Release `NaniKore.exe` | 23,062,528 → 23,261,184 B（+198,656，+0.86%） |
| 日语冷加载（首调用） | Debug 1339–1568 ms / AOT 1403 ms |
| 热态 20 次 | Debug 0.5–0.6 ms / AOT 0.4 ms |
| 释放后重建 | Debug 1339–1463 ms / AOT 1355 ms |

工作集与托管堆随整个自测进程变化（Debug 366–403 MB、AOT 352 MB；托管堆 44–82 MB），只作观测，不作为阈值。

## 未覆盖与交接

- 真实网络下载、断网重试与 ModelScope 上传未验证（本次未上传，014 处理）；`scripts/sync-deps.py` / `dev.ps1 publish -full` 的依赖清单仍是旧路径（014）。
- ABI 版本不符分支缺可复现夹具（需要一份自报错误 ABI 的 DLL），目前只有代码路径与文案。
- 真实 UI 点击、重启替换的人工演练、复制/选区/收藏回归留给 010/011（provider 切换）与 014（分发验收）。
- 生产 provider 未切换：日语仍 IPAdic、英语仍 CMUdict，结果面板注音仍走旧标注器；本项只交付可加载、可释放、可重建、可观测的骨架，`NativePhoneticPackHost.Annotators` 已按 `ITextAnnotator` + 闲置策略就绪，010/011 可直接装配。
- **交接提醒**：011 用 008 构建链重建最终英语包后，必须把 `NativePhoneticManifest` 里 `en` 的字节数/SHA-256（以及变更过的许可哈希）同步回填，否则宿主会按新口径判为「需更新」而拒绝装载；日语包重建同理。
- 主程序 invariant 配置保持原样：非 invariant 的包在真实 AOT 宿主（`InvariantGlobalization=true`）里加载、注音、释放、重建、并发全部通过，无需调整。
