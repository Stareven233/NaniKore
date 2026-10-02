---
kind: issue
title: "模型思考链过滤隔离、译文区节流无缝衔接展示与启用思考配置方案"
type: feature
status: resolved
created: 2026-10-02
updated: 2026-10-02
---

# 模型思考链过滤隔离、译文区节流无缝衔接展示与启用思考配置方案

> **读者：** 负责翻译流水线、路由器与结果面板/设置面板交互的开发者。  
> **关联文件：**  
> - `src/NaniKore/Translation/UnderstandContracts.cs` (DeltaChannel 扩展)  
> - `src/NaniKore/Translation/ThinkingStreamSanitizer.cs` (新增流式思考链状态机)  
> - `src/NaniKore/Translation/Providers/TranslationProviderBase.cs` (流式派发前置过滤)  
> - `src/NaniKore/Translation/SectionChannelRouter.cs` (防御性前导标签补充)  
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `.xaml.cs` (译文区节流刷新与正文无缝衔接)  
> - `src/NaniKore/Config/AppConfig.cs` (ProviderProfile EnableThinking 配置项，默认 false)  
> - `src/NaniKore/App/Settings/TranslationPage.xaml` / `.xaml.cs` (设置页“启用思考”开关呈现)  
> - `src/NaniKore/Translation/Providers/ClaudeProvider.cs` / `GeminiProvider.cs` / `OpenAiProvider.cs` (API 参数自适应注入，含 OpenAI enable_thinking)  
> - `src/NaniKore/Diagnostics/SelfTest.Translation.cs` (全量单测覆盖)  

---

## 0. 背景与主人明确诉求

部分具有深度推理能力的大模型（如 DeepSeek-R1、Qwen-qwq、Claude 3.7 Thinking、Gemini 2.0/2.5 Flash Thinking、以及本地 Ollama 部署的蒸馏小模型等）在流式输出时，会将思考过程 `<think>...</think>` 打在正文区域。当前系统的分栏路由器 `SectionChannelRouter` 误将思考链当作原文发射，导致原文注音解析崩溃、译文被污染，精讲模式也因此格式错乱。

主人明确要求：
1. **译文区就地动态刷新思考，正文无缝衔接**：不放在底部状态栏，直接在用户的核心视觉焦点——**译文文本框**内以低频节流刷新思考摘要（1~2 秒更新一次）；一旦正式译文到达，瞬间无缝替换为正文并恢复流式打字，体验丝滑自然。
2. **设置项标题为“启用思考”，默认关闭**：配置模型命名为 `EnableThinking`，默认值为 `false`（追求极速响应与节省 Token）。
3. **OpenAI 兼容协议支持 `enable_thinking`**：在 `OpenAiProvider` 请求体中适配各大兼容服务商广泛采用的 `enable_thinking` 参数。
4. **双层结合方案**：底层流式状态机硬核过滤兜底 + 上层 API 参数控制，彻底杜绝思考污染。
5. **按 CodeStable 规范将方案落成正式工程文档**。

---

## 1. 做成以后是什么样（可观察结果）

1. **绝对免疫污染：**
   - 无论用户选用什么模型、是否支持关闭思考、中转站是否魔改，原文框永远只接收干净的带注音原文，译文框只接收干净译文，精讲区只接收规范 Markdown，绝不泄漏任何 `<think>` 标签或思考自然语言。
2. **译文区就地呈现思考摘要与正文无缝衔接（极致视觉焦点体验）：**
   - 当模型处于思考阶段时，**译文文本框**内直接呈现单行思考动态，形如：  
     `💭 [思考中] 分析复合动词「～てしまう」的完成与遗憾双重语义...`
   - 界面以约 1.2 秒的节流频率提取思考流的最新单行内容并就地刷新。
   - 当正式译文（`DeltaChannel.Translation`）首个字符到达的瞬间，译文框的思考提示瞬间清除，无缝替换为正式译文并恢复实时流式打字，零残留、零跳动。
3. **设置项明确掌控，默认极速：**
   - 设置面板的每个配置档中提供“**启用思考**”开关，默认**关闭**。
   - 关闭时向 Claude、Gemini 以及 OpenAI 兼容端点（下发 `enable_thinking: false` 等）自动下发禁用参数，省去长达数十秒的无谓思考和 Token 消耗。开启时允许深度思考并享受译文区思考进度反馈。

---

## 2. 现状与根因诊断

### 2.1 触发路径与断裂点

```
模型 SSE 帧 ──> ExtractCompletionsDelta ──> Route ──> SectionChannelRouter.Push ──> Emit(SourceText) ──> ResultPanel.Append
```

1. **`SectionChannelRouter` 假名/标点探测误判：**
   - 路由器初始处于 `_awaitingFirstSection = true`。
   - `SourceLeadingTags` 仅定义了 `<annotated_source>`、`【原文】` 等，**完全不认识 `<think>`**。
   - 当思考内容累积超过 32 字符安全窗口，且思考中包含日语假名、汉字或方括号时，`GetSafeSourceFlushLength` 判定可以安全输出，将大段思考文本直接作为 `SourceText` 持续发射！
2. **`RubyTextParser` 误判注音：**
   - 原文区收到思考内容后，调用 `RubyTextParser.Parse`。思考过程中的方括号（如引用 `[1]` 或标记）被强制解析为振假名，导致原文排版崩溃。
3. **阶段 2 精讲模式直接穿透：**
   - `UnderstandKind.Explain` 阶段直接绕过路由器发射 `DeltaChannel.Explanation`。思考链混入 Markdown 正文，破坏卡片标题（`### 重点词汇` 等），导致微结构化解析完全失败并降级。

---

## 3. 详细设计与实现安排

### 3.1 架构分层：双层防御 + 正交数据通道

```
                    [ 模型原始流式输出 ]
                             │
                             ▼
┌─────────────────────────────────────────────────────────────┐
│  ThinkingStreamSanitizer (通用流式思考链状态机)               │
│  - 状态转移: Normal ──> BufferingPrefix ──> InsideThinking  │
│  - 吞掉 <think> 与 </think> 标签，吸收内部内容               │
│  - 将思考文本重定向派发为 DeltaChannel.Thinking              │
└────────────────────────────┬────────────────────────────────┘
                             │
            ┌────────────────┴────────────────┐
            ▼                                 ▼
   [ 干净的业务流 ]                    [ DeltaChannel.Thinking ]
   - Translate -> SectionChannelRouter        │
   - Explain -> ExplanationParser             ▼
            │                         [ ResultPanelWindow ]
            ▼                         - 1.2s 节流采样 / 提取最新思考片段
   [ 原文/译文/精讲干净上屏 ]           - 译文文本框内就地动态呈现:
                                        `💭 [思考中] {最新思考摘要}...`
                                      - 正式译文到达时瞬间清空思考占位并无缝流式衔接！
```

### 3.2 模块一：`ThinkingStreamSanitizer` 流式状态机

新建 `src/NaniKore/Translation/ThinkingStreamSanitizer.cs`：
- **状态定义：**
  - `Normal`：正常正文流，实时向下游输出；若遇到 `<`，进入 `BufferingOpenTag`。
  - `BufferingOpenTag`：缓冲潜在标签前缀（支持跨 chunk 拼接，如 Chunk 1 为 `<thi`，Chunk 2 为 `nk>`）。
    - 匹配到 `<think>` / `<thought>`：进入 `InsideThinking`，已缓冲的前缀被静默吞掉，不输出给业务通道；
    - 不匹配：将缓冲内容原样退回输出给业务通道，回到 `Normal`。
  - `InsideThinking`：将接收到的内容封装为 `DeltaChannel.Thinking` 增量输出；同时缓冲寻找闭合标签 `</think>`。
  - `BufferingCloseTag`：匹配闭合标签前缀。
    - 匹配到 `</think>` / `</thought>`：退出思考态，顺带吞掉紧随的一个 `\r\n`，回归 `Normal`。
- **正交性：**
  - 在 `TranslationProviderBase.Route` 入口前串接 `ThinkingStreamSanitizer`。
  - `DeltaChannel.Thinking` 专用于 UI 状态反馈，`SectionChannelRouter` 与 `ExplanationParser` 完全对该通道免疫，根治污染。

### 3.3 模块二：译文区就地节流刷新与正文无缝衔接交互

修改 `src/NaniKore/App/ResultPanelWindow.xaml.cs`：
- **译文区思考占位呈现（视觉焦点）：**
  - 维护 `StringBuilder _thinkingBuffer`、`long _lastThinkingUpdateTick` 以及标志位 `bool _isDisplayingThinking`。
  - 当收到 `delta.Channel == DeltaChannel.Thinking` 时：
    - 追加到 `_thinkingBuffer`；
    - 检查当前时间戳距离上次刷新是否 `>= 1200ms`（约 1.2 秒）：
      - 若达到阈值，提取 buffer 最新一段非空文本，清理换行符并保留前 60 个字符（添加省略号）；
      - 直接更新**译文文本框**：`TranslationTextBox.Text = $"💭 [思考中] {cleanSnippet}...";`
      - 置 `_isDisplayingThinking = true`；
      - 更新 `_lastThinkingUpdateTick`。
- **无缝流式衔接（正式正文到达）：**
  - 当收到首个正式译文增量（`delta.Channel == DeltaChannel.Translation`）时：
    - 若 `_isDisplayingThinking == true`，立即清空 `TranslationTextBox.Text = string.Empty`，并将 `_isDisplayingThinking = false`；
    - 随后将正式译文的第一批字符写入并正常流式追加！
    - 用户看到的是：译文框里先是思考动态在实时更新，一旦思考完成，正文立刻覆盖思考提示开始打字，**零视线转移、零残留、体验极度丝滑**。
  - 若流式异常结束或被取消：清理思考 buffer 并恢复正常提示。

### 3.4 模块三：设置页“启用思考”开关与多厂商 API 参数注入

1. **配置扩展（`AppConfig.cs`）：**
   - `ProviderProfile` 增加字段（正向命名，默认关闭）：
     ```csharp
     /// <summary>是否启用模型思考链（Reasoning / Thinking）；默认关闭以追求快速响应与节省 Token。</summary>
     public bool EnableThinking { get; set; } = false;
     ```
2. **设置界面（`TranslationPage.xaml`）：**
   - 在“服务商”分组中新增开关行：
     - **Header**：“启用思考”
     - **Description**：“允许具备推理能力的大模型进行深度思考（将在译文区实时展示思考进度）；关闭时通过 API 参数尝试禁用思考以加速响应。无论是否启用，正式正文均会自动隔离思考链。”
     - **控件**：`ToggleButton`（`Style="{DynamicResource ToggleSwitch}"`，默认 unchecked）。
3. **多厂商 API 参数自适应注入与带外思考流防御规则：**
   - **`OpenAiProvider`（区分 Responses 与 Completions 两套独立 API，防范 HTTP 400 校验错误）**：
     - **分支 A（新版 Responses API，`ApiType == "responses"`）**：
       - 严禁下发根级 `enable_thinking` 或 `reasoning_effort`（官方 Responses API 不支持且会报 400）；
       - 若 `EnableThinking == true` 且模型名匹配推理模型（以 `o1`、`o3` 等开头），注入 `"reasoning": { "effort": "low" }` 对象；
       - 若 `EnableThinking == false`，不传 `reasoning` 配置（或设为 `"effort": "none"`，视模型支持度）。
     - **分支 B（Chat Completions API，`ApiType == "completions"`）**：
       - **官方 OpenAI 端点（`Profile.BaseUrl` 指向 `api.openai.com`）**：
         - 严禁下发非标参数 `enable_thinking`（避免官方严格 Schema 校验报错 `Unrecognized request argument supplied: enable_thinking`）；
         - 仅当 `EnableThinking == true` 且模型为 `o1`/`o3` 时注入 `"reasoning_effort": "low"`；对于 `gpt-4o` 等普通模型严禁下发 `reasoning_effort`。
       - **第三方兼容端点（通义千问 Qwen、百炼、SiliconFlow、DeepSeek、Ollama 等）**：
         - 注入各大服务商通用开关：`enable_thinking = Profile.EnableThinking`；
         - 针对 DeepSeek 官方兼容规范同时挂载：`thinking = new { type = Profile.EnableThinking ? "enabled" : "disabled" }`；
       - **带外流式提取补完**：在 `ExtractCompletionsDelta` 中增加对 DeepSeek 特有的 `choices[0].delta.reasoning_content` 的识别，直接作为 `DeltaChannel.Thinking` 派发。
   - **`ClaudeProvider`（区分 Extended Thinking 支持状态，严禁无差别 disabled）**：
     - `EnableThinking == false` 时：**完全省略 `thinking` 参数**（切勿下发 `{"type": "disabled"}`，否则不支持思考的模型如 Claude 3.5 Sonnet / Haiku 将直接遭遇 400 拒绝）；
     - `EnableThinking == true` 时：若模型包含 `claude-3-7` 等支持思考的模型，注入 `"thinking": { "type": "enabled", "budget_tokens": 1024 }`（需保证 `max_tokens > budget_tokens`）；
     - **带外流式提取补完**：Claude 思考内容由独立的 `thinking_delta` 事件派发（内容在 `delta.thinking`），扩展 SSE 帧解析器提取该字段并直接作为 `DeltaChannel.Thinking` 派发。
   - **`GeminiProvider`（纠正 JSON 嵌套层级与 Part 标记识别）**：
     - **参数位置规范**：严禁置于根节点（会导致 400 报错），必须嵌套于 `generationConfig.thinkingConfig` 内；
     - `EnableThinking == false` 时：注入 `"generationConfig": { "thinkingConfig": { "thinkingBudget": 0 } }`；
     - `EnableThinking == true` 时：注入 `"generationConfig": { "thinkingConfig": { "thinkingBudget": -1 } }`（对 Flash Thinking 启用自适应思考）；
     - **带外流式提取补完**：Gemini 思考内容以 Part 的 `thought: true` 标记返回（无 `<think>` 标签），解析 `candidates` 时分离带 `thought: true` 的文本，分流至 `DeltaChannel.Thinking`，避免作为正文穿透污染业务流。

---

## 4. 动哪些、验哪些

### 4.1 必须改的代码
- `src/NaniKore/Translation/UnderstandContracts.cs`：枚举 `DeltaChannel` 增加 `Thinking` 项。
- `src/NaniKore/Translation/ThinkingStreamSanitizer.cs`：新增带内 `<think>` 标签流式状态机。
- `src/NaniKore/Translation/Providers/TranslationProviderBase.cs`：集成 Sanitizer。
- `src/NaniKore/App/ResultPanelWindow.xaml.cs`：增加译文区思考占位节流刷新与正文到达无缝替换逻辑。
- `src/NaniKore/Config/AppConfig.cs`：`ProviderProfile` 增加 `EnableThinking`（默认 false）字段与克隆逻辑。
- `src/NaniKore/App/Settings/TranslationPage.xaml` / `.xaml.cs`：添加“启用思考”Toggle 绑定与保存校验。
- `src/NaniKore/Translation/Providers/OpenAiProvider.cs`：区分 completions 与 responses 请求体注入；支持 DeepSeek `reasoning_content`。
- `src/NaniKore/Translation/Providers/ClaudeProvider.cs`：安全注入 `thinking` 并支持 `thinking_delta` 流解析。
- `src/NaniKore/Translation/Providers/GeminiProvider.cs`：在 `generationConfig` 注入 `thinkingConfig` 并支持 `thought: true` 标记分流。

### 4.2 需要验的用例（`SelfTest.Translation.cs`）
- `TestThinkingStreamSanitizer_BasicIsolation`：验证 `<think>思考内容</think>原文` 能正确分离出 Thinking 增量与干净的原文。
- `TestThinkingStreamSanitizer_ChunkSplitting`：验证跨数据包切分的标签（如 `<thi` + `nk>` 与 `</thi` + `nk>`）能被正确缓冲与剥离。
- `TestThinkingStreamSanitizer_UnclosedStream`：验证思考流异常中断未闭合时，不向业务通道泄漏内容。
- `TestThinkingStreamSanitizer_ExplainIntegration`：验证精讲阶段包含思考链时，微结构化卡片依然完美解析。
- `TestResultPanel_ThinkingToTranslationTransition`：验证思考占位文本在译文到达时被瞬间无缝替换。
- `TestProviderThinkingParameters_OpenAiOfficial`：验证官方 OpenAI 在 completions/responses 下不传非法参数，o 系列模型按需注入 low effort。
- `TestProviderThinkingParameters_OpenAiCompatible`：验证第三方兼容端点注入 `enable_thinking` 及 DeepSeek `thinking` 对象。
- `TestProviderThinkingParameters_ClaudeAndGemini`：验证 Claude 禁用时省略参数、Gemini 参数嵌套在 `generationConfig` 内。
- `TestProviderOutOfBandThinkingExtraction`：验证 DeepSeek `reasoning_content`、Claude `thinking_delta` 与 Gemini `thought: true` 均正确输出为 `DeltaChannel.Thinking`。
- 运行 `./scripts/dev.ps1 test` 全量回归无 UI 自测试。

---

## 5. 执行步骤

- **Step 1（核心过滤穿刺）**：实现 `ThinkingStreamSanitizer` 与 `DeltaChannel.Thinking`，集成到 `TranslationProviderBase`，并编写完整的单元测试覆盖。
- **Step 2（UI 译文区无缝刷新）**：在 `ResultPanelWindow` 中接入 `DeltaChannel.Thinking`，实现译文区 1.2 秒节流刷新及正文到达瞬间无缝切换。
- **Step 3（配置与 API 下发）**：在 `ProviderProfile` 与 `TranslationPage` 中完成“启用思考”（默认 false）开关呈现与存储，适配各大 Provider 请求体（含 `enable_thinking`）。
- **Step 4（全量自测回归）**：运行 `./scripts/dev.ps1 test`，确保所有既有功能与新增测试 100% PASS。

