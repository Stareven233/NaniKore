using System.Windows;
using NaniKore.Config;

namespace NaniKore.App.Settings;

/// <summary>设置页的最小约定；宿主只组合页面，不了解各功能域的字段。</summary>
internal interface ISettingsPage
{
    string Title { get; }

    /// <summary>导航项图标（Segoe Fluent Icons 字形）。</summary>
    string Glyph { get; }

    /// <summary>页面视图；WPF 化后由各页面自己提供根元素。</summary>
    FrameworkElement View { get; }

    void Load(AppConfig config);

    string? Validate();

    void Apply(AppConfig config);
}
