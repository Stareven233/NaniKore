using System.Drawing;
using System.Windows.Forms;
using NaniKore.App.Theme;

namespace NaniKore.App;

/// <summary>托盘入口集中管理，退出统一交给 WPF Application.ExitThread 路径。</summary>
internal sealed class TrayIconHost : IDisposable
{
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
            Padding = new Padding(0, 4, 0, 4),
        };
        _menu.Items.Add("截图翻译", null, (_, _) => capture());
        _menu.Items.Add("设置", null, (_, _) => settings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());
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

    /// <summary>ToolStrip 的背景与文字色不走渲染器，必须在主题切换后直接赋值。</summary>
    private void ApplyMenuTheme()
    {
        _menu.BackColor = TrayMenuRenderer.Resolve("Brush.Card.Background", SystemColors.Menu);
        _menu.ForeColor = TrayMenuRenderer.Resolve("Brush.Text.Primary", SystemColors.MenuText);
        foreach (var item in _menu.Items.OfType<ToolStripItem>())
        {
            item.BackColor = _menu.BackColor;
            item.ForeColor = _menu.ForeColor;
        }
    }
}
