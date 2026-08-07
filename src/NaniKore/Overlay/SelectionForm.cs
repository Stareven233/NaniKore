using System.Drawing.Drawing2D;

namespace NaniKore.Overlay;

internal sealed class SelectionForm : Form
{
    private const int MinimumSelectionPixels = 4;
    private static readonly Color DimColor = Color.FromArgb(150, 0, 0, 0);
    private static readonly Color ChipColor = Color.FromArgb(210, 20, 20, 20);

    private readonly Bitmap _screenshot;
    private bool _dragging;
    private Point _start;
    private Point _current;

    public SelectionForm(Bitmap screenshot, Rectangle virtualBounds)
    {
        _screenshot = screenshot;

        // 浮层客户区必须与虚拟桌面截图逐像素重合，所有选区坐标才能直接用于 Bitmap.Clone。
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualBounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    public Rectangle? Selection { get; private set; }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        Activate();
        Focus();
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);

        eventArgs.Graphics.CompositingMode = CompositingMode.SourceCopy;
        eventArgs.Graphics.DrawImageUnscaled(_screenshot, Point.Empty);
        eventArgs.Graphics.CompositingMode = CompositingMode.SourceOver;

        var selection = CurrentSelection;
        using var dimBrush = new SolidBrush(DimColor);
        foreach (var region in SelectionGeometry.GetOutsideRegions(ClientRectangle, selection))
        {
            if (!region.IsEmpty)
            {
                eventArgs.Graphics.FillRectangle(dimBrush, region);
            }
        }

        if (selection.Width <= 0 || selection.Height <= 0)
        {
            return;
        }

        DrawSelectionBorder(eventArgs.Graphics, selection);
        DrawSizeChip(eventArgs.Graphics, selection);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);

        if (eventArgs.Button == MouseButtons.Right)
        {
            CancelSelection();
            return;
        }

        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _start = ClampToClient(eventArgs.Location);
        _current = _start;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (!_dragging)
        {
            return;
        }

        _current = ClampToClient(eventArgs.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (!_dragging || eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        _current = ClampToClient(eventArgs.Location);
        _dragging = false;
        Capture = false;

        var selection = CurrentSelection;
        if (selection.Width < MinimumSelectionPixels || selection.Height < MinimumSelectionPixels)
        {
            CancelSelection();
            return;
        }

        Selection = selection;
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CancelSelection();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private Rectangle CurrentSelection => SelectionGeometry.Normalize(_start, _current, ClientSize);

    private Point ClampToClient(Point point) => new(
        Math.Clamp(point.X, 0, ClientSize.Width),
        Math.Clamp(point.Y, 0, ClientSize.Height));

    private void CancelSelection()
    {
        Selection = null;
        _dragging = false;
        Capture = false;
        DialogResult = DialogResult.Cancel;
    }

    private static void DrawSelectionBorder(Graphics graphics, Rectangle selection)
    {
        using var borderPen = new Pen(Color.White, 2f);
        var border = Rectangle.FromLTRB(
            selection.Left,
            selection.Top,
            Math.Max(selection.Left + 1, selection.Right - 1),
            Math.Max(selection.Top + 1, selection.Bottom - 1));
        graphics.DrawRectangle(borderPen, border);
    }

    private void DrawSizeChip(Graphics graphics, Rectangle selection)
    {
        const int horizontalPadding = 7;
        const int verticalPadding = 4;
        const int gap = 6;

        var text = $"{selection.Width} × {selection.Height}";
        var textSize = TextRenderer.MeasureText(
            text,
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var chipSize = new Size(
            textSize.Width + horizontalPadding * 2,
            textSize.Height + verticalPadding * 2);

        var x = selection.Left;
        var y = selection.Bottom + gap;
        if (y + chipSize.Height > ClientSize.Height)
        {
            y = Math.Max(0, selection.Top - gap - chipSize.Height);
        }

        x = Math.Clamp(x, 0, Math.Max(0, ClientSize.Width - chipSize.Width));
        var chip = new Rectangle(new Point(x, y), chipSize);

        using var chipBrush = new SolidBrush(ChipColor);
        graphics.FillRectangle(chipBrush, chip);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            new Point(chip.Left + horizontalPadding, chip.Top + verticalPadding),
            Color.White,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
