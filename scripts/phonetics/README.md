# 三语注音语言包构建链

中、日、英注音资源的统一构建入口。日英原生语言包（MisakiSharp 2.2.0 + UniDic 3.1.0）输入全部在
[`misaki-packs.json`](misaki-packs.json) 里固定：上游提交、源文件 SHA-256、补丁哈希、UniDic 原件哈希、
资源白名单与包定义；任何一项对不上就停，不做兜底。

## 组成

| 文件 | 作用 |
| --- | --- |
| `Build-LanguagePacks.ps1` | 统一入口，分 `Prepare / Chinese / Native / Verify / All` 五段 |
| `chinese/` | 独立中文拼音表构建器，不进入桌面应用程序集 |
| `misaki-packs.json` | 固定版本、哈希、白名单、ABI 与导出列表 |
| `patches/misakisharp-2.2.0.patch` | 最小源码补丁（书面假名、原文处理、候选、释放接口） |
| `RegenerateKana.py` | 从同版 `sys.dic` 再生带书面 `kana` 列的日语词表 |
| `native/` | 生产 ABI v1 导出工程（`nk_phonetics_*`）与 Kokoro→IPA 转换 |
| `tools/` | 构建期工具：按白名单导出上游内嵌资源、审计 IPA 转换覆盖 |
| `verify/` | 语言包验收客户端：哈希/导出/ABI/金样本/坏输入/释放重建/并发 |
| `MakePackManifest.py` | 按实测产物写 `pack.json`（字节、SHA-256、PE 导入表、来源版本） |

## 运行

```powershell
./scripts/phonetics/Build-LanguagePacks.ps1 -Stage All
./scripts/phonetics/Build-LanguagePacks.ps1 -Stage Prepare  # 下载/校验/打补丁/再生/裁剪日英资源
./scripts/phonetics/Build-LanguagePacks.ps1 -Stage Chinese  # 构建中文明文拼音表
./scripts/phonetics/Build-LanguagePacks.ps1 -Stage Native   # 编日英原生包并打包三语资源
./scripts/phonetics/Build-LanguagePacks.ps1 -Stage Verify   # 验收日英原生包
```

`All` 会先构建中文明文 `deps/phonetics/zh/zh_pinyin.txt`，再构建日英原生包，最后统一生成 ModelScope 使用的平铺资源。
`Native` 阶段只打包已有中文明文；要单独重建中文数据可运行 `-Stage Chinese`。

前置条件：`DOTNET_ROOT` 指向 .NET 11 SDK、`NUGET_PACKAGES` 已还原 `MisakiSharp 2.2.0`、
Windows x64 NativeAOT C++ 工具链、Python 3.14、`git`（用于打补丁）。
首次 `Prepare` 需要网络：上游源码、UniDic 3.1.0（约 502 MiB）。所有下载、中间产物与日志都在
`.temp/misaki-pack/`；本地数据落在 `deps/phonetics/{ja,en,zh}/`，分发压缩件位于
`deps/phonetics/{ja,en}.zip` 与根目录 `zh_pinyin.txt.gz`。

## 产物

```text
deps/phonetics/ja/  NaniKore.Phonetics.Ja.dll  pack.json  licenses/
deps/phonetics/en/  NaniKore.Phonetics.En.dll  pack.json  licenses/
deps/phonetics/zh/  zh_pinyin.txt（明文 TSV）
deps/phonetics/ja.zip  日语完整包
deps/phonetics/en.zip  英语完整包
deps/phonetics/zh_pinyin.txt.gz  中文拼音数据
```

`pack.json` 记录字节、SHA-256、ABI、架构、导出列表、PE 普通导入表、嵌入资源哈希、
上游与数据版本；`licenses/` 随包附带上游 `LICENSE`/`NOTICE`、UniDic `BSD`/`COPYING`/`dicrc`
与派生说明 `DERIVED.md`。NativeAOT 产物字节不承诺可复现，重复运行只覆写清单与包文件。

## ABI v1

```c
int32_t  nk_phonetics_abi_version(void);
int32_t  nk_phonetics_annotate(const uint8_t* utf8, int32_t byte_length, uint8_t** json, int32_t* json_length);
int32_t  nk_phonetics_release(uint8_t** json, int32_t* json_length);
void     nk_phonetics_free(uint8_t* buffer);
```

- 输入为带字节长度的 UTF-8；输出 JSON 的 `start`/`length` 一律是原文 UTF-16 偏移。
- 状态码：`0` 成功、`1` 参数非法、`2` 输入不是合法 UTF-8、`3` 解析失败（消息在 JSON `error` 字段）。
- 输出缓冲必须用同一 DLL 的 `nk_phonetics_free` 释放；托管异常不穿越边界。
- 解析、释放、重建共用每语一把锁；`nk_phonetics_release` 只解除引用，不强制 GC、不卸载 DLL。
- 输出形状：`{"abi":1,"language":"ja","tokens":[{"surface","start","length","reading","unknown"}]}`；
  英语额外带 `candidates`（首项为本句读音）。
