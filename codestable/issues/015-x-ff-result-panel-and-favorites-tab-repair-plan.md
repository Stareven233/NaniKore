---
kind: issue
title: "修复计划：划词气泡收藏失效、精讲区域布局裁切、词汇注音重复与收藏去重，及设置面板收藏 Tab 改造"
type: ff
status: in-progress
priority: high
created: 2026-09-29
updated: 2026-09-29
---

# 修复计划：划词气泡收藏失效、精讲区域布局裁切、词汇注音重复与收藏去重，及设置面板收藏 Tab 改造

> **读者：** 负责结果面板与设置面板收藏 Tab 的开发者。  
> **关联文件：**  
> - `src/NaniKore/App/ResultPanelWindow.xaml` / `.xaml.cs`  
> - `src/NaniKore/App/Controls/SelectionBubblePopup.xaml` / `.xaml.cs`  
> - `src/NaniKore/App/Controls/ExplanationCardView.xaml` / `.xaml.cs`  
> - `src/NaniKore/Translation/ExplanationParser.cs`  
> - `src/NaniKore/Translation/PromptTemplates.cs`  
> - `src/NaniKore/Translation/RubyTextParser.cs` / `LanguageDetector.cs`  
> - `src/NaniKore/Favorites/FavoriteContracts.cs` / `SqliteFavoriteService.cs`  
> - `src/NaniKore/App/Settings/FavoritesPage.xaml` / `.xaml.cs`  
> - `src/NaniKore/App/Settings/GeneralPage.xaml.cs` / 共享 `LanguageChoices`  
> - `src/NaniKore/App/Theme/Controls.Base.xaml`（badge 样式共享化）  
> - `src/NaniKore/Diagnostics/SelfTest.Ui.cs` / `SelfTest.Explanation.cs` / `SelfTest.Favorites.cs`

---

## 0. 总览与主人已拍板的决策

| # | 问题 | 性质 | 严重度 | 主人决策 |
| --- | --- | --- | --- | --- |
| 1.1 | 划选后点击气泡无法收藏 | 交互失效，**根因已大幅收窄** | 高（核心交互断链） | 先确认根因再修；主人已做两轮实测 |
| 1.2 | 默认窗口高度下看不到/滚不到精讲底部 | 布局裁切 | 高 | **方案 A：整个内容区一起滚动** |
| 1.3 | 词汇注音重复：`気付く[きづく] (きづく)` | 提示词缺口 + 解析层不去噪，模型随机性触发 | 中 | 提示词、解析器、展示层三层设防（含语法卡片 `Pattern`） |
| 1.4 | 收藏项因注音残留产生重复行 | 数据脏 | 中 | **不做历史合并**；陈列卡片时归一化匹配点亮星标 + 入库归一化，阻止后续重复 |
| 2.1 | 数据库位置：需单行描述 + 复制支持 | 信息架构调整 | 低 | 取消独立区域，单行放**导航条下方** |
| 2.2 | 检索与操作 → 顶部横向导航条；列表直铺；**搜索条填满剩余横向空间（主人 2026-09-29 晚追加）** | 信息架构调整 | 中 | 按此改（搜索条改占星号列，窄窗收敛而非换行） |
| 2.3 | 导航条增加语言筛选 | 新功能 | 中 | **方案 B：新增 language 列**；语言列表与设置页目标语言**共用一份且各用母语写法**；探测现算后映射回列表对应项；**旧行回填「日本語」仅针对已存在的收藏**；划词气泡允许用户调整 |
| 2.4 | 词性 badge 风格与精讲卡片统一 | 视觉一致性 | 低 | 只换样式（沿用 `category` 文案），共享精讲卡片徽章款式 |
| 2.5 | （新增）收藏条目事后编辑 | 功能补全 | 中 | 就地编辑，失焦即保存 + 成功提示，**允许修改 content 及其他字段** |

### 概念澄清（主人提问）

- **`DisplayTitle`**：`VocabularyCardViewModel` 的计算属性，仅用于卡片标题展示：有读音时渲染为 `词条 (读音)`（如 `通る (とおる)`），无读音时就是纯词条。它是**纯展示字段，不进收藏**——收藏落库用的是 `vm.Term`（词条本体），这正是防止收藏键变成 `気付く (きづく)` 而与正文划选得到的 `気付く` 对不上的关键设计。主人看到的信息重复正是 1.3 的 bug（Term 内嵌注音 + DisplayTitle 又追加一次）；1.3 修复后它只显示一次读音，保留即可，无需删除。
- **`Pattern`**：`ExplanationGrammarCard` 的第一列，即语法/句型卡片的「句型」名称，对应提示词行 `- **句型** | 语法功能与释义`，如 `～ない`、`〜そうだ`。它和词汇的 `Term` 对等，同样可能被模型塞入注音，1.3 里一并做剥离。

---

## 1. 翻译结果面板

### 1.1 划选后点击气泡无法收藏

**主人实测结论（2026-09-29）**

1. 点击气泡内任意位置（编辑框/收藏按钮）→ **气泡消失，并在鼠标点击处出现一个新气泡**；
2. 点类别下拉 → **下拉展开后闪烁消失**，同样弹出新气泡。

**机制重建**

两条路径都能完整解释观测，共同的核心是"**鼠标按下阶段气泡即被关闭**"：

- 关闭来源候选：②主窗 `Deactivated` 误判（点 PopupRoot 独立 HWND 抢激活，结果窗正好 `ShowActivated=False` + Topmost）或 ⑤ `Popup.StaysOpen="False"` 的外部点击误判（例如旧选区把鼠标捕获留在了文本框上，整次点击被判定在气泡子树之外）。
- "**新气泡**"的由来：关闭后 MouseUp 落到下层文本框上，`On*TextBoxMouseUp`（`handledEventsToo` 订阅）二次触发 → 选区仍在 → `ShowAt` 在**同一位置**再弹一个。下拉的"闪烁消失"则是 ComboBox 的下拉依赖被拆掉的气泡树，随父 Popup 一起销毁。
- 一个关键分支仍待区分：若捕获导致 MouseDown 被导回文本框，选区会被清空，`GetSelectionOrMouseRect` 会走"鼠标落点"回退分支——同样表现为"点击处冒出新气泡"。埋点日志里 `SelectionLength` 的值即可二分。

**阶段一收尾：埋点确认（主人已放行，见第 4 节 TODO 1.1-A）**

在上述路径 + 按钮入口 + 三个 MouseUp 处理器各写一条带 `HH:mm:ss.fff` 的日志，append 到 `%TEMP%/nanikore-bubble-debug.log`：

| 埋点 | 日志看点 |
| --- | --- |
| `Deactivated` / `LocationChanged` / `OnPanelPreviewMouseDown` / Esc 分支 | 哪条收起路径抢先 |
| `BubblePopup.Closed` | 关闭时刻与路径的先后 |
| `OnFavoriteClick` 入口 | 有没有这一行即按钮是否收到 Click |
| `OnTranslationTextBoxMouseUp` 等三处（带 `SelectionLength`） | len>0 = 旧气泡先关、选区未动；len=0 = 捕获清空了选区 |

确认后移除全部埋点。

**阶段二：修复（无论落在哪个候选都覆盖，四件套）**

1. ~~`BubblePopup` 改 `StaysOpen="True"`~~ → 保留（无害），但**不是本轮的根因**。
2. ~~`Deactivated` 处理器改为延迟判定~~ → 保留（无害）：实测 11/11 次复现里 `Deactivated` 从未触发。
3. ~~`ShowAt*` 打开前清理残留鼠标捕获~~ → 保留（无害）。
4. ~~收藏动作从 `Click` 前移到 `FavoriteButton.PreviewMouseLeftButtonDown`~~ → 保留（无害）。

> ### 2026-09-29 晚：第二轮实测翻案——根因不在上述四件套
>
> 主人用带埋点的版本复现 11 次，日志 11/11 全是同一条路径：`Popup.Closed` → `MouseUp translation len=3` → `Hide via PanelPreviewMouseDown` → 新气泡；`Deactivated` / `LocationChanged` / Esc 一条都没出现。`len=3` 说明划选残留捕获没有清空选区（候选 ⑤ 出局）。
>
> 用临时诊断探针（真实 Show 面板 + `SetCursorPos`/`mouse_event` 在气泡矩形中心合成真实点击 + 抓 `GetWindowRect`/`WindowFromPoint`/z-order）拿到铁证：
> - 气泡 PopupRoot 的 HWND **与面板窗口同在 topmost band，却排在面板之下**（`WindowFromPoint(气泡中心) = 面板 HWND`）；
> - 于是气泡与面板重叠的区域被面板窗口盖住：点气泡 = 点面板 → `OnPanelPreviewMouseDown` 收起气泡 → 同一位置的 MouseUp（`handledEventsToo`）选区仍完好 → `ShowAt` 在同址再弹一个，正是主人看到的「点气泡就消失并在点击处冒出新气泡」。
> - 面板窗口是 `Topmost=True` 且被 `SetWindowPos(HWND_TOPMOST)` 显式声明过（`BringToTopmostBand`），而 WPF 的 PopupRoot 虽自带 `WS_EX_TOPMOST`，在 topmost band 里却排在这之后声明的窗口之下。
>
> **真正的修复（已实施）**：打开气泡后把 PopupRoot 的 HWND 显式提升到 topmost band 最上沿（`SelectionBubblePopup.BringBubbleToTopmostBand`，与 `BringToTopmostBand` 对面板的声明同源）。
> **双保险（已实施）**：窗口侧所有收起路径（`OnPanelPreviewMouseDown`）与三个划词 MouseUp 不再因「点击实际落在气泡矩形内」而收起/重弹（`SelectionBubblePopup.ContainsScreenPoint`，物理像素与 DIP 已按 DPI 换算同口径）。
>
> **真实输入回归（探针实测，全绿）**：A 点气泡内「收藏」按钮 → 入收藏 + 300ms 反馈后收起、不冒新气泡；B 点气泡中部语言下拉 → 气泡保持打开且下拉展开；C 点面板其他区域 → 立即收起；D 面板已激活时点气泡空白边缘 → 保持打开、不冒新气泡。
>
> **终版复跑（适配 125% DPI 的真机环境，仍是 12/12 全绿）**：`WindowFromPoint(气泡中心)` 返回**气泡自己的 HWND**（0x003602C0），不再是结果面板（0x005005C4）——z-order 修复在 DPI 缩放下同样成立；收藏按钮点击被气泡窗口接收（`window-below = 气泡`），`favorited=True`、选区 3 字完好、300ms 反馈后干净收起不再生新气泡；语言下拉/面板别处/气泡空白边缘三项行为均符合预期。探针已按"临时诊断"约定从代码树移除。
>
> **回归标准（待主人实机验收）**：点气泡内编辑框、类别下拉、收藏按钮，气泡保持打开、**不产生新气泡**；三条划词入口的收藏/取消收藏、星标点亮、状态提示正常；点别处文字、拖动、Esc、切换到其他窗口仍立即收起（`SelfTest.Ui.cs` 原有编号 15/17/19 断言覆盖）。

### 1.2 默认窗口高度下无法看到/滚动到精讲底部

**根因**

`ResultPanelWindow.xaml` 是 8 行 Grid：原文/译文两个 Star 行 `MinHeight="80"`，精讲 Expander 在第 6 行（Auto），其后还有状态栏与错误详情两个 Auto 行。默认 520 高度下总内容已超过客户区，Grid 不滚动、直接溢出裁切。精讲区虽有内部 `CardsScrollViewer` 且 `MaxHeight` 被 `SizeChanged` 改为 `ActualHeight * 0.45`（约 234），但当内容高度介于"裁切后剩余高度"与 234 之间时，内部滚动条认为放得下、不出现，而 ExplanationCardView 下半部分连同下方状态栏已在窗口边界外——**看不见也滚不到**。

**修复（方案 A，主人已定）**

1. 把内容根 Grid 整体包进 `ScrollViewer`（`VerticalScrollBarVisibility="Auto"`，与设置页 `PageHost` 同模式），任何内容都可滚动到达——整页滚。
2. `ExplanationView` 保留 `MaxHeight` 上限（沿用 `SizeChanged` 里 `Math.Max(160, ActualHeight * 0.45)` 的现值），让精讲区在整页滚动之外仍有局部滚动，避免长精讲把面板撑到一屏滚不完；形成"外层整页滚 + 内层精讲局部滚"的两级滚动（WPF 内层优先消费滚轮，体验正常）。
3. 状态栏（InfoBar）、MetricsBar、错误详情随 Grid 一起进滚动区，不再被裁；词汇最后一张卡片的 `Margin="0,0,0,6"` 即滚动末端呼吸空间，无需额外 padding。
4. `Window.Height=520` 默认值不动（保持主人当前默认窗口体验，只是现在滚得动）。

**验证点**：默认高度下跑长短两个精讲（词汇卡 2 张 vs ≥5 张），确认都能滚到最后一个卡片下边缘、状态栏可达；最大化/拖高后页面无滚动条、无裁切。

### 1.3 词汇注音重复：`気付く[きづく] (きづく)`

**根因判定：提示词缺口 + 解析/展示层不去噪，二者叠加后由模型随机性触发。不是纯随机行为，代码侧有明确可修点。**

1. **提示词**：`PromptTemplates.DefaultExplainUserPrompt` 只约定 `- **词汇** | 读音或音标 | 词性 | 释义`，没禁止「词汇」字段自带注音；阶段二是同一会话的追问消息，阶段一的振假名规范（`漢字[かな]`）仍在上下文，模型顺手写进词条是高概率事件。
2. **解析器**：`ExplanationParser.TryParseVocabularyLine` 的 `CleanMarkdownFormatting` 只剥最外层 `**/ */` 反引号，不剥词条内部 `[かな]`；`DisplayTitle` 再追加 ` (读音)`，正好两层。
3. **随机性**：是否内嵌随采样波动，同一词条不同轮次表现不一，天然脏数据源。

**修复（三层设防，主人已同意改提示词与 Pattern 字段）**

1. 精讲提示词加显式约束：词条/句型第一字段只写原型（例：`気付く`），严禁在内部附带振假名或括号读音，注音只放专属列。
2. 解析器兜底：`Term`/`Pattern` 落库前先剥离自带注音——复用 `RubyTextParser`；词条内嵌读音与读音列一致时二选一去重到 `Reading`。误伤防护：只剥"方括号内全为假名/长音/中黑"的注音形态，英文 IPA、`arr[0]` 下标、`[语法/句型]` 式标签保留（判定借鉴 `RubyTextParser.IsKana`/`IsCodeBracket`）。
3. `DisplayTitle` 沿用现有"Term (Reading)"逻辑；Term 净化后只显示一次读音，收藏仍存 `Term` 本体。
4. 自测补 case：`- **気付く[きづく]** | きづく | 动词 | …` → `Term=気付く`、`Reading=きづく`、`DisplayTitle=気付く (きづく)`；语法卡 `Pattern` 同例。

### 1.4 收藏项因注音残留产生重复

**根因**：卡片收藏存 `vm.Term`（可能含 `[きづく]`），划词气泡存用户划选原文（如 `気付く (きづく)`）；`SqliteFavoriteService` 的 upsert/查询键是 content 精确匹配（trim + NOCASE），括号形态不同即不同行。

**修复（主人决策：不做历史合并，从源头断重复 + 陈列时认旧账）**

1. **入库归一化**：新增共享归一化函数（剥离 `漢字[かな]`、`漢字 (かな)` 形态后再 trim），`AddFavoriteAsync` 落库前应用——新数据统一形态。
2. **匹配归一化**：`IsFavorited` / `RemoveFavoriteAsync` 改用归一化比较。实现方式：先按归一化键精确查，未命中再对全表做归一化比对（`GetAllFavorites` 已是全量快照，收藏量小、可接受；不要用 SQL 函数猜正则）。
3. **陈列时点亮星标**（核心诉求）：`ExplanationCardView.SyncVocabularyCards` 现有的 `IsFavorited(card.Term, "单词")` 在归一化匹配下，能认出院里已有的旧形态行（`気付く[きづく]`）并点亮星标；用户看到"已收藏"就不会重复点，点下去也是取消收藏路径（删除旧行），不会产生新重复。
4. **类别匹配放宽**：星标点亮改为按归一化 content 匹配、不强制类别一致（否则"单词收藏过、短语卡片再陈列"会漏亮）；类别仅作为默认写入值。
5. 纠正一个隐藏坑：旧行 `気付く[きづく]` 被点掉后，若用户再收藏 `気付く`，走的是归一化后的新写入，库里自动只剩规范形态。

---

## 2. 设置面板收藏 Tab

### 2.1 数据库位置：单行 + 复制

取消"知识库"`SettingsGroup`；在横向导航条**下方**放一行：`TextBlock`（`Text.Caption`）显示数据库绝对路径 + 图标按钮（`Button.Icon` + 复制图标 `&#xE8C8;`），点击复制路径到剪贴板，复用 `FavoritesPage.CopyToClipboard` 的 try/catch + `InfoBar` 提示；原长说明整体降级进入该行 `ToolTip`。`SelfTest.Ui.cs` 中断言 `DatabasePathText` 的用例随布局位置调整。

### 2.2 检索与操作 → 顶部横向导航条，列表直铺

页面结构改为：

```
[横向导航条：类别 ▾ | 语言 ▾ | 关键词搜索框 | 刷新 | 复制全部]
[数据库路径单行 + 复制图标]                ← 2.1
[计数文案 + 空态提示]
[收藏条目卡片列表（沿用现有 ItemTemplate、单条复制/删除/编辑）]
```

删除"检索与操作"与"收藏列表"两个 `SettingsGroup`；`ApplyFilter` / `Refresh` / `OnCopyAllClick` / `OnDeleteItemClick` 逻辑整体保留，只是宿主容器换成导航条。页面根保持自适应高度，滚动继续交给设置窗 `PageHost`。

**主人追加要求（2026-09-29 晚）：搜索条填满剩余横向空间。** 导航条由 `WrapPanel` 改为 5 列 `Grid`（Auto | Auto | **\*** | Auto | Auto）：类别、语言下拉与刷新/复制全部保持 Auto 按最窄可用宽度收敛，关键词搜索框占星号列，随窗口宽度伸缩，不再在行尾留一段难看的固定空白；窗口收窄时搜索框让位（不设硬 MinWidth，保证其他控件不被裁切）。

### 2.3 导航条语言筛选（方案 B：新增 language 列）

**Schema 变更**

- `FavoriteItem` 新增 `Language`，取值 = 共享语言选项的展示名（母语写法，如 `日本語`、`English`、`中文（简体）`）。
- `EnsureSchema`：新库 `CREATE TABLE` 直接含 `language TEXT NOT NULL DEFAULT '日本語'`；旧库用 `PRAGMA table_info(favorites)` 检测缺列则 `ALTER TABLE favorites ADD COLUMN language TEXT NOT NULL DEFAULT '日本語'`。**该默认值只对已存在的旧收藏行生效**（主人明确：回填日语指当前已保存的收藏项）；新收藏一律显式赋值，不依赖列默认。
- SQL 读取（`GetAllFavorites` / `IsFavorited` / `Add` / 2.5 的 Update）随 schema 增补该列。

**语言列表共用一份（主人明确要求）**

- 把 `GeneralPage` 里私有的 `LanguageChoice` 与 `TargetLanguages` 数组提取为共享类型（如 `App/Settings/LanguageChoices.cs`，暴露 `All` 只读集合），**选项展示名改为各语言母语写法**：`中文（简体）` / `繁體中文` / `English` / `日本語` / `한국어` / `Русский` / `Français` / `Deutsch` / `Español` / `Português` / `Italiano`。
  - `GeneralPage` 改为引用共享列表；`config.General.TargetLanguage` 之后存母语名。`LanguageChoice.Matches` 的别名集已涵盖中英与代码（`日语`/`日本語`/`ja`），旧配置值仍能匹配、**无需配置迁移**；`LanguageDetector.IsJapaneseTarget` 等谓词本就匹配 `日本語`，提示词 `{{target_language}}` 展开为母语名亦无害。
- 收藏 Tab 筛选下拉、划词气泡语言下拉、卡片收藏默认值全部取自同一共享列表——"设置页可选目标语言 ≡ 收藏语言选项 ≡ 气泡语言下拉"，永不漂移。
- **探测现算 → 映射回列表对应项（不是三档硬编码）**：`ResultPanelWindow` 用 `LanguageDetector.Detect(当前原文纯文本, 目标语言)` 现算本轮源语言，映射规则：含假名或纯汉字日语 → 共享列表的 `日本語`；仅拉丁 → `English`；命中简体独占字 → `中文（简体）`；均不命中回落 `日本語`（与探测器空文本默认推荐日语注音一致）。映射函数随共享语言列表一并提供（如 `LanguageChoices.FromDetection`），以后语言列表扩充时单点维护。
- 透传管道：探测结果目前只在拼提示词时内部使用、不外传，需补这条小管道——从 `ResultPanelWindow` 传给 `ExplanationCardView` 与 `SelectionBubblePopup`；视觉一条龙模式同样适用（收藏时只认面板里的 OCR 原文）。
- 卡片收藏（词汇/语法）：直接用透传默认值，不做选择 UI。划词气泡：默认取同一值 + 语言下拉允许用户调整（与类别 ComboBox 并列）。
- 收藏页筛选：`全部` + 共享语言列表；与类别筛选、关键词搜索三者 AND。
- `FormatEntry` 复制文本的 `[类别]` 前缀建议带上语言（如 `[单词 | 日本語]`），便于导出阅读。

### 2.4 词性 badge 风格统一

把精讲卡片徽章样式抽成共享资源（`Style x:Key="Badge.Tag"` 落 `App/Theme/Controls.Base.xaml`）：`Brush.Accent.Subtle` 底 + `Brush.Control.Border` 1px 描边 + `CornerRadius="4"` + `Padding="6,1"` + 字号 11 常规字重 + 主文本色。`ExplanationCardView.xaml` 与 `FavoritesPage.xaml` 同时引用，深浅色由色板自动跟随。**文案沿用 `Category`（单词/短语/语法/备注）**（主人确认：词性 badge 是 category 的误读，只换样式，现在的蓝底 `#3B82F6` 去掉）。

### 2.5 收藏条目编辑（主人新增需求）

- `IFavoriteService` 新增 `Task<bool> UpdateFavoriteAsync(FavoriteItem item)`：按 Id 更新 `category / content / note / language`；`InMemoryFavoriteService` 与 `SqliteFavoriteService`（按 Id 的 UPDATE）双实现，事件侧沿用 `FavoriteAdded` 广播（或补 `FavoriteUpdated`，实现时择一并写注释）。
- 收藏条目卡片加"编辑"图标按钮，卡片**就地切换编辑态**（content / note 文本框 + category / language 下拉），失焦即保存并给出保存成功提示（InfoBar Success）。采用行内而非模态：与项目"行内反馈优先于弹窗"的既有约定一致（见 `codestable/issues/settings-ui-redesign.md` §5），`MessageDialog` 只承载消息确认、不适合表单。
- 冲突策略：保存时按 1.4 的归一化函数校验，若命中**另一行**（Id 不同）→ 拒绝保存 + InfoBar 警告，不静默合并、不覆盖。
- 编辑 content 后的归一化：与 Add 同一函数，编辑同样不允许产生新脏形态。
- 键盘细节：Esc 取消还原、Enter 提交，作为失焦保存之外的快捷路径（实现时择要，写注释）。

---

## 3. 受影响的自测与文档

| 位置 | 影响 | 处理 |
| --- | --- | --- |
| `SelfTest.Favorites.cs` | 入库归一化、归一化匹配、language 列与旧库迁移、Update | 补 case：三种括号形态归一化互认；IPA/下标不误剥；旧 schema 打开自动补列且旧行 language=日本語；Update 按 Id 生效与归一化冲突拒绝 |
| `SelfTest.Ui.cs :: TestFavoritesSettingsPageFilteringAndEvents` | 收藏页去 Group、语言筛选、行内编辑、路径行位置 | 同步改；补语言筛选、编辑往返、复制路径断言 |
| `SelfTest.Ui.cs :: TestRubyDocumentBuilderAndDualMode`（气泡段） | `StaysOpen` 语义、语言默认/调整、PreviewMouseDown 触发收藏 | 补"气泡内点击不产生新气泡""语言默认值 = 面板源语言"回归 |
| `SelfTest.Explanation.cs` | 提示词文案、词条/句型注音剥离 | 断言改为检查新措辞；补 `気付く[きづく]` 解析 case |
| `GeneralPage.xaml.cs` | `LanguageChoice` 提取为共享、选项改母语写法 | 抽取后行为不变；补"共享列表同一实例"断言；复核既有引用目标语言字符串的自测（中文名未变，其余语言改母语名） |
| `README.md` | favorites 表结构变化（language 列） | 实现后同步一句 |

---

## 4. 实施顺序与 TODO 进度

实施顺序：**埋点最优先**（不依赖其他改动，让主人尽早取到诊断日志）→ 数据正确性（1.3/1.4）→ 收藏 Tab 改造（2.2 先行，2.1/2.4 随行，2.3 次之，2.5 收尾）→ 1.1 阶段二修复 → 1.2 布局收尾。第 3 节的自测清单随各子项同步完成。

- [x] **1.1-A** 气泡诊断埋点（`%TEMP%` 日志：五条收起路径 + `Closed` + 收藏按钮入口 + 三个 MouseUp 带 `SelectionLength`），交付主人复现
- [x] **1.1-B** 首轮四件套（`StaysOpen=True`；`Deactivated` 延迟 + 逻辑焦点判定；`ShowAt` 清理残留鼠标捕获；收藏触发移至 `PreviewMouseLeftButtonDown`）；埋点随修复一并移除
- [x] **1.1-C** 第二轮实测翻案：PopupRoot 在 topmost band 里排在面板之下 → 点击漏到面板被 `OnPanelPreviewMouseDown` 收起、MouseUp 同址二次冒泡。修复：打开后把 PopupRoot 显式提升到 topmost 顶端（`BringBubbleToTopmostBand`）+ 窗口侧收起/重弹守卫（`ContainsScreenPoint`，按 DPI 同口径）；探针真实输入回归 A/B/C/D 四项全绿；补自测契约断言
- [x] **1.3** 精讲提示词加"词条/句型不嵌注音"约束；解析器剥离 `Term`/`Pattern` 内嵌注音并与 `Reading` 去重；补解析自测 case
- [x] **1.4** 归一化函数（Add 落库 + `IsFavorited`/`RemoveFavoriteAsync` 匹配）；陈列星标按归一化 content 匹配（不强制类别）；补自测 case
- [x] **2.2** 收藏页横向导航条 + 条目直铺，删除"检索与操作""收藏列表"两个 `SettingsGroup`；**搜索条占星号列填满剩余横向空间**（主人追加，见 2.2 节）
- [x] **2.1** 数据库路径单行 + 复制按钮（导航条下方），长说明降级为 ToolTip
- [x] **2.4** 抽 `Badge.Tag` 共享样式（Accent.Subtle 底 + Control.Border 描边），精讲卡与收藏页双引用；文案沿用 `category`
- [x] **2.3-A** `LanguageChoice` 提取为共享 `LanguageChoices`（选项母语写法 + `FromDetection` 映射），`GeneralPage` 改引用
- [x] **2.3-B** `language` 列（新库含列、旧库 `ALTER`，旧行回填「日本語」）+ `FavoriteItem.Language` + SQL 读写
- [x] **2.3-C** 面板探测现算 → 映射 → 透传卡片/气泡；气泡语言下拉；收藏页语言筛选；复制格式带语言
- [x] **2.5** 行内编辑（失焦保存 + 成功 InfoBar）+ `UpdateFavoriteAsync` + 归一化冲突拒绝
- [x] **1.2** 内容区外层 `ScrollViewer`（精讲保留内部 `MaxHeight` 二级滚动，状态栏/错误详情进滚动区）
- [x] **收尾** `./scripts/dev.ps1 test` 全绿 + 主人实机验收（按 1.1/1.2 的回归标准）

## 5. 实施记录（2026-09-29）

全部代码项已完成，`./scripts/dev.ps1 test` 全绿（436 项 PASS / 0 FAIL；自测运行时不再有句柄竞争的进程级异常退出）；下表为各子项落点与自测覆盖。

> **1.1 的两轮根因结论（务必先读）**：首轮四件套修的都是"气泡被误关"的候选路径，实测 11/11 复现里这些路径一条都没触发；真正根因是**气泡 PopupRoot 在 topmost band 里排在结果面板之下**，气泡与面板重叠区被面板窗口盖住——点气泡等于点面板，被 `OnPanelPreviewMouseDown` 收起后同址 MouseUp 又弹一个新的。修复是打开气泡后显式把 PopupRoot 提升到 topmost 顶端，另加窗口侧"点击落在气泡矩形内就不收起/不重弹"的双保险。

| 子项 | 落点 | 自测覆盖 |
| --- | --- | --- |
| 1.1-A/B | 埋点随首轮修复一并移除（`BubbleDebugLog` 已删除）；`StaysOpen=True`、`Deactivated` 延迟 + `ContainsLogicalFocus()`、`ShowAt*` 清理残留捕获、收藏触发移至 `PreviewMouseLeftButtonDown`（Handled 防 Click 双算） | StaysOpen 契约、PreviewMouseDown 触发收藏、逻辑焦点判据可用 |
| 1.1-C | **真根因与真修复**：`BringBubbleToTopmostBand`（打开后把 PopupRoot 显式 `SetWindowPos(HWND_TOPMOST)`，首帧无句柄时 Input 优先级补偿，上限 8 次）+ `ContainsScreenPoint` 守卫（`OnPanelPreviewMouseDown` 与三处划词 MouseUp 均不再因"点击落在气泡内"而收起/重弹；`PointToScreen` 物理像素与 `RenderSize` DIP 已按 DPI 换算同口径）+ `Hide()` 线程安全化（顺带修掉无 UI 自测里 `Task.Delay` 续体落线程池导致的进程异常退出，基线同样存在） | 探针真实输入回归（125% DPI 下 12/12 全绿，含 `WindowFromPoint` z-order 铁证）；`ContainsScreenPoint` 关闭态恒 false 契约断言 |
| 1.3 | 提示词显式禁嵌注音；`RubyTextParser.StripRubyAnnotations` 剥离 `Term`/`Pattern` 内嵌注音并与 `Reading` 去重；IPA/下标/标签不误剥 | 解析 case + 误伤防护直断 |
| 1.4 | `FavoriteContentNormalizer`（Add 落库 + 匹配）；星标按归一化 content 且不强制类别；`IsFavorited`/`RemoveFavoriteAsync` 归一化兜底 | 三种括号形态互认、upsert 单行、跨形态移除 |
| 2.1/2.2/2.4 | 横向导航条（类别/语言/关键词/刷新/复制全部）+ 路径单行 + 复制按钮 + 清单直铺；`Badge.Tag` 共享样式双引用；**导航条改 5 列 Grid，搜索条占星号列填满剩余横向空间**（主人追加） | 去 Group 断言、语言筛选、路径按钮存在 |
| 2.3-A/B/C | `LanguageChoices` 共享列表（母语写法 + `FromDetection`）；`language` 列新库含列、旧库 `ALTER` 回填「日本語」；面板探测现算透传卡片/气泡；气泡/收藏页下拉；复制格式带语言 | 共享列表同一实例、别名命中、探测映射、旧库迁移、语言筛选、气泡默认语言 |
| 2.5 | `UpdateFavoriteAsync`（按 Id，双实现）+ `FavoriteUpdated` 事件；行内编辑失焦保存/Esc/Enter + 归一化冲突拒绝 + Success InfoBar | Update 字段与冲突拒绝、编辑往返 |
| 1.2 | 内容根 Grid 外包 `ScrollViewer`（`MinHeight` 绑 `ViewportHeight`，内容不高时星号行仍撑满）；精讲保留 `MaxHeight` 二级滚动 | 滚动契约断言 |

**待主人实机验收**：1.1 的回归标准（点气泡内编辑框/类别下拉/收藏按钮不关闭也不冒新气泡；点别处文字、拖动、Esc、切窗仍立即收起——本轮已用真机 DPI 的真实 OS 级点击回归，仍需主人在自己的版面下复核一次）、收藏页导航条搜索条随窗口填满剩余宽度的观感，以及 1.2 的验证点（默认高度下长短两种精讲都能滚到底部卡片下边缘与状态栏；最大化后无滚动条、无裁切）。

**注：本轮修复已编译到标准构建输出（`src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\NaniKore.exe`），主人直接启动就是新版；诊断 / 回归用探针（`--bubble-selection-probe`）已按"临时诊断"约定从代码树移除。**


