using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
// 托盘菜单是 WinForms 控件，这里的 Color 必须是 GDI+ 类型。
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace NaniKore.App.Theme;

/// <summary>
/// 托盘菜单渲染器：ContextMenuStrip 是 WinForms 控件，无法套用 WPF 样式，
/// 深色主题下默认渲染会是白底方角菜单，与应用其余部分割裂，因此手工按当前色板绘制。
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer()
        : base(new TrayColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>从 WPF 主题资源取色，保证托盘菜单与设置页、结果卡完全同一套配色。</summary>
    internal static Color Resolve(string brushKey, Color fallback)
    {
        if (WpfApplication.Current?.TryFindResource(brushKey) is SolidColorBrush brush)
        {
            var color = brush.Color;
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        return fallback;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var background = new SolidBrush(Resolve("Brush.Card.Background", SystemColors.Menu));
        e.Graphics.FillRectangle(background, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var pen = new Pen(Resolve("Brush.Card.Border", SystemColors.ControlDark));
        var bounds = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        // 悬停高亮画成圆角，和 WPF 侧的列表项观感一致。
        var bounds = new Rectangle(4, 2, e.Item.Bounds.Width - 8, e.Item.Bounds.Height - 4);
        using var highlight = new SolidBrush(Resolve("Brush.Accent.Subtle", SystemColors.Highlight));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(bounds, 5);
        e.Graphics.FillPath(highlight, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var textColor = e.Item.Enabled
            ? Resolve("Brush.Text.Primary", SystemColors.MenuText)
            : Resolve("Brush.Text.Disabled", SystemColors.GrayText);

        // TextRenderer 的绘制画布已经被 WinForms 平移到当前菜单项，坐标必须从 (0, 0) 起算。
        // 菜单项可能因为下拉菜单的边距而整体右移；取宿主客户端宽度并减去项的 X 偏移，
        // 才能相对整个可见菜单水平居中，而不是相对右移后的项目区域居中。
        var menuWidth = e.Item.Owner?.ClientSize.Width ?? e.Item.Width;
        var textBounds = new Rectangle(
            -e.Item.Bounds.X,
            0,
            Math.Max(0, menuWidth),
            e.Item.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Text,
            e.TextFont,
            textBounds,
            textColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var pen = new Pen(Resolve("Brush.Divider", SystemColors.ControlDark));
        var y = e.Item.Bounds.Height / 2;
        e.Graphics.DrawLine(pen, 14, y, e.Item.Bounds.Width - 14, y);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>下拉边框与背景交给同一套语义色；其余项由重写的绘制方法接管。</summary>
    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Resolve("Brush.Card.Border", SystemColors.ControlDark);

        public override Color ToolStripDropDownBackground => Resolve("Brush.Card.Background", SystemColors.Menu);

        public override Color ImageMarginGradientBegin => Resolve("Brush.Card.Background", SystemColors.Menu);

        public override Color ImageMarginGradientMiddle => Resolve("Brush.Card.Background", SystemColors.Menu);

        public override Color ImageMarginGradientEnd => Resolve("Brush.Card.Background", SystemColors.Menu);
    }
}
