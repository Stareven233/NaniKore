---
kind: issue
title: "降低 OCR 内存峰值并解除结果窗口关闭后的事件引用"
type: ff
status: closed
created: 2026-11-09
---

- 做了什么：禁用动态截图推理的 ONNX Runtime CPU arena 与 memory pattern；后处理直接读取原生输出并按作用域释放，移除完整概率矩阵的托管复制。结果窗口关闭时退订静态注音通知、解除精讲收藏服务三项订阅、取消注音回写；注音任务结束释放 CancellationTokenSource。
- 改了哪些：`OnnxModelRunner`、`TextDetector`、`TextRecognizer`、`ResultPanelWindow`；增加关闭窗口事件订阅清理自测。保持模型会话复用、检测分辨率、识别算法和默认推理线程策略。
- 怎样验证：`./scripts/dev.ps1 build` 成功；`./scripts/dev.ps1 test` 通过，直接捕获自测输出确认 1210 checks 全部通过。Debug 独立进程识别同一张 1920×1080、10 行中英日混排图片，按 10 ms 采样：改前工作集峰值 577.5 MiB、私有内存 777.6 MiB、OCR 2446 ms；最终版三轮工作集峰值 312.9/340.3/349.4 MiB，私有内存 319.6/321.2/321.6 MiB，OCR 2621/2415/2486 ms。10 行文字、位置、打印的置信度一致。CLI 冷进程峰值不等于 GUI 稳态占用；尚未实测 Native AOT 最终版与 GUI 长时间运行内存。
- 发布修复：订阅计数自测的 `GetField` 参数包含运行期 `BindingFlags` 分支，AOT 分析要求保留公共与非公共字段；补齐 `PublicFields | NonPublicFields` 注解，不抑制裁剪告警。`./scripts/dev.ps1 publish` 已成功生成 ZIP，Native AOT 发布版 `--self-test` 的 1210 checks 全部通过。
- 取舍：限制每个模型两条推理线程的收益不稳定且耗时增加，已撤回；未加入强制 GC、工作集裁剪或逐次重建模型。
- codestable 影响：已同步 `codestable/spec/index.md` 的 OCR 输出所有权与结果窗口关闭清理契约。
