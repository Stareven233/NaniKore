---
kind: issue
title: "纯视觉请求缓冲与面板会话释放、自动注音策略修复"
type: ff
status: closed
created: 2026-11-09
---

- 做了什么：纯视觉截图编码完成后、请求序列化前归还原始截图像素；关闭结果窗口取消请求并清空最后截图与精讲会话。接纳新输入（包括关闭自动翻译的输入）先取消旧流、清除旧会话；完成与错误 UI 回调在实际执行时检查请求 generation、取消状态及窗口身份，旧任务不能恢复已关闭面板的精讲。翻译 finally 的 CTS 身份判断及清理合并到同一 UI 回调。
- 请求内存：Provider 以 Utf8JsonWriter 直接写 UTF-8，通过 ByteArrayContent 持有缓冲有效区段，避免完整 UTF-16 JSON 副本及 ToArray 复制。SendAsync 上传完成（ResponseHeadersRead）或失败后解除 request.Content 引用并释放，不等 SSE 接收结束。窗口打开期间仍保留原图与消息历史，重译和精讲语义不变。
- 注音开关：保留总开关与自动触发两个独立设置，通用页明确标为「允许本地注音」。修复视觉入口遗漏 auto_annotate 和翻译完成绕过自动开关的问题；关闭自动注音仍可手动注音，关闭总开关两者都禁用。Reset 取消上一轮注音，防止新输入被旧结果覆盖。
- 改了哪些：App.axaml.cs、ResultPanelWindow、GeneralPage/TranslationPage 说明、TranslationProviderBase 以及请求所有权和注音策略回归自测。
- 怎样验证：Debug 构建及自测通过；`./scripts/dev.ps1 build -Release` Native AOT 构建成功，发布版 `--self-test` 1364 checks 全部通过。请求载荷与旧 StringContent 路径逐字节一致，覆盖 Unicode/转义/嵌套 JSON 与 256 KiB 图片 base64；验证上传释放后响应仍可读、HTTP 错误/超时/取消、OpenAI 400 重建请求回退及自动/手动注音优先级。
- 实测边界：用户报告纯视觉且禁用注音的进程峰值约 170 MB，稍后约 130 MB。本次确认并消除了具体副本和应用字段根引用，尚未完成同场景 GUI 稳态内存的前后对照，不承诺具体降幅。截图、字体、图形缓存及分配器保留仍需靠运行期采样区分；不强制 GC 或裁剪工作集。
- codestable 影响：已同步 `codestable/spec/index.md` 的视觉会话、请求缓冲所有权与两层注音开关语义。
