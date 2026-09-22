---
kind: issue
title: "语言插件门控改独立特征位，日英混排同享振仮名与 IPA 规约"
type: ff
status: closed
priority: normal
created: 2026-09-24
---

# 语言插件门控改独立特征位，日英混排同享振仮名与 IPA 规约

> **读者：** 负责 `LanguageDetector` / `PromptTemplates` 的开发者。
> **上游：** `codestable/epics/001-o-学习型演进-精讲假名与生词库/issues/001-o-多语言提示词架构与智能字集探测.md`（其验收明确要求"英文及日英混合句子都能正确拼接对应插件"——该要求当前实际未达成，本文收口）。
> **下游：** `009-x-ff-vision-prompt-plugin-composition.md` 复用同一批插件常量，依赖本文的英语文案修正。
> **结论：** 已按推荐方案落地（2026-09-24），文末附实施记录；009 的前置依赖（英语插件文案）由此解除。

## 背景与问题

当前注入条件（`PromptTemplates.cs` 两处同构：`BuildModularSystemPrompt` 与 `BuildExplainSystemPrompt`）：

```csharp
if (detection.NeedsJapaneseRuby || detection.HasJapanese) { /* 注入日语插件 */ }
if (detection.NeedsEnglishIpa || (detection.HasEnglish && !detection.HasJapanese)) { /* 注入英语插件 */ }
```

`!HasJapanese` 守卫的用意是防止日语文本里的片假名外来语（ラーメン）与商品名被误标 IPA。**代价**：日英混排句只注入日语插件，英语部分无任何 IPA 指引——`Appleの新しいiPhone発表会`（自测已有样例，`SelfTest.Translation.cs:278`）实际只拿到振假名规则。

**测试为何放过：** `TestLanguageDetector` 第 6 例只断言探测位（`HasJapanese && HasEnglish && NeedsJapaneseRuby`），没有任何**组装级**断言钉住"混排句的 system prompt 同时含两种规约"。缺口在探测层不可见，只在组装层显形。

## 方案（推荐）：职责分离——探测器选规则，模型选适用范围

1. **门控独立化**（两处 Build 方法同步改，保持同构）：

```csharp
if (detection.NeedsEnglishIpa || detection.HasEnglish) { /* 注入英语插件 */ }
```

2. **英语插件文案限定文字类型**（噪声防护从"门控"移到"文案"，一处修改同时服务三条路径与 009 的视觉复用）：

```csharp
// 现状
- 为原文里少见、困难的单词用方括号标注国际音标（IPA），如 apple[ˈæpl]
// 改为
- 为原文中拉丁字母拼写的少见、困难单词用方括号标注国际音标（IPA），如 apple[ˈæpl]
```

片假名外来语（ラーメン）、日文汉字均不在"拉丁字母拼写"范围内，自然不会误标；守卫随之失去存在理由。

3. **补组装级自测**（关闭测试盲区）：
   - `Appleの新しいiPhone発表会` → system prompt 同时含 `漢字[かな]` 与 `国际音标（IPA）`；
   - 少量拉丁混入（如 `ゲームSTART`）也注入英语插件（可接受，规则条件式不误伤）；
   - 纯简体中文句（`今天天气很好…`）仍**不**注入日语插件——探测的"排除"价值正在于此，不得破坏。

## 明确不做

- **分段语言探测 / 分段注入**：每段一套插件会使系统提示词变体爆炸、击穿前缀缓存；模型对各条件式规则本就能逐 token 取舍，收益趋近于零。
- **取消日语门控让所有输入都戴日语插件**：纯简体中文会被振假名误标日语读音，"探测决定纳入哪些规则"的排除逻辑必须保留。

## Token 与缓存影响

- TokenEstimator 口径（按 009 联动变更后：对齐块已删除、Base 含内联目标语言句）：Base ~155 / 日语插件 ~80 / 英语插件 ~42。
- 日英混排请求：现状（仅日语插件）~243 → ~277（+34 tokens，其中英语插件含新文案），前缀缓存命中后摊薄；系统提示词变体仍 ≤4（none / en / ja / ja+en），不新增变体。
- 联动提示：`ExplainRolePrompt` + `ExplainStructureRule` 合并、两个对齐常量删除均属 009 的联动变更；本 issue 只动门控与英语文案，若 009 先落地则以合并后的字面量为验收基准。
- 扩展性：未来扩韩语只需 `LanguageFeatures.Korean` + `KoreanPlugin` + 一个独立门控，组装架构不动——"探测到一种语言就加一种语言的提示词"即按此方向演进。

## 验收标准

- [x] `BuildModularSystemPrompt` 与 `BuildExplainSystemPrompt` 门控独立化且行为一致（英语条件 = `NeedsEnglishIpa || HasEnglish`）
- [x] `EnglishPlugin` 文案含"拉丁字母拼写的"限定；纯日语/纯中文场景无 IPA 误标回归
- [x] 新增组装级自测：日英混排同享两规约；纯简体中文仍无日语插件；自定义插件覆盖行为不变
- [x] `./scripts/dev.ps1 test` 全绿（268 PASS / 0 FAIL）
- [x] 若 009 已落地，视觉路径自动获得正确噪声防护（无需额外改动，回归即可）

## 备注

- 已知边界（本次不收治，记录在案）：纯汉字启发式（简体偏旁 + 高频字表）对全部由中日共用字组成的简体句（如"手机拍照""使用方法"）在目标语言为中文时误判日语、误注入振假名。这是为保「進入禁止」类词召回付出的代价，方向可接受；如未来要收紧，再做二维判据（共用字句中假名占比等），不在本 issue 范围。
- 探测器的空文本兜底逻辑（目标语言为中文时兜底推荐日语插件）只服务文本路径，视觉路径不经过探测，本 issue 不影响它。

---

## 做了什么（2026-09-24）

1. **门控独立化**（`PromptTemplates.BuildModularSystemPrompt` 与 `BuildExplainSystemPrompt` 同步改，保持同构）：英语条件由 `NeedsEnglishIpa || (HasEnglish && !HasJapanese)` 改为 `NeedsEnglishIpa || HasEnglish`，两处各补中文注释说明"守卫职责已移交文案"。日语门控未动——探测的"排除"价值（纯简体中文不注音）必须保留。
2. **噪声防护移交文案**：`EnglishPlugin` 条目改为"为原文中**拉丁字母拼写的**少见、困难单词用方括号标注国际音标（IPA）"。片假名外来语（ラーメン）与日文汉字均不在该范围内，守卫随之失去存在理由；一处修改同时服务文本两条路径与 009 的视觉复用。
3. **顶层 doc 注释同步**：探测机制段"纯拉丁字母 → 英语"改为"含拉丁字母 → 英语"，新增"插件门控职责分离"段（探测器只决定纳入哪些规约，适用范围交由文案限定），两条路径的条件注入措辞由"探测到英语且未判为日语时"改为"含拉丁字母时"。
4. **关闭测试盲区**：`SelfTest.Translation` 新增组装级 `TestMixedLanguagePluginGating`（6 项断言），`TestLanguageDetector` 第 6 例补 `!NeedsEnglishIpa`——把"混排句的英语规约由 `HasEnglish` 门控携带"这一口径钉死。覆盖：日英混排（翻译 + 精讲两条路径同享两规约）、少量拉丁混入（`ゲームSTART`）、纯日语（有振仮名无 IPA）、纯简体中文（两者皆无）、自定义插件在混排句仍优先。

### 验证

- `./scripts/dev.ps1 test`：268 PASS / 0 FAIL（新增 6 项断言全绿，无既有断言回归）。
- 组装层抽查：`Appleの新しいiPhone発表会` → Base + 日语插件 + 英语插件 + 目标语言约束；`ラーメンは美味しい` → 无英语插件；`今天天气很好，我们去散步` → 无任何插件。

### 影响面

- 系统提示词变体数不变（none / en / ja / ja+en），前缀缓存不新增变体；日英混排请求约 +34 tokens（含新文案），命中后摊薄。
- 009 的英语文案前置依赖已满足；`LanguageAlignmentRule` / `ExplainRolePrompt` / `ExplainStructureRule` 的删除属 009 联动变更，本 issue 未触碰。
