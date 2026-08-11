# 设置页 UI 现代化设计（v1）

面向 2026 年 Windows 桌面观感重构 NaniKore 的设置界面与面板类窗口，目标是「看起来像现代应用，但不牺牲启动速度与体积」。

## 1. 现状问题

| 现状 | 问题 |
| --- | --- |
| `SettingsForm` 是 WinForms `Form` + `TabControl` | 顶部 Tab、系统灰底、按钮全是 Win9x 风格 |
| `TextBox` / `ComboBox` / `NumericUpDown` 使用系统默认绘制 | 方角、1px 凹陷边框、无焦点动效、无悬停态 |
| `SettingsLayout` 手工维护两列 `TableLayoutPanel` | 缩放兼容靠代码补偿，字段与说明文字容易错位 |
| 校验失败弹 `MessageBox` | 打断流程，且弹窗本身也是旧观感 |
| 结果卡、进度窗虽是 WPF，但用默认控件模板 | 与设置页观感割裂，深色桌面下发白 |
| 托盘右键菜单用 `ContextMenuStrip` 默认渲染器 | 深色主题下依然是白底方角菜单 |

## 2. 方案选择

选定：**用 WPF 重写设置页 + 自写 Fluent 风格 ResourceDictionary，零新增依赖。**

理由：

- 项目已启用 `UseWPF`，结果卡与进度窗都是 WPF；设置页是最后一块 WinForms UI。统一到 WPF 后只需维护一套样式资源。
- WPF 控件模板可完全自绘（圆角、焦点下划线、悬停过渡），WinForms 自绘 `ComboBox` / `NumericUpDown` 的上限低且代价高。
- 不引入 WPF-UI / MahApps 等第三方库：避免约 2–3 MB 体积与升级风险，本项目需要的控件集合很小（输入框、下拉框、按钮、开关、数字框、卡片、导航）。
- 保留标准窗口边框（不做无边框自定义标题栏）：拖动、贴边、最大化、混合 DPI 全部交给系统，仅通过 DWM 把标题栏染成深色。回归成本最低。

## 3. 视觉规范

- 主题：跟随系统深浅色（注册表 `AppsUseLightTheme`），并监听 `WM_SETTINGCHANGE` / `ImmersiveColorSet` 实时切换；强调色取系统 `AccentColor`，取不到时回退品牌蓝。
- 字体：`Segoe UI Variable Text` → `Segoe UI` → `Microsoft YaHei UI` 回退链；正文 14px，卡片标题 14px SemiBold，说明文字 12px 次要色。
- 圆角：卡片 8px，输入控件 6px，按钮 6px，导航项 6px。
- 间距：窗口内容 20px 外边距，卡片内 12/16px，字段行高不小于 32px。
- 交互态：默认 / 悬停（底色抬升）/ 焦点（底部 2px 强调线）/ 禁用（40% 不透明），统一 120ms 过渡。
- 深色下所有面板窗口标题栏同步变深（`DWMWA_USE_IMMERSIVE_DARK_MODE`）。

## 4. 结构设计

```
App/Theme/
  Palette.Light.xaml     # 浅色语义化色板
  Palette.Dark.xaml      # 深色语义化色板
  Controls.xaml          # 全部控件隐式样式与模板
  ThemeManager.cs        # 主题探测、热切换、DWM 标题栏、深色托盘菜单渲染
App/Controls/
  SettingsRow.cs         # Win11 设置行：标题 + 说明 + 右侧控件
  NumberBox.cs           # 带 ± 步进的数字输入，替代 NumericUpDown
App/Settings/
  SettingsWindow.xaml    # 左侧导航 + 内容区 + 页脚，替代 SettingsForm
  GeneralPage.xaml       # 四个页面改为 WPF UserControl
  TranslationPage.xaml
  HotkeyPage.xaml
  AboutPage.xaml
  HotkeyRecorderBox.cs   # 改为 WPF 模板化控件，录制规则不变
```

关键约定：主题资源全部走**隐式样式 + DynamicResource**，页面 XAML 不引用 app 级 `StaticResource`。这样自测进程在没有 `Application` 实例时也能直接构造页面做逻辑断言。

## 5. 交互改进

1. 顶部 Tab 改为**左侧竖排导航**（通用 / 翻译 / 热键 / 关于），带 Segoe Fluent 图标与选中指示条；窄窗口下导航自动收成图标列。
2. 字段按语义分组进**卡片**，每行支持一句说明文字，替代原先游离在页面底部的整段提示（顺带解决翻译流程说明拥挤的问题：两种流程各占一行说明）。
3. 校验失败改为页脚**内联提示条**（图标 + 文案 + 强调色边框），不再弹 `MessageBox`；成功保存后窗口关闭。
4. 「测试连接」状态改为带状态点的内联提示（进行中 / 成功 / 失败三态配色）。
5. 布尔项用**开关（ToggleSwitch）**而不是复选框，符合当代设置页习惯。
6. 热键录制框录制时显示强调色描边与呼吸提示文案，录制规则、全局热键临时注销逻辑完全沿用。
7. 托盘菜单套用同一色板的自定义 `ToolStripRenderer`，深色下不再白底。

## 6. 效率约束

- 不用第三方 UI 库，不引入 MVVM 框架；页面仍是「Load / Validate / Apply」直连配置对象，无绑定层开销。
- 不使用 `BlurBehind` / `Acrylic`：合成开销大且低配机器掉帧，改用纯色分层（窗口底色 + 卡片抬升色）模拟层次。
- 动画只用 120ms 的颜色过渡，无布局动画。
- 设置窗口保持单例、非模态、`Topmost`，与结果卡同层级。

## 7. 兼容与影响面

- `ISettingsPage.View` 类型由 `System.Windows.Forms.Control` 变为 `System.Windows.FrameworkElement`。
- 删除 `SettingsForm.cs`、`SettingsLayout.cs`；`HotkeyRecorderBox` 由 `TextBox` 派生改为模板化 `Control`，公开成员保留 `Gesture`、`IsRecording`、`BeginRecording`、`CancelRecording`、`ProcessRecordingKey`、`StatusChanged`、`RecordingChanged`。
- `NaniKoreApp.ShowSettings` 改为管理 `SettingsWindow`。
- 自测中依赖 WinForms 布局的两个断言改写：`about page` 读页面文本属性，`scalable layout` 改为校验页面根元素与设置行的自适应约定 + 四个页面的配置往返。
- 配置结构不变，`config.yaml` 不新增字段（主题跟随系统，不做手动覆盖开关）。

## 8. 验收清单

- 100% / 150% / 200% 缩放下四个页面无裁切、无错位。
- 系统深浅色切换时设置页、结果卡、进度窗、托盘菜单即时跟随，无需重启。
- 热键录制、配置档增删复制、API Key 暂存与保存、测试连接、提示词恢复默认行为与旧版一致。
- `./scripts/dev.ps1 test` 全绿；`./scripts/dev.ps1 build` 无警告（项目开启 `TreatWarningsAsErrors`）。

## 9. 本次不做

- 无边框自定义标题栏与 Mica 材质。
- 设置页手动主题覆盖开关（需要新增配置字段）。
- `_todo.md` 中的 API Key 回显修改（属功能项，需主人确认后单独做）。
