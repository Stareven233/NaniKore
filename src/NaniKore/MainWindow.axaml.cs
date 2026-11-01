using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using NaniKore.App.Theme;

namespace NaniKore;

/// <summary>
/// P1 基座窗口：验证 Avalonia 12 渲染底座与深浅色色板解析。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowChrome(this);
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is not null)
        {
            app.RequestedThemeVariant = app.RequestedThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }
}
