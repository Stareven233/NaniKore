---
kind: issue
title: "设置窗口未保存关闭时的尺寸记忆仍可能用旧快照覆盖外部配置更新"
type: ff
status: closed
priority: normal
created: 2026-09-22
---

# 设置窗口未保存关闭时的尺寸记忆仍可能用旧快照覆盖外部配置更新

> **读者：** 下一轮负责设置窗口尺寸记忆与配置写盘冲突的开发者。
> 主人已同意将本问题单开；开新会话时直接指定本文档即可获得完整上下文。
> **关联文件：**
> - `src/NaniKore/App/Settings/SettingsWindow.xaml.cs`（`SaveWindowSize` / `_initialConfig` / `_settingsSaved`）
> - `src/NaniKore/App/NaniKoreApp.cs`（`ApplySettings`、结果卡 `AutoExplainChanged` 写盘入口）
> - `src/NaniKore/Config/ConfigStore.cs`（`AtomicWrite` 单一写盘入口）

---

## 背景：前两轮已完成的修复

本 issue 是同一轮设置窗口/config.yaml 排障的**剩余边界**，前两个问题已修复：

1. **`prompts` 段塌缩成 `{}`**：YAML `~` 反序列化为 null，`ConfigStore` 序列化器配置了 `DefaultValuesHandling.OmitNull`，回写时 5 个提示词子键整体被省略。已在 `ConfigStore.Normalize()` 把 null 收敛为空串（`config.Prompts.* ??= string.Empty`，连同 `Hotkeys.TranslateClipboard`）。**已提交：`a82a82a`**。
2. **“什么都不改、直接关闭”也重写 config.yaml**：`SettingsWindow` 关闭时曾无条件回写。已改为纯决策 `ResolvePersistedSize`：尺寸与基线一致（1px 容差）或尺寸不可信时完全零写入；仅真的拖过窗口才写回尺寸。已用真实 `SettingsWindow` 端到端验证：直接关闭 → 0 次写盘、文件逐字节不变。**当前在暂存区待提交**（`SettingsWindow.xaml.cs` + `SelfTest.Ui.cs`）。

## 问题描述

**当前行为：** 用户在设置窗口里**没有点保存**，但**真的改变了窗口尺寸**，然后关闭。此时 `SaveWindowSize()` 以**打开时的快照 `_initialConfig`** + 新尺寸整份写盘（设计意图是“未保存就丢弃页面编辑”）。

**缺陷：** 如果从打开设置窗到关闭这段时间里，**其他入口**改写了配置——目前已知的是结果面板的 `AutoExplainChanged` → `NaniKoreApp.ApplySettings` → `AtomicWrite`——那么这次尺寸回写会拿**旧快照整体覆盖**磁盘上**较新**的配置，导致该入口的修改被静默回滚。

**预期行为：** 尺寸记忆只应作为“叠加项”落在**当前最新配置**之上；页面编辑依旧被丢弃。即写盘内容 = 实时当前配置 + 新窗口尺寸，而不是打开时的历史快照 + 新窗口尺寸。

**影响面：** 低概率但静默丢数据。触发需要同时满足：设置窗未保存、期间真的拖过设置窗尺寸、期间其他入口恰好改过配置。目前只有结果卡的 auto_explain 开关这一条其他写盘路径。

## 复现步骤

1. 启动应用，打开结果面板（任意一次截图翻译后）
2. 从托盘或结果卡打开设置窗口，**不做任何修改、不点保存**
3. 切换回结果面板，拨动 `AutoExplainToggle`（该入口会立即写盘）
4. 回到设置窗口，**拖一下窗口边缘改变尺寸**，然后点关闭（不点保存）
5. 检查 config.yaml：`general.auto_explain` 被回滚成步骤 2 快照里的旧值

## 修复方向

### 推荐方案：把“打开时快照”改成“实时当前配置引用”

- `SettingsWindow` 构造函数当前接收 `AppConfig current`（快照）。改为接收 `Func<AppConfig>` 实时提供者（宿主传 `() => _configStore.Current`）。
- `_initialConfig` **保留**：仍用于“未保存则丢弃页面编辑”的判定基线（页面脏检查仍以打开时为准）。
- `SaveWindowSize()` 在“未保存 + 尺寸变了”分支改为克隆**实时当前配置**，只叠加 `Windows` 尺寸后写盘。
- 注意区分两条写盘语义：业务保存（`Save()` → 页面编辑生效）与纯尺寸回写（不得夹带任何业务字段）。

### 备选方案：宿主侧合并识别

- `NaniKoreApp.ApplySettings(next)` 识别“内容与当前落盘配置一致、仅 `Windows` 不同”的调用，只把尺寸字段合并进 `Current` 再写盘。不动 `SettingsWindow` 构造签名，但把策略泄漏进了宿主，判等成本也更高。

**倾向推荐方案**：语义最干净，能根除整类“快照过期”问题，不止 auto_explain 一条路径。

## 验收标准

- [x] 上述复现步骤 5 中，`general.auto_explain` 保持步骤 3 修改后的值，同时 `windows.settings_*` 记录为新尺寸（已用真实 `SettingsWindow` 端到端验证，见下）
- [x] 未保存 + 改尺寸关闭时，页面编辑（配置档、提示词、热键、通用开关）依旧全部丢弃
- [x] `./scripts/dev.ps1 test` 全绿。**基线确认（已实测）：`a82a82a` 与 `8c15405` 两个提交上均有 13 项提示词相关失败，失败集完全一致**（`prompt literal * is compact...` × 8、`assembled system prompt...` × 4、`empty prompt config strictly builds...` × 1）——issue 作者当时仅记录了其中 1 项。主人指示顺手收治，**13 项已全部修复，当前 257 项 PASS / 0 FAIL**（详见文末附记）。
- [x] 为“外部入口修改 + 尺寸回写不覆盖”补一条回归自测（参照现有 `TestSettingsCloseSizeMemory` / `TestEmptyPromptKeysSurviveRewrite` 的写法）

---

## 做了什么（2026-09-22，改动暂存于工作区，待主人审阅后另行提交）

按推荐方案落地：

1. **`SettingsWindow` 构造函数改收 `Func<AppConfig> current` 实时提供者**，宿主两处传入 `() => _configStore.Current`（`NaniKoreApp.ShowSettings`）与 `() => store.Current`（`Program.PreviewSettings`）。`_initialConfig` 字段随之移除——修复后已无引用，且 `TreatWarningsAsErrors=true` 下未用私有字段会触发 CS0414 编译失败。
2. **新增纯决策 `ResolveCloseWriteBackBaseline(settingsSaved, working, latest)`**：已保存过业务配置时以 `_working` 为基线（内容已随保存落盘，只叠加尺寸）；未保存时必须取 `latest` 实时配置。`SaveWindowSize()` 未保存分支改用它，页面编辑依旧只存在于 `_working`，选用 `latest` 即天然丢弃，纯尺寸回写不夹带任何业务字段。
3. **回归自测 `SelfTest.Ui.TestSettingsCloseSizeWriteBackKeepsExternalUpdates`**（5 项断言）：模拟“打开 → 页面编辑未保存 → 外部入口 `AtomicWrite` 改写 `auto_explain` → 未保存 + 改尺寸关闭”，验证基线取实时配置、外部更新存活、新尺寸落盘、页面编辑丢弃、已保存路径不回退。

## 怎样验证

- **真实窗口端到端**（issue 复现步骤 3–5）：以真实 `SettingsWindow` + 临时 `ConfigStore` 走完整流程——窗口打开后经外部入口把 `auto_explain` 改成 `true` 并写盘，再拖尺寸至 1024×700、不点保存直接关闭。结果：`Closed` 触发恰好 1 次写盘，落盘 `auto_explain=true` + `1024×700`（旧行为会写 `auto_explain=false`）。
- `./scripts/dev.ps1 test`：244 项 PASS；13 项 FAIL 均为上述提示词既有基线，与本 issue 无关。

## 备注

- 前两轮修复引入的自测（`SelfTest.Config.TestEmptyPromptKeysSurviveRewrite`、`SelfTest.Ui.TestSettingsCloseSizeMemory`）已验证：直接关闭零写盘、`~` 不再塌缩、真改尺寸仍能记忆。本 issue 只收治“未保存 + 改尺寸”这一条写盘路径的数据新鲜度。
- WPF 实测结论（2026-09-22）：`Window.ActualWidth == Window.Width`，多轮开关注销零漂移，尺寸判定容差 1px 足够——本 issue 无需改动该部分。

---

## 附记：13 项提示词失败收治（主人 2026-09-22 指示，随本 issue 工作区一并落地）

实测确认 `a82a82a` / `8c15405` 上失败集完全一致（13 项），分两个根因：

1. **12 项（8 literal + 4 assembled）——CRLF 行尾结构性问题**：仓库 `core.autocrlf=true`，工作区源码检出为 CRLF，多行原始字符串字面量原样继承 `\r\n`，违反“零 CR”契约。修法：`PromptTemplates` 新增 `PromptLiteral()` 行尾归一化闸门，8 个多行字面量及其别名统一经此声明（`const` → `static readonly`），源码顶格排版与最终下发值彻底解耦，此后编辑器/检出策略如何漂移都不再复发。
2. **1 项（`empty prompt config strictly builds...`）——契约漂移**：`a82a82a` 改写 base 提示词时丢掉了角色首句“你是准确、自然的翻译引擎”，而自测仍钉着该短语。已在 `DefaultTranslationBasePrompt` 开头补回该行。

修复后全量 257 项 PASS / 0 FAIL。遗留可选项：`DefaultVisionOneshotPrompt` 同样在 `a82a82a` 丢了角色首句“你是准确、自然的视觉翻译引擎”，无测试钉住，本次未动，是否补回由主人定。
