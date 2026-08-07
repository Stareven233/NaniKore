using System.Drawing.Imaging;

namespace NaniKore.Capture;

internal static class DesktopCapturer
{
    public static DesktopFrame CaptureVirtualScreen()
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0)
        {
            throw new InvalidOperationException($"Invalid virtual screen bounds: {virtualBounds}");
        }

        var bitmap = new Bitmap(
            virtualBounds.Width,
            virtualBounds.Height,
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                virtualBounds.Location,
                Point.Empty,
                virtualBounds.Size,
                CopyPixelOperation.SourceCopy);

            return new DesktopFrame(bitmap, virtualBounds);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}

internal sealed record DesktopFrame(Bitmap Image, Rectangle VirtualBounds) : IDisposable
{
    public void Dispose() => Image.Dispose();
}
