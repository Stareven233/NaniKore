using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NaniKore.App.Theme;
using NaniKore.Interop;
using SkiaSharp;
using Color = Avalonia.Media.Color;
using Pen = Avalonia.Media.Pen;
using Point = Avalonia.Point;
using Brushes = Avalonia.Media.Brushes;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace NaniKore.Overlay;

/// <summary>
/// 全虚拟屏幕无边框置顶选区浮层：在冻结画面上提供 1:1 物理映射的矩形框选交互，
/// 附带暗色蒙版与尺寸提示 Chip，按 Esc 或鼠标右键取消。
/// </summary>
internal sealed partial class SelectionOverlayWindow : Window
{
    private const int MinimumSelectionPixels = 4;
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush ChipBrush = new SolidColorBrush(Color.FromArgb(210, 20, 20, 20));
    private static readonly IPen BorderPen = new Pen(Brushes.White, 2);

    private readonly PixelRect _virtualBounds;
    private readonly WriteableBitmap _displayBitmap;
    private bool _dragging;
    private PixelPoint _start;
    private PixelPoint _current;

    public SelectionOverlayWindow(SKBitmap screenshot, PixelRect virtualBounds)
    {
        InitializeComponent();
        _virtualBounds = virtualBounds;

        Cursor = new Cursor(StandardCursorType.Cross);

        // 将底层 SKBitmap 像素直接复制到 Avalonia WriteableBitmap
        _displayBitmap = new WriteableBitmap(
            new PixelSize(virtualBounds.Width, virtualBounds.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        unsafe
        {
            using var fb = _displayBitmap.Lock();
            var src = (void*)screenshot.GetPixels();
            var dst = (void*)fb.Address;
            var bytesToCopy = (long)virtualBounds.Width * virtualBounds.Height * 4;
            Buffer.MemoryCopy(src, dst, bytesToCopy, bytesToCopy);
        }

        Position = new PixelPoint(virtualBounds.X, virtualBounds.Y);

        KeyDown += OnWindowKeyDown;
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        PointerMoved += OnWindowPointerMoved;
    }

    /// <summary>
    /// 框选结束后的物理选区矩形（相对全虚拟桌面左上角的坐标与尺寸）。取消时为 null。
    /// </summary>
    public PixelRect? SelectionResult { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 通过底层 HWND 强制物理对齐到虚拟桌面的真实范围（抹平跨屏负坐标与混合 DPI 偏差）
        var handle = TryGetPlatformHandle();
        if (handle != null && handle.Handle != 0)
        {
            NativeMethods.SetWindowPos(
                handle.Handle,
                NativeMethods.HWND_TOPMOST,
                _virtualBounds.X,
                _virtualBounds.Y,
                _virtualBounds.Width,
                _virtualBounds.Height,
                NativeMethods.SWP_SHOWWINDOW);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _displayBitmap.Dispose();
        base.OnClosed(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var clientBounds = Bounds;

        // 1. 绘制底层截图画面
        context.DrawImage(_displayBitmap, new Rect(0, 0, clientBounds.Width, clientBounds.Height));

        var selection = CurrentSelection;

        // 2. 绘制选区外 4 个互斥区域的半透明暗色蒙版
        var outsideRegions = SelectionGeometry.GetOutsideRegions(
            new PixelRect(0, 0, _virtualBounds.Width, _virtualBounds.Height),
            selection);

        foreach (var region in outsideRegions)
        {
            if (region.Width > 0 && region.Height > 0)
            {
                var dipRect = new Rect(
                    region.X / scaling,
                    region.Y / scaling,
                    region.Width / scaling,
                    region.Height / scaling);
                context.FillRectangle(DimBrush, dipRect);
            }
        }

        if (selection.Width <= 0 || selection.Height <= 0)
        {
            return;
        }

        // 3. 绘制 2px 白色选区边框
        var selectionDip = new Rect(
            selection.X / scaling,
            selection.Y / scaling,
            selection.Width / scaling,
            selection.Height / scaling);
        context.DrawRectangle(BorderPen, selectionDip);

        // 4. 绘制尺寸标签 Chip
        DrawSizeChip(context, selection, scaling);
    }

    /// <summary>
    /// 尺寸 chip 的 Typeface（issue 033）：跟随配置的界面字体；资源缺失时仍用平台默认，
    /// 不让字体配置阻断选区浮层这条主路径。
    /// </summary>
    internal static Typeface ResolveChipTypeface() => FontResources.UiTypeface;

    private void DrawSizeChip(DrawingContext context, PixelRect selection, double scaling)
    {
        const double horizontalPadding = 7;
        const double verticalPadding = 4;
        const double gap = 6;

        var text = $"{selection.Width} × {selection.Height}";
        var formattedText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            ResolveChipTypeface(),
            12,
            Brushes.White);

        var chipWidth = formattedText.Width + horizontalPadding * 2;
        var chipHeight = formattedText.Height + verticalPadding * 2;

        var selectionDip = new Rect(
            selection.X / scaling,
            selection.Y / scaling,
            selection.Width / scaling,
            selection.Height / scaling);

        var x = selectionDip.Left;
        var y = selectionDip.Bottom + gap;

        if (y + chipHeight > Bounds.Height)
        {
            y = Math.Max(0, selectionDip.Top - gap - chipHeight);
        }

        x = Math.Clamp(x, 0, Math.Max(0, Bounds.Width - chipWidth));
        var chipRect = new Rect(x, y, chipWidth, chipHeight);

        context.FillRectangle(ChipBrush, chipRect);
        context.DrawText(formattedText, new Point(x + horizontalPadding, y + verticalPadding));
    }

    private PixelPoint ClampToVirtual(PixelPoint point)
    {
        return new PixelPoint(
            Math.Clamp(point.X, 0, _virtualBounds.Width),
            Math.Clamp(point.Y, 0, _virtualBounds.Height));
    }

    private PixelPoint GetPhysicalPoint(PointerEventArgs e)
    {
        var dip = e.GetPosition(this);
        var screen = this.PointToScreen(dip);
        return ClampToVirtual(new PixelPoint(screen.X - _virtualBounds.X, screen.Y - _virtualBounds.Y));
    }

    private PixelRect CurrentSelection => SelectionGeometry.Normalize(
        _start,
        _current,
        new PixelSize(_virtualBounds.Width, _virtualBounds.Height));

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSelection();
            e.Handled = true;
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            CancelSelection();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            _dragging = true;
            _start = GetPhysicalPoint(e);
            _current = _start;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            CancelSelection();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _current = GetPhysicalPoint(e);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (e.InitialPressMouseButton == MouseButton.Right || props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            CancelSelection();
            e.Handled = true;
            return;
        }

        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _current = GetPhysicalPoint(e);
        _dragging = false;
        e.Pointer.Capture(null);

        var selection = CurrentSelection;
        if (selection.Width < MinimumSelectionPixels || selection.Height < MinimumSelectionPixels)
        {
            CancelSelection();
        }
        else
        {
            SelectionResult = selection;
            Close();
        }

        e.Handled = true;
    }

    private void CancelSelection()
    {
        SelectionResult = null;
        _dragging = false;
        Close();
    }
}
