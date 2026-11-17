# NaniKore 运行时依赖

本目录包含应用本地 OCR、ONNX Runtime 与注音功能所需的数据文件。

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
   ├─ ja.zip            # 日语注音数据包
   ├─ en.zip            # 英语注音数据包
   └─ zh_pinyin.txt.gz  # 中文拼音数据
```

`ocr-onnx/` 包含 PP-OCRv6 small 的检测与识别模型及配置。`onnxruntime/` 包含运行 ONNX 模型所需的 Windows x64 运行库。

`phonetics/` 包含日语、英语原生注音数据包和中文拼音合并表。日英 ZIP 内含运行库、包清单与许可文件；中文文件为压缩 TSV 数据。

| 文件 | 内容 | 数据来源 | 许可 |
| --- | --- | --- | --- |
| `ocr-onnx/pp-ocr-v6-small/det/inference.onnx` | 文本检测模型 | PaddleOCR PP-OCRv6 small | Apache-2.0（见 NOTICE） |
| `ocr-onnx/pp-ocr-v6-small/rec/inference.onnx` | 文本识别模型 | PaddleOCR PP-OCRv6 small | Apache-2.0（见 NOTICE） |
| `ocr-onnx/pp-ocr-v6-small/{det,rec}/inference.yml` | 对应模型配置 | PaddleOCR PP-OCRv6 small | Apache-2.0（见 NOTICE） |
| `onnxruntime/*.dll` | Windows x64 推理运行库 | ONNX Runtime | MIT（见 NOTICE） |
| `phonetics/ja.zip` | 日语原生注音包，含书面假名词典与许可文件 | MisakiSharp 2.2.0、UniDic 3.1.0 | Apache-2.0 / BSD-3-Clause（见 NOTICE） |
| `phonetics/en.zip` | 英语原生注音包，含美音词典与许可文件 | MisakiSharp 2.2.0 | Apache-2.0（见 NOTICE） |
| `phonetics/zh_pinyin.txt.gz` | 中文拼音合并表（TSV + gzip） | pypinyin、CC-CEDICT、Unihan kMandarin | MIT / CC BY-SA 4.0 / Unicode License v3（见 NOTICE） |

中文数据的来源优先级和生成规则见仓库内部维护文档；各组件的完整许可文本与归属见仓库根目录 `NOTICE`。
