# NaniKore 运行时依赖

[NaniKore](https://github.com/Stareven233/NaniKore) 的本地 OCR 和 ONNX Runtime 运行时文件。`deps/` 是仓库内依赖的唯一真实源，目录结构与 ModelScope 仓库 `Noetuu/nanikore-deps` 保持一致。

## 布局

```text
deps/
├─ ocr-onnx/
│  └─ pp-ocr-v6-small/
│     ├─ det/
│     │  ├─ inference.onnx
│     │  └─ inference.yml
│     └─ rec/
│        ├─ inference.onnx
│        └─ inference.yml
├─ onnxruntime/
│  ├─ onnxruntime.dll
│  └─ onnxruntime_providers_shared.dll
└─ phonetics/
   ├─ misaki/{ja,en}/   # 日英原生注音语言包（DLL + pack.json + licenses/）
   └─ zh/               # 中文拼音数据（zh_pinyin.txt.gz）
```

`inference.json` 和模型目录中的说明文件是模型仓库附带的资料，C# OCR 运行时不读取它们。`.ms_upload_cache` 是 ModelScope 上传工具缓存，不属于运行时依赖。

## 下载

应用设置页的“下载依赖”按钮会在用户确认后，从 ModelScope `master` 分支下载 OCR 权重和 ONNX Runtime DLL，并保存到当前可执行程序所在目录的 `deps/`。OCR 权重下载失败时，应用会逐个尝试原 Hugging Face 地址；ONNX Runtime DLL 使用 ModelScope 直链。注音数据（日英语言包与中文拼音表）在设置页各有自己的入口，同样走 ModelScope 直链。

应用不会在后台静默下载，下载文件会在替换前进行 SHA-256 校验，下载失败或校验失败不会留下可用性不明的文件。

## 注音数据（phonetics/）

本地注音引擎使用的数据文件。目录结构与 ModelScope 仓库 Noetuu/nanikore-deps 的 phonetics/ 严格镜像。三语注音数据都**不随程序集分发**，在设置页按需下载；哈希即版本（不符则提示更新）。判据词表（停用/高频/常用字符表）仍是 KB 级内嵌策展资源，与数据哈希解耦。

```text
deps/phonetics/
├─ misaki/ja/   NaniKore.Phonetics.Ja.dll + pack.json + licenses/（约 20.8 MB）
├─ misaki/en/   NaniKore.Phonetics.En.dll + pack.json + licenses/（约 8.5 MB）
└─ zh/          zh_pinyin.txt.gz（pypinyin + CC-CEDICT + Unihan 合并表，约 1.5 MB）
```

`misaki/` 由 `scripts/phonetics/Build-MisakiPacks.ps1` 生成，每包的 `pack.json` 记录字节、SHA-256、ABI 版本、
win-x64 架构、导出列表、PE 普通导入表与资源哈希；`licenses/` 附上游许可、UniDic 许可与派生说明。
构建与实测见 `codestable/notes/phonetics-packs.md`。

| 语言 | 数据形态 | 宿主侧清单 |
| --- | --- | --- |
| ja / en | 原生 AOT 语言包（DLL + 随包许可） | `src/NaniKore/Phonetics/Native/NativePhoneticManifest.cs` |
| zh | 单份合并拼音表（TSV + gzip） | `src/NaniKore/Phonetics/ChinesePinyinData.cs` |

旧的 IPAdic 编译态词典、`ipa_dict.db` 与旧 MeCab 中文产物已随旧 provider 下线，不再由应用读取或下载（issue 014 归档）。

| 语言 | 数据来源 | 许可 |
| --- | --- | --- |
| ja | MisakiSharp + UniDic 3.1.0（书面 kana 由同版 `sys.dic` 再生） | Apache-2.0 / BSD-3-Clause（见 NOTICE 3.2） |
| en | MisakiSharp 2.2.0（选音 + 词典候选） | Apache-2.0（见 NOTICE 3.2） |
| zh | pypinyin + CC-CEDICT + Unihan kMandarin 合并 | MIT / CC BY-SA 4.0 / Unicode License v3（见 NOTICE 3.1） |

再生管线见 `codestable/notes/phonetics-pipeline.md`；许可归属见仓库根 `NOTICE`。

## 同步到 ModelScope

`deps/` 与 ModelScope 仓库 `Noetuu/nanikore-deps` 严格镜像（`master` 分支）：OCR 权重、ONNX Runtime DLL 与注音数据分别位于仓库根的 `ocr-onnx/`、`onnxruntime/`、`phonetics/` 子树。上传用 `scripts/sync-deps.py`——一个子目录对应仓库里一个同名前缀，只写自己这份，不影响仓库已有的其他子目录；`phonetics/` 另有白名单，只传正式产物（`misaki/*` 与 `zh/zh_pinyin.txt.gz`），目录里的其他东西不会被推上去：

```powershell
python -m pip install modelscope                        # 一次性：安装 Python 上传 SDK
$env:MODELSCOPE_API_TOKEN = "<token>"                    # 令牌在 https://modelscope.cn/my/myaccesstoken 申请
python scripts/sync-deps.py                             # 同步 deps/ 下所有子目录（各自独立提交）
python scripts/sync-deps.py phonetics                  # 只同步 deps/phonetics/ → phonetics/
python scripts/sync-deps.py ocr-onnx onnxruntime       # 一次同步多个子目录
python scripts/sync-deps.py phonetics --dry-run        # 预检：列文件 + 核对，不联网
```

上传走 SDK 的 `HubApi.upload_folder` 并指定 `path_in_repo=<子目录名>`，大文件自动走 LFS；每条子目录一个提交。令牌只从环境变量或 `--token` 读取，不落盘、不进 git；`.ms_upload_cache`、`_source`、`_build` 等上传缓存与中间产物会被脚本忽略，并已被根 .gitignore 覆盖。

### 改了带哈希契约的子目录（完整闭环）

三个现有子目录都绑定了哈希契约（phonetics → `ChinesePinyinData.cs` + `Native/NativePhoneticManifest.cs`；ocr-onnx / onnxruntime → `OcrDependencies.cs`），上传前会自动核对本地文件与源码清单哈希，不符即中止：

1. `python scripts/sync-deps.py phonetics --print-hashes` —— 算出白名单内全部文件 SHA-256，按各清单的路径口径生成可直接粘贴的行（`--build-zh-data` 结束时也会打印拼音表的一份）；
2. 把新哈希回填进对应的 C# 源码清单；
3. `python scripts/sync-deps.py phonetics` —— 核对通过后上传（忘了回填会在报错里看到「实际」值，抄回去重跑即可）。

没有契约的新目录用 `--no-verify` 跳过核对；要改用别的清单用 `--verify <文件>`（C# 源码定义或纯文本 `路径 哈希` 均可）。运行期行为（按语言下载、哈希校验、软降级）见 `src/NaniKore/Phonetics/ChinesePinyinData.cs` 与 `src/NaniKore/Phonetics/Native/`。
