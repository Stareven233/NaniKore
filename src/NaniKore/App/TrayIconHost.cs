using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NaniKore.App.Controls;

namespace NaniKore.App;

/// <summary>
/// 托盘入口集中管理：基于 Avalonia TrayIcon + NativeMenu 系统原生菜单，
/// 并通过自绘 WS_EX_NOACTIVATE 通知小窗承载警告消息通道（落实裁决 11 与 12）。
/// 彻底告别 WinForms NotifyIcon 与 200 行自绘 TrayMenuRenderer。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private bool _disposed;

    public TrayIconHost(Action capture, Action paste, Action settings, Action exit)
    {
        var menu = new NativeMenu();

        var captureItem = new NativeMenuItem("截图翻译");
        captureItem.Click += (_, _) => capture();
        menu.Add(captureItem);

        var pasteItem = new NativeMenuItem("选取翻译");
        pasteItem.Click += (_, _) => paste();
        menu.Add(pasteItem);

        var settingsItem = new NativeMenuItem("设置");
        settingsItem.Click += (_, _) => settings();
        menu.Add(settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => exit();
        menu.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "NaniKore",
            Icon = ApplicationIcon.Load(),
            Menu = menu,
            IsVisible = true
        };

        // 左键单击直达设置（常驻入口少一次点击少一分打断）
        _trayIcon.Clicked += (_, _) => settings();

        if (Application.Current is not null)
        {
            var icons = TrayIcon.GetIcons(Application.Current);
            icons?.Add(_trayIcon);
        }
    }

    /// <summary>
    /// 展示警告通知：通过自绘 WS_EX_NOACTIVATE 贴边浮层弹窗承载，
    /// 确保在无任何窗口存在时 7 个调用点完全可达，且绝不抢占前台键盘焦点。
    /// </summary>
    public void ShowWarning(string text)
    {
        TrayNotificationWindow.ShowNotification(text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Dispatcher.UIThread.CheckAccess())
        {
            Cleanup();
        }
        else
        {
            Dispatcher.UIThread.Post(Cleanup);
        }
    }

    private void Cleanup()
    {
        if (Application.Current is not null)
        {
            var icons = TrayIcon.GetIcons(Application.Current);
            icons?.Remove(_trayIcon);
        }

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
