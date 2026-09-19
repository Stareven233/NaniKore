---
kind: issue
title: "全模态两阶段流式与 Session 缓存复用"
type: feature
status: in_progress
created: 2026-09-19
updated: 2026-09-19
---

# 全模态两阶段流式与 Session 缓存复用

> **读者：** 负责两阶段流式理解链路、Prompt Cache 会话复用、多厂商 Provider 前缀对齐与精讲 Expander 交互的开发者。  
> **归属：** `codestable/epics/001-o-学习型演进-精讲假名与生词库/spec.md` 切片 3。  
> **关联代码落点：**  
> - `src/NaniKore/Translation/TranslationSession.cs`  
> - `src/NaniKore/Translation/UnderstandContracts.cs`  
> - `src/NaniKore/Translation/PromptTemplates.cs`  
> - `src/NaniKore/Translation/Providers/OpenAiProvider.cs`  
> - `src/NaniKore/Translation/Providers/ClaudeProvider.cs`  
> - `src/NaniKore/Translation/Providers/GeminiProvider.cs`  
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `ResultPanelWindow.xaml.cs`  
> - `src/NaniKore/App/NaniKoreApp.cs`  
> - `src/NaniKore/Diagnostics/SelfTest.Translation.cs` / `SelfTest.Ui.cs`

---

## 背景与目标

本切片实现两阶段流式理解与会话上下文复用：
1. **统一 `TranslationSession`**：维护包含 System Prompt、第一阶段用户输入（包含文本及图片）、模型原始 Assistant 响应、以及第二阶段追问的多轮对话历史；
2. **多厂商前缀缓存（Prompt Cache）100% 保持**：无论纯文本 OCR 还是多模态 Vision 模式，第二阶段向 OpenAI、Claude、Gemini 发起的请求严格复用第一轮请求的 System Prompt 与第一轮输入/输出前缀，确保 Token 序列完全前缀匹配；
3. **两阶段流式路由解耦**：第一阶段保持极速翻译与假名标注；第二阶段（`UnderstandKind.Explain`）流式绕过段落分流器，直出至精讲文本框；
4. **结果卡 Expander 惰性触发与状态机管理**：
   - 翻译中/无译文：Expander 禁用；
   - 翻译完成：Expander 激活，标题提示 `📖 精讲与语法解析 (点击展开)`；
   - 用户展开：立即触发第二阶段流式生成，状态栏与指标栏实时呈现响应；
   - 生成完成：折叠与再展开不再重复请求；用户编辑原文重译时自动重置。

---

## 执行痕迹与闭环成果

1. **抽象统一的多模态 `TranslationSession`**：
   - 在 `TranslationSession.cs` 中建立包含 `ChatRole` 与 `ChatMessage` 的多轮会话结构；
   - 支持多模态 Payload：首轮 User 消息完整保存原始截图二进制（`ImagePng`）与 OCR 文本；
   - 原始输出无损累积：在第一阶段流式过程中，通过 `AppendRawAssistantOutput` 将模型输出的逐 token 纯文本（含注音与译文分隔标记）100% 完整录入，流结束时调用 `FinalizeStage1Output` 固化为 Assistant 消息，杜绝重新序列化带来的空格、换行或格式漂移；
   - 保持前缀一致性：在第二阶段准备时，调用 `PrepareStage2Request` 注入精讲追问（`PromptTemplates.DefaultExplainUserPrompt`），使整个会话消息链严格保持为 `User -> Assistant -> User`。
2. **主流厂商多轮前缀缓存（Prompt Cache）深度对齐**：
   - **`OpenAiProvider`**：
     - `chat/completions`：支持在 `request.Session` 存在且包含多轮时构建包含 `system`、首轮 `user`（包含 `image_url` 与文本）、`assistant`、第二轮 `user` 的 messages 数组；
     - `responses`：支持构建包含 `input_image`、`input_text` 与 `output_text` 的多轮 input 结构；
   - **`ClaudeProvider`**：
     - 顶层保持完全相同的 `system` 提示词，`messages` 数组交替生成首轮 `user`（包含 `image` 与 `text` block）、`assistant`（第一轮原样文本）与第二轮 `user`（精讲追问）；
   - **`GeminiProvider`**：
     - 保持完全相同的 `system_instruction`，`contents` 数组生成 `user`（含 `inline_data` 与 `text`）、`model`（首轮输出）与 `user`（精讲追问）；
   - **`PromptTemplates`**：
     - `GetSystemPrompt` 增加对 `request.Session.SystemPrompt` 的优先复用，确保第一阶段动态计算出的 System Prompt 在第二阶段 100% 保持不变，满足 Prompt Caching 的逐字节前缀匹配硬约束。
3. **两阶段流式分流路由彻底解耦**：
   - `SectionChannelRouter` 仅在 `request.Kind == UnderstandKind.Translate` 时启用；
   - 精讲模式（`UnderstandKind.Explain`）全面绕过分栏器，流式增量直接标记为 `DeltaChannel.Explanation` 直出至 UI，消除段落标记解析阻滞。
4. **结果卡底部 Expander 惰性展开与状态机完整闭环**：
   - 窗口加载与翻译进行中：`ExplanationExpander` 保持折叠且 `IsEnabled = false`；
   - 阶段 1 成功收到有效结果：`panel.SetExplanationEnabled(true)`，标题显示 `📖 精讲与语法解析 (点击展开)`；
   - 用户展开：触发 `ExplanationRequested` 事件并调用 `NaniKoreApp.StartExplanationAsync`；
   - 生成中：标题动态更新为 `📖 精讲与语法解析 (正在生成…)`，流式增量打字机呈现至 `ExplanationTextBox`，状态栏实时更新 TTFT；
   - 生成完成：标题更新为 `📖 精讲与语法解析`，折叠并再次展开不重复调用大模型；
   - 失败重试：标题提示 `📖 精讲与语法解析 (点击重试)`，允许再次展开重试；
   - 重译/新截图复位：发起新翻译时自动折叠、清空、禁用 Expander，并重置会话。

---

## 质量目标兑现情况

- **性能效率（Performance Efficiency）**：
  - 会话前缀一致性：第二阶段向大模型发送的请求严格复用第一阶段完全一致的 System Prompt 与 User（含多模态图像）+ Assistant 历史，确保稳定命中 OpenAI、Claude、Gemini 的 Prompt Cache，节省 >80% 输入 Token 并将第二阶段首字响应延迟压缩至最低；
  - 惰性调用：不主动展开则绝不发起第二阶段调用，零额外开销。
- **易用性（Usability）**：
  - 底部点击即展开流式输出，配合 TTFT 实时反馈与自动滚屏；
  - 折叠后不打扰用户查阅核心译文，生成过的内容再次展开 0 延迟立即可见。
- **可靠性（Reliability）**：
  - 请求取消与面板生命周期严密联动：关闭结果卡或发起重译时，自动打断正在进行的精讲请求并清理资源。

---

## 验证证据

1. **自动化自测全量通过**：
   - 运行命令：`powershell -ExecutionPolicy Bypass -File ./scripts/dev.ps1 test`
   - 测试结果：80+ 项自测试（含 `TranslationSession` 初始化与多轮累积、OpenAI/Claude/Gemini 多轮请求结构组装校验、SystemPrompt 前缀复用校验、Expander 状态机联动与流式打字测试）全部 PASS，退出码 0。
2. **编译构建验证**：
   - 运行命令：`powershell -ExecutionPolicy Bypass -File ./scripts/dev.ps1 build`
   - 构建结果：0 警告，0 错误。

