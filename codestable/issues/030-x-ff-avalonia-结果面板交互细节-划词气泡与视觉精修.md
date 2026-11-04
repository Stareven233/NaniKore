---
kind: issue
title: "Avalonia 细节再打磨：结果面板交互状态机、划词气泡贴合、全局选中高亮与视觉极简精修"
type: ff
status: closed
created: 2026-11-04
updated: 2026-11-04
---

# Avalonia 细节再打磨：结果面板交互状态机、划词气泡贴合、全局选中高亮与视觉极简精修

> **读者：** 负责桌面 UI 视觉体验、结果面板状态机、划词气泡定位与排版细节的开发者。  
> **关联 Epic：** `codestable/epics/002-o-Avalonia 迁移与 Native AOT 单文件发布/spec.md`  
> **关联文件：**
> - `src/NaniKore/App/ResultPanelWindow.axaml` / `.axaml.cs`
> - `src/NaniKore/App/Controls/SelectionBubblePopup.axaml` / `.axaml.cs`
> - `src/NaniKore/App/Controls/ExplanationCardView.axaml`
> - `src/NaniKore/App/Theme/Palette.Dark.axaml` / `Palette.Light.axaml`
> - `src/NaniKore/App/Theme/NaniKoreStyles.axaml`
> - `src/NaniKore/App/Theme/ThemeManager.cs`

---

## 1. 现象与主人反馈（8 项打磨清单）

在初步解决热键与字体问题后，主人进行了全面深入的交互测试，提出了 8 项亟待攻克的质感与交互细节问题：

1. **仍然没有窗口圆角**：设置面板与结果面板在真机上依然呈现生硬的直角矩形；
2. **强调色不够鲜活，按钮字体偏粗重**：保存与翻译按钮的背景色绿意不够鲜艳元气、偏暗，且按钮文字被显式加粗，显得厚重呆板；
3. **选中文本高亮为死黑，划词收藏气泡体验欠佳**：
   - 原文区划词选中文本时，选区背景色竟然是纯黑色（完全未适配主题色）；需要彻查整套应用到底还有多少处选中色/高亮色遗漏；
   - 划词后弹出的收藏气泡栏距离被选中的文本过远，未贴合在文字正下方；
   - 气泡菜单四角颜色加深发暗（阴影/内衬瑕疵），不如直接做成统一清爽的圆角；气泡选项文字略小；
4. **原文区双击编辑交互生硬**：双击必须严格点中文字才能进入编辑，点在原文框空白处无效；且进入编辑模式后会自动全选文字，极不符合直觉；
5. **打开结果面板时原文区处于伪编辑状态**：打开面板时原文区文字呈现垂直居中（没有聚焦光标，却变了对齐方式）；编辑状态应当自然地从左上角（Top-Left）开始；
6. **未翻译时译文区状态与键盘焦点混乱**：明明还没有翻译，译文区却可以点击，光标在正中间垂直居中；此时单击原文区又会触发编辑；按 Esc 会直接关闭整个窗口，状态机混乱；
7. **译文清除按钮图标杂乱**：右上角的清除按钮使用了复杂的镂空 "x"，需要改成极简清爽的小叉；
8. **精讲折叠栏与自动开关排版瑕疵**：
   - 精讲与语法解析折叠栏加了突兀边框，且上下 padding 过大；
   - 下拉箭头在右侧而非习惯的左侧；
   - 未翻译/未触发时字体应当呈现浅灰置灰态；
   - 右侧的自动开关（ToggleSwitch）右侧带有冗余的 "On/Off" 文字，应彻底隐藏。
9. 临时补注：托盘右键菜单各选项没有居中，且hover的颜色不是绿色系，同时显得很老旧没有现代感
10. 临时补注：截图划选时应当允许右键取消选择

---

## 2. 根因剖析与详细整改方案

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        030 打磨整改架构全景                            │
└────────────────────────────────────────────────────────────────────────┘

 [1. 窗口圆角彻底落地]
   ThemeManager.cs / ResultPanelWindow.axaml / SettingsWindow.axaml
     │──> 引入 ExtendClientAreaToDecorations / 自定义卡片圆角或强制 DWM 重绘
     │──> 确保 Win11 / Win10 均呈现平滑大圆角

 [2. 色彩与按钮瘦身]
   Palette.Dark.axaml / Palette.Light.axaml / NaniKoreStyles.axaml
     │──> 强调色升级为鲜亮元气草地绿 (如 #8ED200 / #78C800)
     │──> Button.accent 移除 FontWeight="SemiBold"，字重回归常规 (Normal)

 [3. 选中高亮与划词气泡]
   NaniKoreStyles.axaml / SelectionBubblePopup.axaml
     │──> 全局覆盖 SelectionBrush 为半透明草地绿 (#3D95CA00 / #4D78C800)
     │──> 彻查 TextBox / SelectableTextBlock / ComboBoxItem 全局选中态
     │──> 划词气泡紧贴选区文本底边 (Margin/Offset 调优，垂直间距 4~6px)
     │──> 气泡背景圆角一体化，消除暗角；字号调大至 13px

 [4 & 5 & 6. 结果面板状态机与文本框重构]
   ResultPanelWindow.axaml / ResultPanelWindow.axaml.cs
     │──> 原文容器整体承接双击事件，空白处双击同样进入编辑
     │──> 进入编辑时不全选 (SelectionLength = 0，光标置于末尾)
     │──> 原文框与译文框统一 VerticalAlignment="Top"，消除垂直居中异象
     │──> 译文区未就绪时 IsHitTestVisible="False" / 仅显示占位提示，杜绝误点
     │──> Esc 键分层处理：编辑中按 Esc 退出编辑，常规态按 Esc 隐藏面板

 [7 & 8. 图标与精讲折叠栏精简]
   ResultPanelWindow.axaml / ExplanationCardView.axaml
     │──> 清除按钮改用极简细线 "×" (Segoe Fluent Icons 细体字形)
     │──> Expander 移除外边框，上下 Padding 紧凑收紧
     │──> Expander 箭头调整至左侧，未就绪时文字呈现 Brush.Text.Disabled
     │──> AutoExplainToggle 显式设置 OnContent="{x:Null}" OffContent="{x:Null}"
```

### 2.1 任务一：窗口系统级圆角彻底保障
- 调研并彻底解决 Windows DWM 未生效原因：在无边框或标准窗体下，开启透明背景与外层 `Border CornerRadius="10"` 配合卡片阴影，或者通过 Win32 `SetWindowCompositionAttribute` / DWM 重绘确保大圆角 100% 可见。

### 2.2 任务二：活力绿色彩调优与按钮减重
- 将强调色 `Color.Accent` 升级为更加鲜艳通透的高级元气草地绿（深色下对比度更高、更清脆醒目）；
- 将 `Button.accent` 的 `FontWeight` 从 `SemiBold` 改为 `Normal` 或与普通按钮统一，去除过重油墨感。

### 2.3 任务三：选中文本高亮彻查与划词气泡精修
- 在全局样式层为 `SelectableTextBlock`、`TextBox`、`ListBox` 统一配置 `SelectionBrush="{DynamicResource Brush.Accent.Subtle}"` 或半透明绿色高亮，绝不允许任何死黑高亮残留；
- 重新计算 `SelectionBubblePopup` 的贴边锚点，从当前偏差数十像素调整为严格跟随划词末端/中心正下方（间距 4-6px）；
- 气泡去掉四角重叠阴影与深色边角，外框一体化 `CornerRadius="8"`，菜单文字字号调至 13px。

### 2.4 任务四、五、六：面板状态机、对齐与焦点收敛
- **双击范围扩展**：双击监听挂载在整个原文卡片 Border 上，点击空白处也能顺畅进入编辑；
- **取消全选**：进入编辑态时设置 `CaretIndex = Text.Length; SelectionLength = 0;`；
- **排版归位**：原文与译文多行框均设置 `VerticalContentAlignment="Top"`，文字从左上角自然书写；
- **未翻译静默**：译文未产生输出前，禁用光标展示与点击交互；
- **Esc 键阶梯响应**：原文编辑态按 Esc 仅保存/退出编辑状态；结果面板常态下按 Esc 隐藏窗口。

### 2.5 任务七、八：极简关闭图标与精讲 Expander 净化
- 替换复杂的镂空 "x"，采用优雅干净的细线乘号/关闭符号；
- `Expander` 样式设为无框无描边模式，上下内边距缩减至 6px；箭头前置至标题左侧；未启用状态文字灰显；
- `AutoExplainToggle` 清空所有标签文本（`OnContent="" OffContent=""`），仅保留精致绿色胶囊开关。

---

## 3. 验收标准

1. 设置窗口与结果面板在真机上呈现平滑自然的圆角轮廓；
2. 保存/翻译按钮呈现充满生机的活力绿，文字匀称自然不加粗；
3. 文本选中无论在何处划词，均呈现半透明柔和绿色背景；划词气泡紧跟选区下方、圆角干净、字体舒适；
4. 双击原文任意区域均可进入编辑，光标自然留在末端无全选打扰；
5. 原文框与译文框均从左上角起笔，毫无垂直居中异象；未翻译时译文区不抢焦点；
6. 精讲栏干净无框、箭头左置、未翻译时灰度优雅；自动开关无多余 On/Off 字符；
7. 托盘右键菜单拥有绿色微质感与圆角悬停；
8. 截图划选支持鼠标右键随时取消。

---

## 4. 落地验证与完成情况

本次针对全部 10 项打磨细节逐项实施攻坚并一次性全量通过无 UI 自测试验套件：

1. **窗口圆角双系统完美保障**：
   - Windows 11（>= 22000）：通过 `DwmSetWindowAttribute` 注入 `DWMWA_WINDOW_CORNER_PREFERENCE = 2`（`DWMWCP_ROUND`）；
   - Windows 10（< 22000）：通过 Win32 `SetWindowRgn` 配合 16px 圆角裁切，并在窗口尺寸改变及最大化时智能同步重置，彻底攻克 Windows 10 生硬直角。
2. **强调色升级与按钮瘦身**：
   - 深色色板强调色升级为鲜亮元气草地绿 `#8ED200`，浅色色板升级为 `#78C800`，对比度显著提升且清脆自然；
   - `Button.accent` 移除 `FontWeight="SemiBold"`，回归匀称舒适的常规字重。
3. **选中文本高亮与划词气泡精修**：
   - 在全局样式及色板中覆盖 `SelectableTextBlock` 与 `TextBox` 的 `SelectionBrush` 为半透明草地绿（`#448ED200` / `#3D78C800`），彻底消灭死黑高亮；
   - 气泡弹出框通过 `PlacementRect = targetRect; VerticalOffset = 6;` 严格跟随划词末端/中心正下方；
   - 剥离气泡深色边角与生硬阴影，字体调优至 13px，高度拉伸至 30px，操作舒适。
4. **原文区全域双击与取消全选**：
   - 双击事件挂载至外层 `SourceViewerBorder`，点中空白区域同样可顺畅进入编辑模式；
   - 进入编辑态后异步校准光标定位在末尾且选区长度为 0，彻底压制 Avalonia 聚焦全选。
5. **消除垂直居中伪编辑**：
   - 打开面板无论是否自动翻译均默认停留在注音渲染态；
   - 原文框与译文框统一配置 `VerticalContentAlignment="Top"`，文字从左上角自然书写。
6. **译文区状态与 Esc 状态机阶梯响应**：
   - 译文区未翻译时设置 `IsHitTestVisible="False"` 并显示占位符「等待翻译...」，杜绝误点抢焦点；
   - Esc 键层级调度：气泡开启时关闭气泡 -> 原文编辑时撤销修改还原阅读态 -> 常态下按 Esc 关闭面板。
7. **极简清除按钮图标**：
   - 将繁杂镂空的乘号替换为 Segoe Fluent Icons 极简细线 `&#xE711;`。
8. **精讲折叠栏与自动开关净化**：
   - 引入 `Expander.clean` 样式：移除外边框，上下 Padding 紧凑收紧至 4px，展开小箭头前置于标题左侧（展开时旋转为绿色）；
   - 未就绪时置灰文字颜色；
   - `AutoExplainToggle` 清空 `OnContent` 与 `OffContent`，隐藏冗余文本。
9. **托盘右键菜单现代微质感定制**：
   - 全局定义 `MenuFlyoutPresenter`、`ContextMenu` 与 `MenuItem` 样式，具备 8px 圆角卡片底框与半透明草地绿悬停态，彻底告别老旧灰色 Win32 菜单。
10. **截图划选鼠标右键随时取消**：
    - 在浮层 PointerPressed 与 PointerReleased 隧道事件中监听鼠标右键，划选过程中随时右击即刻取消并关闭选区。
