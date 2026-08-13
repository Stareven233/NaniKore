using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using NaniKore.Interop;
// WinForms 与 WPF 同时启用，隐式 using 会让 Application 等类型撞名；主题层一律使用 WPF 类型。
using Application = System.Windows.Application;

namespace NaniKore.App.Theme;

/// <summary>
/// 全应用主题入口：只跟随系统深浅色切换对应色板，把色板合并进 <see cref="Application.Resources"/>，
/// 并在系统设置变化时热切换。强调色等品牌色由色板固定提供，不再读取系统个性化强调色，
/// 以保证品牌观感稳定。所有控件样式只引用语义化画刷键，故切换时无需重建窗口。
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;

    private static readonly Uri LightPalette = new("/App/Theme/Palette.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("/App/Theme/Palette.Dark.xaml", UriKind.Relative);
    private static readonly Uri BaseControls = new("/App/Theme/Controls.Base.xaml", UriKind.Relative);
    private static readonly Uri InputControls = new("/App/Theme/Controls.Input.xaml", UriKind.Relative);

    private static ResourceDictionary? _paletteSlot;
    private static bool _initialized;

    /// <summary>当前是否为深色主题；托盘菜单等非 WPF 宿主据此取色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题切换完成后触发，供 WinForms 侧（托盘菜单）与 DWM 标题栏同步刷新。</summary>
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
        _paletteSlot = LoadPalette(IsDark);
        application.Resources.MergedDictionaries.Add(_paletteSlot);
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = BaseControls });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = InputControls });
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>
    /// 让窗口标题栏跟随主题。WPF 只能控制客户区，标题栏必须交给 DWM，
    /// 否则深色主题下会出现「白色标题栏 + 深色内容」的割裂观感。
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void Refresh(object? sender, EventArgs e) => ApplyTitleBar(window);

        ApplyTitleBar(window);
        window.SourceInitialized += Refresh;
        ThemeChanged += Refresh;
        // 必须持有同一委托实例才能真正退订，否则关闭的窗口会一直挂在静态事件上。
        window.Closed += (_, _) =>
        {
            window.SourceInitialized -= Refresh;
            ThemeChanged -= Refresh;
        };
    }

    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var useDark = IsDark ? 1 : 0;
        // 旧版本 Windows 不支持该属性；返回值非 0 时忽略即可，不影响客户区主题。
        _ = NativeMethods.DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var application = Application.Current;
        if (application is null || _paletteSlot is null)
        {
            return;
        }

        // 系统广播可能早于注册表写入完成，交回 UI 线程时再读一次当前值。
        application.Dispatcher.BeginInvoke(() =>
        {
            var dark = ReadSystemUsesDarkMode();
            var next = LoadPalette(dark);
            var index = application.Resources.MergedDictionaries.IndexOf(_paletteSlot!);
            if (index < 0)
            {
                return;
            }

            IsDark = dark;
            application.Resources.MergedDictionaries[index] = next;
            _paletteSlot = next;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        });
    }

    private static ResourceDictionary LoadPalette(bool dark) =>
        new() { Source = dark ? DarkPalette : LightPalette };

    private static bool ReadSystemUsesDarkMode()
    {
        try
        {
            // 值为 0 表示应用使用深色；键缺失时按浅色处理。
            return Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
