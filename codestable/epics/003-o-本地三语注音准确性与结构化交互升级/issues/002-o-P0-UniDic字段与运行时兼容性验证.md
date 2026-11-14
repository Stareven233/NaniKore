---
kind: issue
title: "P0：UniDic 字段与运行时兼容性验证"
type: feature
status: open
created: 2026-11-14
---

# P0：UniDic 字段与运行时兼容性验证

> **读者：** 负责判断本地 UniDic 2.1.2 能否替换当前日语 IPAdic 的开发者。
> **归属：** 本文件所在路径即归属 `Epic 003`。

## 做成以后是什么样

使用 `resources/unidic-lite@unidic-mecab-2.1.2_bin/` 的原版二进制做隔离验证，明确 LibNMeCab/NMeCab 对 UniDic 格式的打开结果、Feature 列、`pron`/`kana`/`lemma`/`pronBase`/重音型字段可读性、分词粒度和送假名回归差异，并记录 Debug 与 NativeAOT 的加载结果。验证期间不覆盖生产 IPAdic，不改 ModelScope 清单。

**范围：** 通用 MeCab 打开探针、UniDic 字段解析验证、现有日语金样本对照、资源/许可/运行时记录。
**不包含：** 日语异读 UI、生产切换、旧 IPAdic 兼容分支或自动回退链。

## 当前证据

`JapaneseFuriganaAnnotator` 使用 `MeCabIpaDicTagger` 和 IPAdic 专用属性，生产依赖清单只检查 `sys.dic`、`unk.dic`、`matrix.bin`、`char.bin`。UniDic 的 `dicrc` 明确列出 26 列字段，并要求区分 `pron`、`kana`、`lemma`、`aType` 等，不能只替换文件。

## 方案与验证

- 增加开发期隔离入口或参数，允许指向明确的 UniDic 目录；默认路径仍不影响生产。
- 用 `食べた`、`買い出し`、`行きます`、`躊躇した`、活用、复合词、专名和未登录词对照 IPAdic 与 UniDic。
- 输出每个节点的 surface、Feature 列数和关键字段；验证书面振假名应使用的字段，不能把音便或长音表达未经检验当作原假名。
- 记录打开方式、首次分词、稳态注音、闲置释放/重新加载和内存；用项目约定命令验证 Debug 与 NativeAOT，失败时记录真实错误。
- 核对目录内版本、原始 README、许可随附材料和未来分发所需文件；验证未通过则只记录原因，不改生产下载产物。

## 质量承诺

- **功能适宜性：** 每个关键字段都要有实际读取证据，不能因有字段名就宣称整句重音分析可用。
- **兼容性：** Debug 与 NativeAOT 必须分别记录；词典格式、LibNMeCab 入口和运行期字段契约要明确。
- **性能效率：** 区分展开磁盘大小、加载内存、首次/稳态耗时和回收重载成本。
- **可维护性：** 结论必须指出后续生产适配的最小接口，不在验证阶段复制一套日语标注器。

## 执行记录

- 2026-11-14：建立隔离验证 issue；尚未修改代码或替换生产资源。

## 关闭时

- 回写到 Epic spec：UniDic 是否通过、字段映射、分词/送假名差异、性能与 AOT 结果。
- 若通过，另开或推进生产切换 issue；若失败，记录失败原因与重新评估条件，不伪装成零结果成功。
