---
kind: issue
title: "[Claude 视觉流空响应回退]"
type: ff
status: closed
created: 2026-08-29
---

- 做了什么：Claude 视觉请求跳过空文本内容块；SSE 仅返回零正文时，自动以非流式 `v1/messages` 重试并解析完整 message；HTTP 失败诊断会记录限长服务端错误文本。
- 改了哪些：`src/NaniKore/Translation/Providers/ClaudeProvider.cs`、`src/NaniKore/Translation/Providers/TranslationProviderBase.cs`、`src/NaniKore/Translation/TranslationDiagnostics.cs`；`src/NaniKore/Diagnostics/SelfTest.cs`。
- 怎么验证：`./scripts/dev.ps1 test` 通过；新增空文本块省略、Claude 完整响应解析、错误文本限长与日志目录回归检查。
- 对 `codestable/` 的影响：无影响。
- 说明：诊断文件改为 `<当前 EXE 目录>/logs/translation-diagnostics.log`，不再写入用户 AppData 目录。
