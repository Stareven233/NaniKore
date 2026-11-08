using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NaniKore.Config;

namespace NaniKore.App.Theme;

/// <summary>两个窗口共用同一份已解码图片；动态资源让保存后的背景立即生效。</summary>
internal static class WindowBackgroundResources
{
    private static Bitmap? _bitmap;
    private static string? _loadedPath;
    private static DateTime _lastWriteTime;

    internal static string ResolvePath(string path) => Path.GetFullPath(path.Trim(), AppContext.BaseDirectory);

    internal static Bitmap LoadImage(string path) => new(ResolvePath(path));

    public static void Apply(WindowConfig config)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        Bitmap? next = null;
        string? path = null;
        var modified = default(DateTime);
        try
        {
            if (!string.IsNullOrWhiteSpace(config.BackgroundImage))
            {
                path = ResolvePath(config.BackgroundImage);
                modified = File.GetLastWriteTimeUtc(path);
                // 尺寸记忆也会保存配置；图片未变时复用解码结果，不重复读盘。
                next = path == _loadedPath && modified == _lastWriteTime && _bitmap is not null
                    ? _bitmap
                    : new Bitmap(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 便携目录中的图片可能被移动或损坏：仅停用图片层，保留路径供用户修正。
            path = null;
        }

        var previous = _bitmap;
        _bitmap = next;
        _loadedPath = path;
        _lastWriteTime = modified;
        app.Resources["WindowBackground.Image"] = next;
        app.Resources["WindowBackground.Opacity"] = config.BackgroundOpacity;
        app.Resources["WindowBackground.Blur"] = new BlurEffect { Radius = config.BackgroundBlur };
        if (!ReferenceEquals(previous, next))
        {
            previous?.Dispose();
        }
    }
}
