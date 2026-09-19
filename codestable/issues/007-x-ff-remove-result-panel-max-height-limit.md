---
kind: issue
title: "移除结果面板最大高度限制并支持会话内记忆自定义尺寸"
type: ff
status: closed
created: 2026-09-19
---

# 移除结果面板最大高度限制并支持会话内记忆自定义尺寸

> **读者：** 关注桌面结果卡布局与交互的开发者。  
> **关联文件：**  
> - `src/NaniKore/App/ResultPanelWindow.xaml.cs`  
> - `src/NaniKore/App/NaniKoreApp.cs`  

---

## 做了什么

1. **移除硬编码高度上限**：
   - 删除了 `ResultPanelWindow` 构造函数中 `MaxHeight = SystemParameters.WorkArea.Height * 0.6;` 的硬编码限制；
   - 彻底放开垂直缩放自由度，用户可随意拖拽窗口边缘调整到任意舒适高度浏览长文本与精讲内容。
2. **会话级记忆自定义窗口尺寸**：
   - 在 `NaniKoreApp` 中跟踪记录用户调整后的 `_lastPanelWidth` 与 `_lastPanelHeight`；
   - 用户手动缩放窗口后，后续截图触发新翻译面板时自动继承该尺寸，无需每次重复拖拽。

---

## 怎样验证

- 运行 `./scripts/dev.ps1 test`，所有自测用例全绿通过。

