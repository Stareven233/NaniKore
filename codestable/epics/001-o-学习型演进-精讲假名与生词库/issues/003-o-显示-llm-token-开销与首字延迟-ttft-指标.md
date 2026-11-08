---
kind: issue
title: "显示 LLM Token 开销与首字延迟（TTFT）指标"
type: feature
status: open
created: 2026-09-19
updated: 2026-09-19
---

# 显示 LLM Token 开销与首字延迟（TTFT）指标

> **读者：** 负责流式 Token 提取、TTFT 度量、TPS计算以及面板状态呈现的开发者。  
> **归属：** `codestable/epics/001-o-学习型演进-精讲假名与生词库/spec.md` 切片 6。  
> **关联代码落点：**  
> - `src/NaniKore/Translation/UnderstandContracts.cs`  
> - `src/NaniKore/Translation/TokenEstimator.cs`  
> - `src/NaniKore/Translation/Providers/OpenAiProvider.cs`  
> - `src/NaniKore/Translation/Providers/ClaudeProvider.cs`  
> - `src/NaniKore/Translation/Providers/GeminiProvider.cs`  
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `ResultPanelWindow.xaml.cs`  
> - `src/NaniKore/App/NaniKoreApp.cs`  
> - `src/NaniKore/Diagnostics/SelfTest.Translation.cs` / `SelfTest.Ui.cs`  

---

## 做成以后是什么样

1. **统一输入/输出 Token、TTFT 与TPS指标感知**：
   - 结果卡底部在翻译完成时优雅呈现本次调用的性能与开销指标行：
     `Input: 320 tokens · Output: 45 tokens · TTFT: 680ms · TPS: 42 t/s`；
   - 收到首个非空文本 Token 时，状态栏即时反馈首字响应延迟（如 `首字响应 TTFT: 680ms · 正在流式接收…`）。
2. **多模型服务商流式 Usage 全覆盖**：
   - **OpenAI**：为 `chat/completions` 声明 `stream_options: { include_usage: true }`，并在流中捕获 `usage`；支持 `responses` API 的 `response.done` / `usage` 提取；
   - **Claude**：从 `message_start` 事件提取 `input_tokens`，从 `message_delta` 提取 `output_tokens`；
   - **Gemini**：从流式候选块中提取 `usageMetadata` 的 `promptTokenCount` 与 `candidatesTokenCount`。
3. **启发式 TokenEstimator 降级兜底**：
   - 针对未返回 usage 的第三方代理或截断请求，根据 CJK 单字（~1.1-1.3 token）与英文单词（~1.3 token）进行启发式估算，并标以 `~` 标记。
4. **测试全面覆盖**：
   - 自测试集覆盖 OpenAI completions、responses、Claude、Gemini 的 SSE 解析，TokenEstimator 估算，TranslationMetrics 格式化以及 UI 面板在 Reset / BeginTranslation / SetMetrics 下的状态切换。

---

## 现状怎么工作

原先流式翻译完成时仅在 `StatusBar` 呈现静态文本“翻译完成。”，用户无法得知本次请求的实际输入与输出 Token 消耗，亦无法感知当前接口与模型的网络首字延迟（TTFT）与推理吞吐速度。

---

## 动了哪些、验了哪些

- **动了哪些**：
  - `UnderstandContracts.cs`：添加 `TokenUsage`、`TranslationMetrics`，并在 `UnderstandDelta` 中支持携带可选 `Usage`；
  - `TokenEstimator.cs`：新建轻量 CJK/英文/多模态图像估算器；
  - `OpenAiProvider.cs`、`ClaudeProvider.cs`、`GeminiProvider.cs`：在流式 SSE 解析中提取各自的输入/输出 Token，并在结束时产出带 `Usage` 的 delta；
  - `ResultPanelWindow.xaml` / `.xaml.cs`：新增 `MetricsBar`，暴露 `TranslationText`、`SetMetrics`、`ClearMetrics`，并在重置/新翻译时自动折叠；
  - `NaniKoreApp.cs`：使用 `Stopwatch` 统计 TTFT 与总耗时，计算 `tokens/s` TPS，并调度下发给面板。
- **验了哪些**：
  - 运行 `./scripts/dev.ps1 test`，所有翻译与 UI 相关测试用例全部 PASS。

