using Avalonia;

namespace NaniKore.Overlay;

/// <summary>
/// 选区几何计算：纯几何与矩形归一化。
/// 基于 Avalonia PixelPoint / PixelSize / PixelRect 体系，彻底摆脱 System.Drawing。
/// </summary>
internal static class SelectionGeometry
{
    public static PixelRect Normalize(PixelPoint first, PixelPoint second, PixelSize bounds)
    {
        var left = Math.Clamp(Math.Min(first.X, second.X), 0, bounds.Width);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), 0, bounds.Height);
        var right = Math.Clamp(Math.Max(first.X, second.X), 0, bounds.Width);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, bounds.Height);

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static PixelRect[] GetOutsideRegions(PixelRect bounds, PixelRect selection)
    {
        var intersected = bounds.Intersect(selection);
        if (intersected.Width <= 0 || intersected.Height <= 0)
        {
            return [bounds];
        }

        var sLeft = intersected.X;
        var sTop = intersected.Y;
        var sRight = intersected.Right;
        var sBottom = intersected.Bottom;

        return
        [
            // 上方区域
            new PixelRect(bounds.X, bounds.Y, bounds.Width, sTop - bounds.Y),
            // 下方区域
            new PixelRect(bounds.X, sBottom, bounds.Width, bounds.Bottom - sBottom),
            // 左侧中间区域
            new PixelRect(bounds.X, sTop, sLeft - bounds.X, intersected.Height),
            // 右侧中间区域
            new PixelRect(sRight, sTop, bounds.Right - sRight, intersected.Height),
        ];
    }
}
