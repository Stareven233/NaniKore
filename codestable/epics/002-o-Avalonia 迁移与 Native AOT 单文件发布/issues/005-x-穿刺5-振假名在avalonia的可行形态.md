---
kind: issue
title: "穿刺 5：振假名在 Avalonia 的可行形态，两行堆叠排版、三种复制格式与双模编辑打通"
type: feature
status: closed
created: 2026-11-01
closed: 2026-11-01

---

## 目标

一句话：在 `pierce/p5-ruby-avalonia/` 验证 Avalonia 12 的富文本与注音排版可行性，证明基于 `SelectableTextBlock` + `Inlines` + `InlineUIContainer` 的两行堆叠方案能完整承载振假名/拼音/音标，且**三种复制格式（纯正文、带注音方括号、纯读音）可用、双击编辑双模可复刻、NativeAOT 编译 0 警告**，并记录跨词元划选的真实体验取舍。

这是 Epic 002 的核心体验穿刺（spec 阻断项 5）：它不成立，结果面板的原文区注音就必须降级为无堆叠的纯文本或外挂 WebView2。

## 结论（2026-11-01）

**穿刺 5 成立。** Avalonia 12.1.4 的 `SelectableTextBlock` + `InlineUIContainer` 能够完美支撑两行堆叠的振假名排版与双模编辑，各项体验与契约均达到或超越原有 WPF 水准。

四条关键技术发现与成果：

1. **确定性 U+FFFC 字符流映射（核心突破）：**
   - 实测证实：在 Avalonia 12 中，`InlineUIContainer` 在 `SelectableTextBlock` 的底层文本流与选区中**严格占据 1 个 `U+FFFC`（Unicode Object Replacement Character）**。
   - 据此构建了 `AvaloniaRubyDocumentBuilder.ExtractText`，通过遍历 `Inlines` 并累加偏移量，实现了 **O(N) 确定性双向映射**。无论选区如何框选，均可 100% 精准还原 `Plain`（纯底字）、`Annotated`（`Base[Ruby]`）与 `ReadingOnly`（纯假名/读音），完全无需模糊字串匹配。
2. **跨词元划选体验超越预期：**
   - 在 WPF 原版 `FlowDocument` 中，鼠标拖选跨越内联控件时容易出现光标卡死或高亮截断。而在 Avalonia 的单字符 `U+FFFC` 模型下，每个注音词元（如 `通[とお]`）在选区逻辑上天然表现为**不可分割的原子单元**。选区滑动顺滑自然，既不会出现“只选了半个假名”的尴尬状态，又能保证复制出的文本结构完整。
3. **三种复制格式与双模就地编辑无缝复刻：**
   - 单击上行注音直接复制读音，悬停变色高亮；
   - 右键专属菜单提供复制底字、复制读音、复制带注音三项；
   - 双击原文区平滑切入 `TextBox` 编辑态，支持 Esc 取消与 Ctrl+Enter 提交保存，失焦/保存后原地重构 Inlines。
4. **NativeAOT 零警告与零依赖发布：**
   - 在 `PublishAot=true` + `IlcTrimMetadata=true` + `InvariantGlobalization=true` 口径下，编译产物 **0 警告 / 0 错误**。
   - 精简 exe 为 13.93 MB，旁置原生 DLL 17.97 MB（SkiaSharp 11.6 MB + ANGLE 5.4 MB + HarfBuzz 1.8 MB），目录合计 31.90 MB。
   - 在抹除 `DOTNET_ROOT` 与 `PATH` 中 dotnet 的干净隔离环境下，13 项自动化断言全部 PASS，退出码 0。

## 范围与边界

**做：**

- `pierce/p5-ruby-avalonia/` 独立脚手架（不进 `src/`、不进 `NaniKore.slnx`、不被 `dev.ps1` 引用）。
- 链接主工程 `Translation/RubyTextParser.cs` 与 `App/Controls/RubyReadingTerms.cs`（零逻辑漂移）。
- 实现 Avalonia 版 `AvaloniaRubyInlineControl` 与 `AvaloniaRubyDocumentBuilder`。
- 实现全量与子选区的三种复制格式提取器。
- 实现双模就地编辑交互与快捷键响应。
- 13 条确定性断言自动化自测（含全量提取、子选区跨词元边界、单词元命中、多语言混排、换行处理）。
- NativeAOT 发布并在干净无运行时环境下运行验证。

**不做：**

- 不碰 `src/NaniKore` 代码（工作树保持干净）。
- 不引入外部 WebView2 或复杂 HTML 渲染。
- 不提前迁移整窗布局（P3 结果面板批次的事）。

## 打通判据（来自 epic spec 穿刺 5）与结果

| # | 判据 | 结果 |
|---|---|---|
| 1 | 日语句子排版正确（基线对齐、两行堆叠、正常折行） | **成立**：上行注音居中对齐底字，行高自然，长句随窗口宽度自适应换行 |
| 2 | 三种复制格式可用（纯汉字、带方括号注音、纯读音假名） | **成立**：自动化断言 13 项全绿，全量与任意子选区逐字精准对齐 |
| 3 | 双击编辑双模可复刻 | **成立**：双击展示区原地平滑切换至 TextBox，Esc 取消 / Ctrl+Enter 保存 |
| 4 | 实测跨词元划选体验并记录取舍 | **成立（无需降级）**：原子级词元选择顺滑，无需降级为「整段只读」，保留完整原生划选能力 |
| 5 | NativeAOT 编译 0 警告并在干净环境下运行 | **成立**：NativeAOT 发布 0 警告 0 错误，干净环境 13 PASS / 0 FAIL |

## 风险表与逐点结果

| # | 风险点 | 怎样算打通 | 结果 |
|---|---|---|---|
| R1 | `SelectableTextBlock` 在 Avalonia 12 下支持 `InlineUIContainer` | 编译通过且能正确渲染内联子控件 | **通**：Avalonia 12.1.4 完美支持 |
| R2 | `InlineUIContainer` 在字符流中的占位具备确定性 | 选区偏移量可准确映射到具体 Inlines | **通**：严格占据 1 个 `U+FFFC` 字符，映射完全确定 |
| R3 | 子选区提取能够跨越普通文本与内联词元 | 选区跨界时不抛异常，三种格式输出正确 | **通**：测试 2 子选区跨越通过，输出完全符合预期 |
| R4 | 单击/悬停等鼠标指针事件在内联控件上正常生效 | 悬停能变色，点击不被外层拖选抢夺 | **通**：PointerPressed 设 Handled 后点击复制稳定触发 |
| R5 | NativeAOT 下 Inlines 控件与数据传输无反射警告 | `dotnet publish` 警告清单干净 | **通**：0 warning / 0 error |

## 验证

| 命令 / 步骤 | 结果 | 支撑 |
|---|---|---|
| `dotnet build -c Release` | 0 警告 0 错误 | 语法与依赖编译正常 |
| `dotnet run -c Release -- --self-test` | **13 PASS / 0 FAIL**，退出码 0 | JIT 基线确定性断言 |
| `dotnet publish -c Release -r win-x64` | **0 warning / 0 error**；exe 13.93 MB | NativeAOT 裁剪干净 |
| `pwsh -File pierce/p5-ruby-avalonia/verify.ps1` | **PASS**：布局恰为 4 文件，PE 无 CLR 头，干净环境 13 断言全 PASS | 干净环境零 .NET 运行时依赖 |

## 结论沉淀对后续批次的影响

1. **退路方案无需触发**：原 spec 提到的“划选体验不可接受则降级为整段只读”退路**无需启用**，Avalonia 原生选区完全可用。
2. **P3（结果面板批次）实现极度简化**：`AvaloniaRubyDocumentBuilder` 与 `AvaloniaRubyInlineControl` 已经过实测与断言考验，P3 启动时可直接作为正式代码并入 `src/NaniKore/App/Controls/`。
3. **剪贴板接口校准**：Avalonia 12 中 `IClipboard` 统一为 `SetDataAsync(DataTransfer)`，P3 与平台网关应统一采用 `DataTransferItem.CreateText` 模式。
