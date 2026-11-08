using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace NaniKore.App.Controls;

/// <summary>
/// 无边框窗口（<c>WindowDecorations="None"</c>，自绘卡片 + 透明背景）的缩放握柄层。
/// <para>
/// 关掉系统装饰会连系统缩放边框一并失去：窗口只能整窗拖动、无法改尺寸。
/// 系统自带的 <see cref="Window.BeginResizeDrag"/> 走的是 <c>WM_NCLBUTTONDOWN</c> 缩放回路，
/// 而该回路要求窗口带 <c>WS_THICKFRAME</c>（<c>WindowDecorations="None"</c> 的窗口没有），因此这里改为自己驱动：
/// 沿四边与四角铺 8 个透明命中区，按下捕获指针，按屏幕位移改窗口尺寸，西/北边同时反向位移窗口位置，
/// 让对边保持不动。
/// </para>
/// <para>
/// 中间区域不挂任何画刷、不参与命中测试，绝不遮挡窗口内容；握柄也只在
/// <see cref="Window.CanResize"/> 且窗口处于 <see cref="WindowState.Normal"/> 时生效。
/// </para>
/// </summary>
internal sealed class WindowResizeGrip : Panel
{
    /// <summary>握柄厚度（逻辑像素）：按得准，又不至于明显侵占内容。</summary>
    private const double GripThickness = 6;

    /// <summary>
    /// 四角命中区边长：取窗口卡片的圆角半径（12px）。圆角会把角落裁掉，
    /// 只有延伸到圆弧内侧的那一小块才可点，因此四角比四条边更宽；“刚好落在圆弧外”的透明像素仍然不响应，与原生圆角窗口一致。
    /// </summary>
    private const double CornerZoneSize = 12;

    /// <summary>窗口未声明最小尺寸时的防御性下限，避免被拖成不可操作的一线窗。</summary>
    private const double FallbackMinWidth = 120;

    private const double FallbackMinHeight = 90;

    /// <summary>8 个缩放区域，顺序与 <see cref="ZoneCursors"/>、<see cref="ArrangeOverride"/> 的摆位顺序一致。</summary>
    private static readonly WindowEdge[] Zones =
    [
        WindowEdge.NorthWest,
        WindowEdge.North,
        WindowEdge.NorthEast,
        WindowEdge.West,
        WindowEdge.East,
        WindowEdge.SouthWest,
        WindowEdge.South,
        WindowEdge.SouthEast,
    ];

    /// <summary>与 <see cref="Zones"/> 一一对应的光标。</summary>
    private static readonly Cursor[] ZoneCursors =
    [
        new(StandardCursorType.TopLeftCorner),
        new(StandardCursorType.SizeNorthSouth),
        new(StandardCursorType.TopRightCorner),
        new(StandardCursorType.SizeWestEast),
        new(StandardCursorType.SizeWestEast),
        new(StandardCursorType.BottomLeftCorner),
        new(StandardCursorType.SizeNorthSouth),
        new(StandardCursorType.BottomRightCorner),
    ];

    private readonly Border[] _grips = new Border[Zones.Length];

    // 拖拽会话状态：起点全部取屏幕坐标，窗口自身位移/改尺寸不会污染增量。
    private Window? _target;
    private IPointer? _capturedPointer;
    private PixelPoint _startPointerScreen;
    private PixelPoint _startWindowPosition;
    private double _startWidth;
    private double _startHeight;

    public WindowResizeGrip()
    {
        for (var i = 0; i < Zones.Length; i++)
        {
            var grip = new Border
            {
                // Transparent 而非 null：空画刷不参与命中测试，握柄必须能被点到。
                Background = Brushes.Transparent,
                Cursor = ZoneCursors[i],
                Tag = Zones[i],
            };
            grip.PointerPressed += OnGripPointerPressed;
            grip.PointerMoved += OnGripPointerMoved;
            grip.PointerReleased += OnGripPointerReleased;
            grip.PointerCaptureLost += OnGripPointerCaptureLost;
            _grips[i] = grip;
            Children.Add(grip);
        }
    }

    /// <summary>
    /// 只摆放不测量：握柄自身没有内容（期望尺寸为 0），尺寸全部来自宿主单元格的拉伸矩形，
    /// 因此不能用期望尺寸参与测量，否则会把 0 尺寸带回布局。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width);
        var height = Math.Max(0, finalSize.Height);
        var limit = Math.Min(width, height) / 2;
        // 窗口被压到极小（低于两倍握柄尺寸）时收缩握柄，避免相互重叠交出错误的方向。
        var thickness = Math.Min(GripThickness, limit);
        var corner = Math.Min(CornerZoneSize, limit);

        ArrangeZone(0, 0, 0, corner, corner); // 左上角
        ArrangeZone(1, corner, 0, width - 2 * corner, thickness); // 上边
        ArrangeZone(2, width - corner, 0, corner, corner); // 右上角
        ArrangeZone(3, 0, corner, thickness, height - 2 * corner); // 左边
        ArrangeZone(4, width - thickness, corner, thickness, height - 2 * corner); // 右边
        ArrangeZone(5, 0, height - corner, corner, corner); // 左下角
        ArrangeZone(6, corner, height - thickness, width - 2 * corner, thickness); // 下边
        ArrangeZone(7, width - corner, height - corner, corner, corner); // 右下角

        return finalSize;
    }

    private void ArrangeZone(int index, double x, double y, double width, double height) =>
        _grips[index].Arrange(new Rect(x, y, Math.Max(0, width), Math.Max(0, height)));

    private void OnGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: WindowEdge } grip ||
            TopLevel.GetTopLevel(this) is not Window window ||
            window.WindowState != WindowState.Normal ||
            !window.CanResize ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _target = window;
        _capturedPointer = e.Pointer;
        _startPointerScreen = window.PointToScreen(e.GetPosition(window));
        _startWindowPosition = window.Position;
        _startWidth = window.Bounds.Width;
        _startHeight = window.Bounds.Height;

        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border { Tag: WindowEdge edge } ||
            _target is not { } window ||
            _capturedPointer is null ||
            !ReferenceEquals(e.Pointer, _capturedPointer))
        {
            return;
        }

        var scaling = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var pointer = window.PointToScreen(e.GetPosition(window));
        var deltaX = (pointer.X - _startPointerScreen.X) / scaling;
        var deltaY = (pointer.Y - _startPointerScreen.Y) / scaling;

        var (width, height) = ResolveSize(
            edge,
            _startWidth,
            _startHeight,
            deltaX,
            deltaY,
            ResolveMin(window.MinWidth, FallbackMinWidth),
            ResolveMin(window.MinHeight, FallbackMinHeight),
            window.MaxWidth,
            window.MaxHeight);

        var position = ResolvePosition(edge, _startWindowPosition, _startWidth, _startHeight, width, height, scaling);

        if (position != window.Position)
        {
            window.Position = position;
        }

        if (SizeDiffers(window.Width, width))
        {
            window.Width = width;
        }

        if (SizeDiffers(window.Height, height))
        {
            window.Height = height;
        }

        e.Handled = true;
    }

    private void OnGripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndSession(e.Pointer);
        e.Handled = true;
    }

    private void OnGripPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndSession(e.Pointer);

    private void EndSession(IPointer pointer)
    {
        var captured = _capturedPointer;
        if (captured is null || !ReferenceEquals(pointer, captured))
        {
            return;
        }

        // 先清状态再释放捕获：Capture(null) 会同步回调 PointerCaptureLost，不清干净会重入。
        _capturedPointer = null;
        _target = null;
        captured.Capture(null);
    }

    /// <summary>尺寸是否真的需要写入；NaN（从未显式设过宽度）一律视为需要写入。</summary>
    private static bool SizeDiffers(double current, double target) =>
        !double.IsFinite(current) || Math.Abs(current - target) > 0.01;

    /// <summary>
    /// 缩放尺寸的纯决策：只有被拖动的方向才参与计算，对边保持不动；
    /// 结果按窗口声明的最小/最大尺寸收敛，避免拖出不可操作或越界的窗口。
    /// </summary>
    internal static (double Width, double Height) ResolveSize(
        WindowEdge edge,
        double startWidth,
        double startHeight,
        double deltaX,
        double deltaY,
        double minWidth,
        double minHeight,
        double maxWidth,
        double maxHeight)
    {
        var width = AffectsHorizontal(edge)
            ? startWidth + (IsLeftEdge(edge) ? -deltaX : deltaX)
            : startWidth;
        var height = AffectsVertical(edge)
            ? startHeight + (IsTopEdge(edge) ? -deltaY : deltaY)
            : startHeight;

        return (Clamp(width, minWidth, maxWidth), Clamp(height, minHeight, maxHeight));
    }

    /// <summary>
    /// 缩放时窗口位置的纯决策：只有西/北边参与缩放才需要反向位移窗口，使对边钉在原处；
    /// 位移量按被钳制后的真实尺寸换算成物理像素，避免触到最小尺寸时窗口继续漂移。
    /// </summary>
    internal static PixelPoint ResolvePosition(
        WindowEdge edge,
        PixelPoint startPosition,
        double startWidth,
        double startHeight,
        double width,
        double height,
        double scaling)
    {
        if (scaling <= 0 || !double.IsFinite(scaling))
        {
            scaling = 1;
        }

        var x = startPosition.X;
        var y = startPosition.Y;

        if (IsLeftEdge(edge))
        {
            x += (int)Math.Round((startWidth - width) * scaling);
        }

        if (IsTopEdge(edge))
        {
            y += (int)Math.Round((startHeight - height) * scaling);
        }

        return new PixelPoint(x, y);
    }

    private static bool AffectsHorizontal(WindowEdge edge) => edge is not (WindowEdge.North or WindowEdge.South);

    private static bool AffectsVertical(WindowEdge edge) => edge is not (WindowEdge.West or WindowEdge.East);

    private static bool IsLeftEdge(WindowEdge edge) =>
        edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;

    private static bool IsTopEdge(WindowEdge edge) =>
        edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;

    private static double ResolveMin(double declared, double fallback) =>
        double.IsFinite(declared) && declared > 0 ? declared : fallback;

    private static double Clamp(double value, double min, double max)
    {
        var clamped = Math.Max(min, value);
        return double.IsFinite(max) && max > 0 ? Math.Min(max, clamped) : clamped;
    }
}
