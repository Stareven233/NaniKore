using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NaniKore.Interop;

namespace NaniKore.App.Controls;

/// <summary>
/// 自绘托盘警告通知小窗：替代 Windows 气泡通知与实验性 Toast，
/// 具备 WS_EX_NOACTIVATE 特性，无论何时均绝对不抢占用户前台焦点，
/// 并在 4 秒后或点击后自动淡出关闭。
/// </summary>
internal sealed partial class TrayNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private bool _closing;

    public TrayNotificationWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(4000)
        };
        _dismissTimer.Tick += (_, _) => Dismiss();

        PointerPressed += (_, _) => Dismiss();
    }

    public static void ShowNotification(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowNotification(message));
            return;
        }

        var window = new TrayNotificationWindow();
        window.MessageBlock.Text = message;
        window.Show();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var handle = TryGetPlatformHandle();
        if (handle != null && handle.Handle != 0)
        {
            // 附加 WS_EX_NOACTIVATE 与 WS_EX_TOOLWINDOW，确保不抢前台焦点且不进任务栏
            var exStyle = NativeMethods.GetWindowLongPtr(handle.Handle, NativeMethods.GWL_EXSTYLE);
            exStyle = (nint)((long)exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            NativeMethods.SetWindowLongPtr(handle.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

            // 靠屏幕右下角/任务栏定位
            PositionNearTaskbar(handle.Handle);
        }

        _dismissTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismissTimer.Stop();
        base.OnClosed(e);
    }

    private void PositionNearTaskbar(nint hWnd)
    {
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);

        if (width <= 0 || height <= 0)
        {
            width = (int)(360 * scaling);
            height = (int)(90 * scaling);
        }

        const int margin = 16;
        var appbar = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        var ok = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref appbar);
        int targetX;
        int targetY;

        if (ok != 0 && appbar.rc.Width > 0 && appbar.rc.Height > 0)
        {
            // 任务栏在底部
            if (appbar.uEdge == 3 /* ABE_BOTTOM */)
            {
                targetX = appbar.rc.Right - width - margin;
                targetY = appbar.rc.Top - height - margin;
            }
            // 任务栏在顶部
            else if (appbar.uEdge == 1 /* ABE_TOP */)
            {
                targetX = appbar.rc.Right - width - margin;
                targetY = appbar.rc.Bottom + margin;
            }
            // 任务栏在左侧
            else if (appbar.uEdge == 0 /* ABE_LEFT */)
            {
                targetX = appbar.rc.Right + margin;
                targetY = appbar.rc.Bottom - height - margin;
            }
            // 任务栏在右侧
            else
            {
                targetX = appbar.rc.Left - width - margin;
                targetY = appbar.rc.Bottom - height - margin;
            }
        }
        else
        {
            // 回退到主屏工作区右下角
            var primaryScreen = Screens.Primary;
            var workArea = primaryScreen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            targetX = workArea.Right - width - margin;
            targetY = workArea.Bottom - height - margin;
        }

        NativeMethods.SetWindowPos(
            hWnd,
            NativeMethods.HWND_TOPMOST,
            targetX,
            targetY,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _dismissTimer.Stop();
        Close();
    }
}
