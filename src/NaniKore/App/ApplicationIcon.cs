using System.Reflection;
using Avalonia.Controls;

namespace NaniKore.App;

/// <summary>
/// 统一从嵌入资源加载应用图标，供 Avalonia 窗口与系统托盘使用。
/// 彻底摆脱 System.Drawing.Icon 与 WPF ImageSource 历史依赖。
/// </summary>
internal static class ApplicationIcon
{
    private const string ResourceName = "NaniKore.Assets.nanikore.ico";

    /// <summary>
    /// 加载 Avalonia 统一使用的 WindowIcon。
    /// </summary>
    public static WindowIcon Load()
    {
        using var stream = OpenResource();
        return new WindowIcon(stream);
    }

    private static Stream OpenResource() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"应用图标资源不存在：{ResourceName}");
}
