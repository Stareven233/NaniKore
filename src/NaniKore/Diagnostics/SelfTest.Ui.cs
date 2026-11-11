using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Color = Avalonia.Media.Color;
using Colors = Avalonia.Media.Colors;
using NaniKore.App.Theme;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（UI 套件）：Avalonia 12 渲染底座、FluentTheme 资源解析、深浅色模式与对比度校验。
/// 结果面板与设置页相关 UI 自测将在 P3/P4 对应批次按 Avalonia 重写并重新接入。
/// </summary>
internal static partial class SelfTest
{
    private static void RunUiTests()
    {
        EnsureThemedApplication();
        TestThemeResourcesResolve();
        TestContrastRatiosAndReadability();
        TestThemeVariantHotSwitching();
        RunResultPanelUiTests();
        RunSettingsUiTests();
        RunFavoriteFailureUiTests();
    }

    /// <summary>
    /// 无 UI 自测中初始化无头 Avalonia Application，承载主题与样式字典解析。
    /// </summary>
    private static void EnsureThemedApplication()
    {
        if (Application.Current is null)
        {
            AppBuilder.Configure<NaniKoreApp>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }
    }

    /// <summary>验证色板语义资源键在深浅色模式下的解析与键值一致性。</summary>
    private static void TestThemeResourcesResolve()
    {
        var app = Application.Current;
        Check("Palette.AppNotNull", app is not null);
        if (app is null)
        {
            return;
        }

        // 1. 深色色板解析
        app.RequestedThemeVariant = ThemeVariant.Dark;
        var darkBg = TryFindResource<IBrush>(app, "Brush.Window.Background", ThemeVariant.Dark);
        var darkCardBg = TryFindResource<IBrush>(app, "Brush.Card.Background", ThemeVariant.Dark);
        var darkAccent = TryFindResource<IBrush>(app, "Brush.Accent", ThemeVariant.Dark);
        var darkText = TryFindResource<IBrush>(app, "Brush.Text.Primary", ThemeVariant.Dark);

        Check("Palette.Dark.WindowBgFound", darkBg is not null);
        Check("Palette.Dark.CardBgFound", darkCardBg is not null);
        Check("Palette.Dark.AccentFound", darkAccent is not null);
        Check("Palette.Dark.TextPrimaryFound", darkText is not null);

        if (darkAccent is ISolidColorBrush scbDarkAccent)
        {
            Check("Palette.Dark.AccentColorValue",
                scbDarkAccent.Color.R == 0x8E && scbDarkAccent.Color.G == 0xD2 && scbDarkAccent.Color.B == 0x00);
        }

        // 2. 浅色色板解析
        app.RequestedThemeVariant = ThemeVariant.Light;
        var lightBg = TryFindResource<IBrush>(app, "Brush.Window.Background", ThemeVariant.Light);
        var lightCardBg = TryFindResource<IBrush>(app, "Brush.Card.Background", ThemeVariant.Light);
        var lightAccent = TryFindResource<IBrush>(app, "Brush.Accent", ThemeVariant.Light);
        var lightText = TryFindResource<IBrush>(app, "Brush.Text.Primary", ThemeVariant.Light);

        Check("Palette.Light.WindowBgFound", lightBg is not null);
        Check("Palette.Light.CardBgFound", lightCardBg is not null);
        Check("Palette.Light.AccentFound", lightAccent is not null);
        Check("Palette.Light.TextPrimaryFound", lightText is not null);

        if (lightAccent is ISolidColorBrush scbLightAccent)
        {
            Check("Palette.Light.AccentColorValue",
                scbLightAccent.Color.R == 0x78 && scbLightAccent.Color.G == 0xC8 && scbLightAccent.Color.B == 0x00);
        }

        // 切回默认深色
        app.RequestedThemeVariant = ThemeVariant.Dark;
    }

    /// <summary>验证品牌强调色与主文本在深浅色底色上的 WCAG AAA 对比度（7:1 规范）。</summary>
    private static void TestContrastRatiosAndReadability()
    {
        var app = Application.Current;
        if (app is null) return;

        // 深色主文本 vs 深色底
        var darkBg = (TryFindResource<IBrush>(app, "Brush.Window.Background", ThemeVariant.Dark) as ISolidColorBrush)?.Color ?? Colors.Black;
        var darkText = (TryFindResource<IBrush>(app, "Brush.Text.Primary", ThemeVariant.Dark) as ISolidColorBrush)?.Color ?? Colors.White;
        var darkRatio = CalculateContrastRatio(darkText, darkBg);
        Check("Contrast.DarkTextPrimaryAgainstBg", darkRatio >= 7.0, $"Ratio: {darkRatio:F2}");

        // 浅色主文本 vs 浅色底
        var lightBg = (TryFindResource<IBrush>(app, "Brush.Window.Background", ThemeVariant.Light) as ISolidColorBrush)?.Color ?? Colors.White;
        var lightText = (TryFindResource<IBrush>(app, "Brush.Text.Primary", ThemeVariant.Light) as ISolidColorBrush)?.Color ?? Colors.Black;
        var lightRatio = CalculateContrastRatio(lightText, lightBg);
        Check("Contrast.LightTextPrimaryAgainstBg", lightRatio >= 7.0, $"Ratio: {lightRatio:F2}");

        // 强调色在深浅色下的对比度
        var accent = (TryFindResource<IBrush>(app, "Brush.Accent", ThemeVariant.Dark) as ISolidColorBrush)?.Color ?? Colors.Green;
        var onAccentDark = (TryFindResource<IBrush>(app, "Brush.Text.OnAccent", ThemeVariant.Dark) as ISolidColorBrush)?.Color ?? Colors.Black;
        var accentRatio = CalculateContrastRatio(accent, onAccentDark);
        Check("Contrast.AccentOnDarkTextOnAccent", accentRatio >= 7.0, $"Ratio: {accentRatio:F2}");
    }

    /// <summary>验证 ThemeVariant 热切换平滑生效。</summary>
    private static void TestThemeVariantHotSwitching()
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = ThemeVariant.Dark;
        Check("ThemeVariant.InitiallyDark", app.RequestedThemeVariant == ThemeVariant.Dark);

        app.RequestedThemeVariant = ThemeVariant.Light;
        Check("ThemeVariant.SwitchedToLight", app.RequestedThemeVariant == ThemeVariant.Light);

        var lightBg = TryFindResource<IBrush>(app, "Brush.Window.Background", ThemeVariant.Light);
        Check("ThemeVariant.LightBgResolvedAfterSwitch", lightBg is not null);

        app.RequestedThemeVariant = ThemeVariant.Dark;
        Check("ThemeVariant.RestoredToDark", app.RequestedThemeVariant == ThemeVariant.Dark);
    }

    private static T? TryFindResource<T>(Application app, string key, ThemeVariant variant) where T : class
    {
        if (app.Resources.TryGetResource(key, variant, out var resource) && resource is T match)
        {
            return match;
        }

        if (app.Resources.TryGetResource(key, null, out var fallback) && fallback is T fallbackMatch)
        {
            return fallbackMatch;
        }

        return null;
    }

    private static double CalculateContrastRatio(Color c1, Color c2)
    {
        var l1 = CalculateRelativeLuminance(c1);
        var l2 = CalculateRelativeLuminance(c2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double CalculateRelativeLuminance(Color c)
    {
        double R = AdjustComponent(c.R / 255.0);
        double G = AdjustComponent(c.G / 255.0);
        double B = AdjustComponent(c.B / 255.0);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;

        static double AdjustComponent(double val)
        {
            return val <= 0.03928 ? val / 12.92 : Math.Pow((val + 0.055) / 1.055, 2.4);
        }
    }

    private static T? Find<T>(Avalonia.Controls.Control parent, string name) where T : Avalonia.Controls.Control =>
        parent.FindControl<T>(name);

    private static void RaiseClick(Avalonia.Controls.Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
}
