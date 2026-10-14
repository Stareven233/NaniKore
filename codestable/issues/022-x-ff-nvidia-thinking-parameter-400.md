---
kind: issue
title: "[快改] NVIDIA 端点拒收非标思考参数整单 400，补一次去参重试兜底"
type: ff
status: closed
created: 2026-10-20
---

<!-- 快改痕迹：轻。读者只要 30 秒扫完。禁止迷你 Design。 -->

# [快改] NVIDIA 端点拒收非标思考参数整单 400，补一次去参重试兜底

> **读者：** 以后搜到这条时——「改了啥、怎么信、动没动制度记忆」。  
> **自检（四答即可，可合成短段，勿空标题凑节）：** 做了什么 · 改了哪些文件 · 怎么验证 · 对 `codestable/` 有无影响。

---

主人配 `openai-nvidia` 配置档（`https://integrate.api.nvidia.com/v1` + `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning`）后翻译直接失败，服务端原文：`Validation: Unsupported parameter(s): \`enable_thinking\`, \`thinking\``。根因是 issue 016 的设计把「第三方兼容端点恒定下发 `enable_thinking` + `thinking`」当成了通用方言：这套参数只是 Qwen/DeepSeek 一系的非标扩展，NVIDIA 这类严格 Schema 校验端点收到即整单 400，与「启用思考」开关的开/关无关（关闭时下发的是 `false`/`disabled`，同样被打回），用户侧表现为该档完全不可用。

修法不是再维护一份「哪些宿主认得这套参数」的名单（名单必然腐烂，且漏一个就复现本 bug），而是让端点自报方言：首次被 400 且报文点名 thinking 参数时，去掉这两个参数（连同同一方言的 `reasoning_effort`）原样重试一次，并在该 provider 实例内记住「此端点不收思考参数」，后续请求一开始就不带。`ProviderFactory` 以配置档为粒度缓存 provider，所以一次翻译会话最多多付一个立刻返回的 400；与思考无关的 400（坏 Key、模型不存在）不走重试，原样抛出。

- 改动：`src/NaniKore/Translation/Providers/OpenAiProvider.cs` — `CreateCompletionsRequest` 增 `skipThinkingParameters` 开关；`StreamAsync` 改走 `SendWithThinkingParameterFallbackAsync`，命中 `ShouldRetryWithoutThinkingParameters`（非 responses 链路 + 400 + 报文点名 thinking / reasoning_effort）才去参重试一次并置 `_thinkingParametersRejected`。
- 改动：`src/NaniKore/Translation/Providers/TranslationProviderBase.cs` — 构造函数增可选 `transport`，仅供自测注入假网络栈（不引入构造函数里的虚调用，也不需要拆掉各 provider 的 `sealed`）。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Translation.cs` — 新增 `TestOpenAiThinkingParameterRejectionFallback`：用逐字复刻 NVIDIA 报错的脚本 handler 验「首请求带参被打回 → 去参重试成功出流 → 第三次请求一开始就不带参」，另验「只点名 reasoning_effort 也走去参重试」「无关 400 不重试」；配套 `Drain` / `Rejected` / `Streaming` / `ScriptedHandler` 助手。
- 验证：`dev.ps1 build` + `dev.ps1 test` 全绿（新增 4 条 PASS，原思考参数 9 条口径不变）；对真实 NVIDIA 端点实测——带 `enable_thinking`/`thinking` → HTTP 400 原文复现，去掉后 → HTTP 200 且流式返回 `reasoning_content` 增量（该字段本就被 `ExtractCompletionsDelta` 分到 Thinking 通道，故修复后 NVIDIA 档思考动态与正文都正常）。
- codestable：无影响（仅 provider 请求兜底 + 自测，未触碰 spec 既定口径；issue 016 的「兼容端点下发 enable_thinking」设计对认得该方言的端点原样有效）。
