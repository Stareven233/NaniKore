# 注音数据维护

本文只记录当前注音数据的来源、生成、校验和分发约束。运行行为以 `src/NaniKore/Phonetics/` 为准。

## 数据与来源

| 数据 | 形式 | 来源 |
| --- | --- | --- |
| 日语、英语 | 分语言 Native AOT 包（DLL、`pack.json`、许可文件） | MisakiSharp；日语书面假名由 UniDic 再生 |
| 中文 | 本地明文 TSV，远端 gzip | pypinyin、CC-CEDICT、Unihan `kMandarin` |

再生输入与固定版本记录在 `scripts/phonetics/`。许可归属以仓库根目录 `NOTICE` 为准。判据和停用数据是独立的内嵌资源，不属于外置语言包。

## 生成与运行约束

- 中、日、英资源统一通过 `scripts/phonetics/Build-LanguagePacks.ps1` 构建。日英上游版本、补丁、资源白名单和输入哈希由 `misaki-packs.json` 管理；升级时一并复核，不手工改生成包。
- 日语界面需要书面假名，不能直接使用 Misaki 的发音字段。`RegenerateKana.py` 从同版 UniDic 的 `sys.dic` 再生 `kana` 字段；字段定义以该版本 `dicrc` 为准。
- 中文表由 `scripts/phonetics/chinese/` 下的独立构建器生成。输入放在 `resources/phonetics/`，输出为 `deps/phonetics/zh/zh_pinyin.txt`；统一构建脚本再压缩为远端平铺文件 `zh_pinyin.txt.gz`。
- 日英包的 `pack.json` 和宿主清单共同校验包内容、ABI、架构与许可文件。改动包内容时必须同步更新相应清单；Native AOT 包不保证字节可复现，以生成清单记录的哈希为准。
- 中文数据与日英包均按需下载。数据缺失或哈希不符时，注音不可用并降级为纯文本，不以内置旧数据回退。

## 哈希与分发

修改分发文件后，先生成哈希并回填源码清单，再预检和同步：

```powershell
python scripts/sync-deps.py phonetics --print-hashes
# 将中文表哈希和日英 ZIP 哈希分别回填到对应清单
python scripts/sync-deps.py phonetics --dry-run
python scripts/sync-deps.py phonetics
```

脚本绑定 `ChinesePinyinData.cs` 与 `NativePhoneticManifest.cs` 并在上传前自动核对，只发布 `ja.zip`、`en.zip`、`zh_pinyin.txt.gz`。仓库目标、认证和其他子目录用法见 `scripts/sync-deps.py` 文件头。

## 验证

改动生成逻辑、包内容或宿主行为后，运行 `./scripts/dev.ps1 test`。需要单独检查包时使用 `./scripts/phonetics/verify`；需要查看本地数据定位和标注器状态时使用 `--phonetics-probe`。
