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
