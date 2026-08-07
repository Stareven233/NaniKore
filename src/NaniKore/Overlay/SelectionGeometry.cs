namespace NaniKore.Overlay;

internal static class SelectionGeometry
{
    public static Rectangle Normalize(Point first, Point second, Size bounds)
    {
        var left = Math.Clamp(Math.Min(first.X, second.X), 0, bounds.Width);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), 0, bounds.Height);
        var right = Math.Clamp(Math.Max(first.X, second.X), 0, bounds.Width);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, bounds.Height);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static Rectangle[] GetOutsideRegions(Rectangle bounds, Rectangle selection)
    {
        selection.Intersect(bounds);
        if (selection.IsEmpty)
        {
            return [bounds];
        }

        return
        [
            Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, selection.Top),
            Rectangle.FromLTRB(bounds.Left, selection.Bottom, bounds.Right, bounds.Bottom),
            Rectangle.FromLTRB(bounds.Left, selection.Top, selection.Left, selection.Bottom),
            Rectangle.FromLTRB(selection.Right, selection.Top, bounds.Right, selection.Bottom),
        ];
    }
}
