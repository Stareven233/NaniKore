using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

/// <summary>设置 Tab 的最小约定；宿主只组合页面，不了解各功能域的字段。</summary>
internal interface ISettingsPage
{
    string Title { get; }

    Control View { get; }

    void Load(AppConfig config);

    string? Validate();

    void Apply(AppConfig config);
}
