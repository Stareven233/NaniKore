---
doc_type: issue-fix
issue: 002-x-ff-选区浮层修复
path: fast-track
fix_date: 2026-08-07
tags: [windows, overlay, csharp, winforms, ocr]
---

# 选区浮层卡死修复记录

## 1. 问题描述

Go/Gio 版本按下 `Ctrl+Shift+A` 后无法正常控制选区，会自动截取左上角一小块并卡死。上一轮仅以 `go build` 和 `go vet` 通过作为成功依据，实际修复结论无效。

## 2. 根因

问题不是单个输入事件 API 的遗漏，而是 Gio 输入命中、Win32 全局热键、窗口摆位、截图坐标与 DPI 虚拟化共同耦合。继续在两个消息模型之间打补丁无法建立可信的真实交互闭环。

同时，原验证流程缺少真实键鼠测试，只验证“可以编译”，因此没能发现运行时仍然失败。

## 3. 修复方案

整体迁移为单一 C#/.NET 10 WinForms 程序：

- 热键、截图、浮层、键鼠输入和结果窗口统一运行在一个 Windows 消息循环中；
- 在创建 HWND 前启用 Per-Monitor V2，选区统一使用物理像素坐标；
- 选区位图在内存中直接交给 C# OCR，不通过临时文件或 Python 子进程；
- ONNX Runtime C# 直接运行 PP-OCRv6-small 检测和识别模型；
- OCR 放到工作线程，首次模型加载时保持 UI 响应；
- 发布保留 `NaniKore.exe` 与 ONNX Runtime 原生 DLL 分文件；
- 旧 Go/Gio 实现从 `main` 删除，仅保留在 `legacy-go` 分支。

## 4. 改动文件清单

- `src/NaniKore/`：C# WinForms 截图、选区、热键、OCR、诊断与结果界面。
- `scripts/`：项目本地 SDK/模型准备、构建、自测和发布脚本。
- `models/README.md`：PP-OCRv6-small 本地模型布局与约束。
- `README.md`：当前实现、开发命令、分文件发布与已知边界。
- `codestable/roadmap/002-csharp-single-process-migration/spec.md`：迁移阶段和验收状态。
- 删除 `cmd/`、`pkg/`、`go.mod`、`go.sum`、旧版本配置与旧二进制。

## 5. 验证结果

- Debug/Release 构建：0 warning、0 error。
- 无 UI 自测：10 项全部通过。
- 发布版 `--self-test`：通过。
- 模型元数据检查：通过。
- 真实中英混排 OCR：通过。
- 自动化 GUI 冒烟：连续两轮完成热键、拖选、OCR 和关闭结果窗口，进程未卡死。
- 主人真实验收：除副显示器因无设备无法测试外，其余项全部通过。

## 6. 遗留事项

- 将来具备副显示器后，补测负坐标虚拟桌面和混合 DPI；当前属于测试环境缺失，不是已知失败。
- 当前轻量 DB 后处理优先水平文本；旋转、弯曲和竖排文本按真实需求再评估。
- 翻译 provider 与可选 GGUF 本地模型属于下一阶段，不纳入本次 issue。
