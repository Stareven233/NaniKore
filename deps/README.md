# NaniKore 运行时依赖

NaniKore 的本地 OCR 和 ONNX Runtime 运行时文件。`deps/` 是仓库内依赖的唯一真实源，目录结构与 ModelScope 仓库 `Noetuu/nanikore-deps` 保持一致。

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
└─ onnxruntime/
   ├─ onnxruntime.dll
   └─ onnxruntime_providers_shared.dll
```

`inference.json` 和模型目录中的说明文件是模型仓库附带的资料，C# OCR 运行时不读取它们。`.ms_upload_cache` 是 ModelScope 上传工具缓存，不属于运行时依赖。

## 下载

应用设置页的“下载依赖”按钮会在用户确认后，从 ModelScope `master` 分支下载 OCR 权重和 ONNX Runtime DLL，并保存到当前可执行程序所在目录的 `deps/`。OCR 权重下载失败时，应用会逐个尝试原 Hugging Face 地址；ONNX Runtime DLL 使用 ModelScope 直链。

应用不会在后台静默下载，下载文件会在替换前进行 SHA-256 校验，下载失败或校验失败不会留下可用性不明的文件。

## 注音词典（phonetics/）

本地注音引擎（NMeCab + 按语言分发的词典）使用的词典二进制与语料来源。目录结构与 ModelScope 仓库 Noetuu/nanikore-deps 的 phonetics/ 严格镜像。词典不随程序集分发，按语言在设置页按需下载；哈希即版本（不符则提示更新）。判据词表（停用/高频/常用字符表）是 KB 级策展内容，随程序集内嵌分发，与词典哈希解耦。

```text
deps/phonetics/
├─ ja/     IPAdic 编译态（sys.dic/unk.dic/matrix.bin/char.bin，约 53 MB）
├─ en/     ipa_dict.db（SQLite，CMUdict 转 IPA，约 3.8 MB）
├─ zh/     CC-CEDICT + Unihan 转译 MeCab 词典（约 10 MB）
├─ _source/  公开数据源原件（仅供再生管线，gitignore）
├─ _build/   MeCab 词典编译中间产物（gitignore）
```

| 语言 | 数据来源 | 许可 | 生成方式 |
| --- | --- | --- | --- |
| ja | IPAdic（mecab 官方发布词典） | IPAdic 许可（见 NOTICE） | 直接采用编译态二进制，无本地编译 |
| en | CMUdict（发音词典，ARPAbet 音标） | CMUdict 许可（BSD 风格，见 NOTICE） | 管线转 IPA 写 SQLite；主读音（美式）在生成期定音 |
| zh | CC-CEDICT（汉英词典拼音）+ Unihan kMandarin 补字 | CC BY-SA 4.0（见 NOTICE） | 转 MeCab CSV 后 mecab-dict-index 编译；多音字每条读音各自成条 |

再生管线见 tools/phonetics/README.md；许可归属见仓库根 NOTICE。
