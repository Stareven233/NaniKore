using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Win32;
using NaniKore.Interop;

namespace NaniKore.App.Theme;

/// <summary>
/// 全应用主题入口：跟随系统深浅色切换对应 ThemeVariant，
/// 并在系统设置变化时热切换。强调色与语义画刷由 Palette.Dark.axaml / Palette.Light.axaml 提供。
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;

    private static readonly List<string> LoadedFontFiles = [];
    private static bool _initialized;

    /// <summary>当前是否为深色主题。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题切换完成后触发，供 DWM 标题栏与外部组件同步刷新。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>在 <see cref="Application"/> 启动时调用一次；重复调用无副作用。</summary>
    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // 优先探测并加载主人指定的现代无衬线中英混排字体（私有加载无需管理员权限）
        EnsureCustomFonts();

        IsDark = ReadSystemUsesDarkMode();
        application.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>卸载事件监听并释放私有载入的字体资源。</summary>
    public static void Shutdown()
    {
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            foreach (var fontPath in LoadedFontFiles)
            {
                try
                {
                    _ = NativeMethods.RemoveFontResourceEx(fontPath, NativeMethods.FR_PRIVATE, 0);
                }
                catch
                {
                }
            }
            LoadedFontFiles.Clear();
        }
    }

    private static void EnsureCustomFonts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 探测本地候选字体文件（优先主人机器上的 F:\t\YaHeiMonacoHybrid.ttf 以及应用 deps 目录）
        string[] candidateFontPaths = [
            @"F:\t\YaHeiMonacoHybrid.ttf",
            System.IO.Path.Combine(AppContext.BaseDirectory, "deps", "fonts", "YaHeiMonacoHybrid.ttf")
        ];

        foreach (var fontPath in candidateFontPaths)
        {
            if (System.IO.File.Exists(fontPath) && !LoadedFontFiles.Contains(fontPath))
            {
                var result = NativeMethods.AddFontResourceEx(fontPath, NativeMethods.FR_PRIVATE, 0);
                if (result > 0)
                {
                    LoadedFontFiles.Add(fontPath);
                }
            }
        }
    }

    /// <summary>让窗口标题栏跟随深浅色主题（Windows DWM）。</summary>
    public static void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        void Refresh(object? sender, EventArgs e) => ApplyTitleBar(window);

        ApplyTitleBar(window);
        ThemeChanged += Refresh;
        window.Closed += (_, _) => ThemeChanged -= Refresh;
    }

    private static void ApplyTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            return;
        }

        var useDark = IsDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var dark = ReadSystemUsesDarkMode();
            if (dark == IsDark)
            {
                return;
            }

            IsDark = dark;
            application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        });
    }

    private static bool ReadSystemUsesDarkMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true; // 默认深色
        }

        try
        {
            return Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
