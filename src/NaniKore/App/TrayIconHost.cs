using System.Windows.Forms;

namespace NaniKore.App;

/// <summary>托盘入口集中管理，退出统一交给 WPF Application.ExitThread 路径。</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _disposed;

    public TrayIconHost(Action capture, Action settings, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("截图翻译", null, (_, _) => capture());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = "NaniKore",
            Icon = ApplicationIcon.LoadWinForms(),
            ContextMenuStrip = menu,
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

        _icon.Visible = false;
        _icon.Dispose();
        _disposed = true;
    }

}
