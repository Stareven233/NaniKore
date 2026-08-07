---
kind: roadmap
status: active
created: 2026-08-07
branch: main
---

# C# 单进程迁移

## 为什么迁移

旧实现把 Gio 输入命中、Win32 热键消息循环、外部窗口摆位和 DPI 坐标耦合在一起。上一轮仅通过编译检查便错误关闭 issue，真实交互仍会出现左上角小窗口和卡死。

迁移已停止继续修补 Gio，改用 .NET 10 WinForms 提供单一 Windows 消息循环。旧实现只保留在 `legacy-go` 分支，不再存在于 `main` 工作树。

## 硬约束

- 单一 C# 进程，Windows 10/11 x64 优先。
- 工具链默认安装到项目内 `.tools/dotnet`，不污染系统环境。
- 选区使用物理像素坐标，支持虚拟桌面负坐标和 Per-Monitor V2。
- 模型权重放在项目内 `models/`，但不提交 Git。
- PP-OCRv6 直接调用 ONNX Runtime，不依赖 Python、leaf-ocr 或 OpenCV。
- 发布保留 `NaniKore.exe` 与 ONNX Runtime 原生 DLL 分文件，便于观察和替换原生依赖。
- GGUF 只作为后续本地翻译模型的可选运行时，不进入截图/OCR 核心链路。

## 分阶段交付

### 阶段 1：截图选区闭环（已完成）

- 全局热键 Ctrl+Shift+A。
- 捕获完整虚拟桌面。
- 无边框置顶浮层。
- 左键拖选，Esc/右键取消。
- 选区只在内存中传递，不产生临时截图文件。
- 连续触发不重入、不死锁。

### 阶段 2：OCR（已完成）

- 使用 `Microsoft.ML.OnnxRuntime` CPU 包。
- 直接运行 PP-OCRv6-small 检测与识别 ONNX。
- 模型会话常驻复用；截图完成后只传递裁剪区域。
- 实现纯 C# 图像张量转换、DB 连通域近似后处理、动态宽度识别和 CTC 解码。
- OCR 在工作线程执行，首次加载期间显示进度窗。
- 识别结果自动复制到剪贴板并显示。

### 阶段 3：翻译（尚未开始）

- 先定义云端翻译与本地翻译共用的 provider 契约。
- 如果采用 GGUF，使用 llama.cpp 兼容运行时；原生库按需加载，不让未启用用户承担体积。
- OCR 与 LLM 运行时生命周期分离，允许只启用 OCR。

## 验收结论

2026-08-07，主人完成真实使用验收：除副显示器因当前无设备无法测试外，其余项目均无问题。此前的“快捷键后自动截取左上角小块并卡死”现象已经消失。

已完成的机器验证包括：

- Debug/Release 构建 0 warning、0 error；
- 10 项无 UI 自测全部通过；
- 发布产物 `--self-test` 通过；
- 官方 PP-OCRv6-small 检测/识别模型元数据检查通过；
- 真实中英混排图片 OCR 成功；
- 自动化键鼠冒烟连续完成两轮 `Ctrl+Shift+A → 拖选 → OCR → 关闭结果窗`；
- framework-dependent 发布为 EXE + 两个 ONNX Runtime 原生 DLL，总计约 40.18 MiB；
- 必需模型约 29.75 MiB，完整应用约 69.93 MiB。

副显示器、负坐标虚拟桌面和混合 DPI 的实机回归记为环境暂不可用，不阻塞阶段 1/2 收尾。阶段 3 保持 active，待下一次开发继续。
