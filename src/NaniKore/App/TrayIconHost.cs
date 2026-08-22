using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NaniKore.App.Theme;

namespace NaniKore.App;

/// <summary>托盘入口集中管理，退出统一交给 WPF Application.ExitThread 路径。</summary>
internal sealed class TrayIconHost : IDisposable
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public Guid Guid;
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rectangle rectangle);

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _disposed;

    public TrayIconHost(Action capture, Action settings, Action exit)
    {
        _menu = new ContextMenuStrip
        {
            // 托盘菜单是 WinForms 控件，样式需要显式跟随应用主题，否则深色下仍是白底方角菜单。
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            // 未勾选的普通菜单项不需要预留勾选栏，否则文字会相对整个菜单偏右。
            ShowCheckMargin = false,
            // 跟随系统菜单字体并稍微放大，保持原生可读性同时提升现代感。
            Font = new Font(SystemFonts.MenuFont?.FontFamily ?? SystemFonts.DialogFont.FontFamily, (SystemFonts.MenuFont?.Size ?? SystemFonts.DialogFont.Size) + 1.5f),
            Padding = new Padding(6, 6, 6, 6),
        };
        _menu.Items.Add("截图翻译", null, (_, _) => capture());
        _menu.Items.Add("设置", null, (_, _) => settings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());
        _menu.Opening += (_, args) =>
        {
            if (args.Cancel) return;
            // WinForms 下拉菜单是独立原生窗口，必须在显示前请求 DWM 系统圆角。
            var handle = _menu.Handle;
            var preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            if (!TryGetTrayIconBounds(out var iconBounds)) return;
            args.Cancel = true;
            // 菜单显示前必须先完成布局，否则拿不到准确的弹出宽度。
            _menu.PerformLayout();
            var screen = Screen.FromHandle(handle);
            var location = new Point(iconBounds.Right + 4, iconBounds.Top - 2);
            location.Y = Math.Min(location.Y, screen.WorkingArea.Bottom - _menu.Height);
            location.X = Math.Max(screen.WorkingArea.Left, Math.Min(location.X, screen.WorkingArea.Right - _menu.Width));
            _menu.Show(location);
        };
        ApplyMenuTheme();
        ThemeManager.ThemeChanged += OnThemeChanged;

        _icon = new NotifyIcon
        {
            Text = "NaniKore",
            Icon = ApplicationIcon.LoadWinForms(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => settings();
    }

    public void ShowWarning(string text) => _icon.ShowBalloonTip(4_000, "NaniKore", text, ToolTipIcon.Warning);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ThemeManager.ThemeChanged -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _disposed = true;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyMenuTheme();

    /// <summary>通过共享托盘 API 查询图标的物理矩形，避免猜测鼠标位置。</summary>
    private bool TryGetTrayIconBounds(out Rectangle bounds)
    {
        bounds = default;
        if (_icon.Visible == false) return false;
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            WindowHandle = _icon.GetType().GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_icon) is IntPtr handle ? handle : IntPtr.Zero,
            Id = Convert.ToUInt32(_icon.GetType().GetField("id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_icon) ?? 0u),
        };
        if (identifier.WindowHandle == IntPtr.Zero || identifier.Id == 0) return false;
        return Shell_NotifyIconGetRect(ref identifier, out bounds) == 0;
    }

    /// <summary>ToolStrip 的背景与文字色不走渲染器，必须在主题切换后直接赋值。</summary>
    private void ApplyMenuTheme()
    {
        _menu.BackColor = TrayMenuRenderer.Resolve("Brush.Card.Background", SystemColors.Menu);
        _menu.ForeColor = TrayMenuRenderer.Resolve("Brush.Text.Primary", SystemColors.MenuText);
        foreach (var item in _menu.Items.OfType<ToolStripItem>())
        {
            item.BackColor = _menu.BackColor;
            item.ForeColor = _menu.ForeColor;
            // 加大菜单项点击区域；分隔线只保留少量纵向留白。
            if (item is ToolStripSeparator)
            {
                item.Padding = new Padding(0, 4, 0, 4);
            }
            else
            {
                item.Padding = new Padding(16, 9, 16, 9);
            }
        }
    }
}
