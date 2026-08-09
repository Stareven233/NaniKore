# Project Spec

## 这个项目是什么

NaniKore 是一个轻量的 Windows 截图 OCR 翻译工具。用户按全局快捷键框选屏幕文字，程序在本地完成 OCR；后续阶段再接入云端或本地翻译，并提供按需精讲。

## 当前已实现真相

### 捕获与选区

- 单一 C#/.NET 10 WinForms 进程和 Windows 消息循环。
- `Ctrl+Shift+A` 注册为全局热键。
- 使用 GDI 捕获完整虚拟桌面。
- 进程在创建 HWND 前启用 Per-Monitor V2，内部使用物理像素坐标。
- 左键拖选，Esc/右键取消，重复热键不会重入第二条选区流水线。
- 裁剪位图只在内存中向下游传递，不写临时截图。

### OCR

- C# 通过 `Microsoft.ML.OnnxRuntime` 直接运行 PP-OCRv6-small 检测与识别 ONNX。
- 不依赖 Python、leaf-ocr 或 OpenCV。
- 模型配置、归一化参数和识别字典来自官方 `inference.yml`。
- 检测后处理为纯 C# 连通域与水平矩形近似；识别包含动态宽度和 CTC 解码。
- 模型会话复用，推理在工作线程执行。
- 结果自动复制到剪贴板并显示在结果窗口。

### 翻译、结果与配置（初版，待真机整改）

- 已有 OpenAI（completions/responses）、Claude、Gemini 的流式 provider 契约，支持 `local_ocr` 与 `vision_oneshot` 两条输入路径。
- 结果窗口可显示 OCR 原文和流式译文，设置页包含通用、翻译、热键和关于页；API Key 以当前 Windows 用户的 DPAPI 密文保存。
- 2026-08-09 真机验收发现结果卡层级/编辑、翻译目标可靠性、配置档切换、配置版本、设置生命周期和热键录制等问题，尚不能视为稳定体验。下一轮实施与验收以 `codestable/roadmap/002-csharp-single-process-migration/phase-3-post-test-remediation.md` 为准。
### 工具链与发布

- .NET SDK、NuGet 缓存和模型默认放在项目目录。
- Windows x64 framework-dependent 发布为 `NaniKore.exe`、`onnxruntime.dll` 和 `onnxruntime_providers_shared.dll`。
- 发布总计约 40.18 MiB；必需 OCR 模型约 29.75 MiB。
- 目标机器需要 .NET 10 Desktop Runtime。

## 已验证边界

- 构建、自测、真实中英混排 OCR、自动化键鼠冒烟和主显示器人工交互均通过。
- 副显示器、负坐标虚拟桌面和混合 DPI 代码路径已实现，但因当前没有副显示器设备，尚未完成实机回归。
- 水平游戏/UI 文本是当前目标；旋转、弯曲和竖排文本不是当前轻量后处理的保证范围。

## 尚未实现

- 可选 GGUF 本地翻译运行时。
- 精讲的实际调用、内容结构与持久化。
- 阶段 3 初版体验的真机整改，见上述整改规格。
下一阶段规划见 `codestable/roadmap/002-csharp-single-process-migration/spec.md`。
