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
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2; // 系统大圆角偏好（Windows 11）

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
        IsDark = ReadSystemUsesDarkMode();
        application.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>卸载事件监听。</summary>
    public static void Shutdown()
    {
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    /// <summary>让窗口标题栏跟随深浅色主题，并启用 Windows 11 DWM 系统级平滑大圆角与柔和阴影。</summary>
    public static void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        void Refresh(object? sender, EventArgs e) => ApplyTitleBar(window);
        void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                if (window.TryGetPlatformHandle()?.Handle is { } h && h != nint.Zero)
                {
                    ApplyWin10RoundedCorners(window, h);
                }
            }
        }
        void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                if (window.TryGetPlatformHandle()?.Handle is { } h && h != nint.Zero)
                {
                    ApplyWin10RoundedCorners(window, h);
                }
            }
        }

        // 若 HWND 已就绪立即调用；若尚未呈现则挂接 Opened 事件，确保窗口创建后 100% 获得深色标题栏与大圆角
        if (window.TryGetPlatformHandle()?.Handle is { } handle && handle != nint.Zero)
        {
            ApplyTitleBar(window);
        }
        else
        {
            window.Opened += (_, _) => ApplyTitleBar(window);
        }

        ThemeChanged += Refresh;
        window.SizeChanged += OnSizeChanged;
        window.PropertyChanged += OnPropertyChanged;
        window.Closed += (_, _) =>
        {
            ThemeChanged -= Refresh;
            window.SizeChanged -= OnSizeChanged;
            window.PropertyChanged -= OnPropertyChanged;
        };
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

        // 深色沉浸式标题栏
        var useDark = IsDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));

        // Windows 11 (22000+)：显式指定 DWM 圆角偏好为优雅大圆角（DWMWCP_ROUND = 2）
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var roundCorner = DwmwcpRound;
            _ = NativeMethods.DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref roundCorner, sizeof(int));
        }
        else
        {
            // Windows 10：DWM 不支持 DWMWA_WINDOW_CORNER_PREFERENCE，通过 SetWindowRgn 实施平滑圆角裁切
            ApplyWin10RoundedCorners(window, handle);
        }
    }

    /// <summary>在 Windows 10 系统上通过 Win32 Region 实施窗口圆角（兼容全屏最大化重置）。</summary>
    private static void ApplyWin10RoundedCorners(Window window, nint handle)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            // 最大化状态还原全屏直角，避免边缘内容被裁切
            _ = NativeMethods.SetWindowRgn(handle, nint.Zero, true);
            return;
        }

        if (NativeMethods.GetWindowRect(handle, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width > 0 && height > 0)
            {
                // 16px 直径（8px 半径）平滑圆角矩形区域
                var rgn = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, 16, 16);
                if (rgn != nint.Zero)
                {
                    _ = NativeMethods.SetWindowRgn(handle, rgn, true);
                }
            }
        }
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
