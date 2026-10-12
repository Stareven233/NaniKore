namespace NaniKore.Input;

/// <summary>
/// 两个全局热键的身份（issue 018）：截图翻译走选区 + OCR/视觉链路，
/// 选取翻译走「选区文本 → 剪贴板文本 → 剪贴板图像」回落链。
/// 宿主据此分别注册、注销与重注册，互不牵连。
/// </summary>
internal enum HotkeySlot
{
    Capture,
    Paste,
}

/// <summary>录制状态变化通知宿主：哪个槽位正在录制，宿主只临时注销对应的那一个实例。</summary>
internal readonly record struct HotkeyRecordingState(bool Recording, HotkeySlot Slot);
