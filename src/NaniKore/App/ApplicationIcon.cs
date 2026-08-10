using System.Drawing;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NaniKore.App;

/// <summary>统一从嵌入资源加载应用图标，避免托盘、WinForms 和 WPF 窗口使用不同图标。</summary>
internal static class ApplicationIcon
{
    private const string ResourceName = "NaniKore.Assets.nanikore.ico";

    public static Icon LoadWinForms()
    {
        using var stream = OpenResource();
        using var icon = new Icon(stream);
        // 克隆后再释放资源流，避免 NotifyIcon 或 Form 继续使用图标时依赖已关闭的流。
        return (Icon)icon.Clone();
    }

    public static ImageSource LoadWpf()
    {
        using var stream = OpenResource();
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    private static Stream OpenResource() => Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"应用图标资源不存在：{ResourceName}");
}
