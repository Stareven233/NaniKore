---
kind: issue
title: "扩展全局热键按键并修复注册与保存生命周期"
type: ff
status: closed
created: 2026-11-09
---

# 扩展全局热键按键并修复注册与保存生命周期

扩展修饰键后可用的标点、导航键、编辑键、小键盘、锁定键及浏览器/多媒体键，统一配置解析、录制与格式化。裸 Esc 保留取消/解绑用途，带修饰键时可绑定；F12 明确提示 Windows 调试器保留，裸功能键范围为 F1–F11。

- 改动：`Input/HotkeyGesture.cs`、`App/Controls/HotkeyRecorderBox.cs`、`App/Settings/HotkeyPage.*` — 扩展键码与稳定键名，拒绝 Unicode 假键码、空分段和两个槽位的等价组合。
- 改动：`App.axaml.cs`、`App/Settings/SettingsWindow.axaml.cs` — 录制时暂停两个槽位，交换前统一释放；保存提交独立快照，关闭尺寸仅叠加最新已提交配置，修复连续保存不重注册及拒绝编辑泄漏。
- 改动：`Input/Win32HotkeyHost.cs` — 同步请求接收与关闭边界，检查消息投递并终结未完成请求，拒绝重复 ID，防止覆盖回调与并发关闭永久等待。
- 验证：`./scripts/dev.ps1 build` 零警告、零错误；最终 `./scripts/dev.ps1 test` 退出码 0。新增解析/录制映射、真实 Windows 扩展键注册、重复 ID、并发关闭、连续保存快照及关闭写回回归测试；`git -c core.whitespace=cr-at-eol diff --check` 通过。
- 界面跟进：将两个槽位重复的「点击方框开始录制」说明合并到页尾，与录制期间注销说明放在一起；槽位状态行默认隐藏，交互后显示各自结果或错误。再次执行 `./scripts/dev.ps1 build`，零警告、零错误。
- codestable：已同步 `spec/index.md` 当前热键与设置快照契约，并更新 README。构建脚本自动递增模板版本 `0.6.2+8` → `0.6.2+10`。
