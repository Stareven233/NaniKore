using System.Runtime.Versioning;

namespace NaniKore.Input;

/// <summary>
/// 全局热键窗口封装：基于纯 Win32 消息专用窗口，完全兼容 NativeAOT，脱离 WinForms NativeWindow。
/// 保持原有接口与异常契约，支持设置保存后的重新注册与销毁。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlobalHotkeyWindow : IDisposable
{
    private readonly int _hotkeyId;
    private readonly Win32HotkeyHost _host;
    private bool _registered;
    private bool _disposed;

    /// <param name="gesture">规范化热键组合文本（如 Ctrl+Shift+A）。</param>
    /// <param name="onPressed">热键按下触发的回调。</param>
    /// <param name="hotkeyId">热键 ID。</param>
    /// <param name="host">底层 Win32 宿主；缺省使用进程级共享宿主。</param>
    public GlobalHotkeyWindow(string gesture, Action onPressed, int hotkeyId, Win32HotkeyHost? host = null)
    {
        _host = host ?? Win32HotkeyHost.Shared;
        _hotkeyId = hotkeyId;

        // 解析规则与设置页录制控件完全一致，格式错误或未包含有效修饰键时在此抛出 ArgumentException
        var (modifiers, key) = HotkeyGesture.Parse(gesture);

        // 底层已处理 MOD_NOREPEAT 与冲突检测（冲突抛出 Win32Exception(1409, ...)）
        _host.RegisterHotKey(_hotkeyId, modifiers, key, onPressed);
        _registered = true;
    }

    /// <summary>校验手势文本是否合法，非法时抛出 ArgumentException。</summary>
    public static void ValidateGesture(string gesture) => _ = HotkeyGesture.Parse(gesture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_registered)
        {
            _host.TryUnregisterHotKey(_hotkeyId);
            _registered = false;
        }

        _disposed = true;
    }
}
