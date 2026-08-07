# 本地模型目录

这个目录用于放置运行时模型权重，权重文件默认不提交 Git。

## 当前布局

```text
models/
├─ pp-ocr-v6-small/
│  ├─ det/
│  │  ├─ inference.onnx
│  │  └─ inference.yml
│  └─ rec/
│     ├─ inference.onnx
│     └─ inference.yml
└─ llm/                     # 后续本地翻译模型，可选
   └─ *.gguf
```

运行下面的命令会从 PaddlePaddle 官方模型仓库下载并校验 PP-OCRv6-small：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/bootstrap-models.ps1
```

`inference.json` 是官方导出附带的可选图描述，当前 C# 运行时不需要它。

## 约束

- PP-OCRv6 直接使用 ONNX Runtime C# API，不依赖 Python/leaf-ocr。
- 输入尺寸、归一化参数、检测阈值和识别字典从 `inference.yml` 读取。
- 模型目录可通过 `NANIKORE_MODEL_DIR` 覆盖；默认会从程序目录和项目祖先目录查找。
- GGUF 只用于后续本地语言模型；OCR 继续使用 ONNX，二者不混为一种格式。