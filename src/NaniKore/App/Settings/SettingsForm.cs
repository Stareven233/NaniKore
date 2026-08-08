using System.Windows.Forms;
using NaniKore.Config;

namespace NaniKore.App.Settings;

/// <summary>非模态设置宿主；所有页面在配置副本上编辑，保存后一次性热应用。</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _working;
    private readonly Action<AppConfig> _save;
    private readonly List<ISettingsPage> _pages;
    private readonly TranslationPage _translationPage;

    public SettingsForm(AppConfig current, Action<AppConfig> save, Func<ProviderProfile, string?, Task<string>> testConnection)
    {
        _working = AppConfigCloner.Clone(current);
        _save = save;
        Text = "NaniKore 设置";
        Width = 600;
        Height = 480;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new GeneralPage();
        _translationPage = new TranslationPage { TestConnection = testConnection };
        var hotkeys = new HotkeyPage();
        var about = new AboutPage();
        _pages = [general, _translationPage, hotkeys, about];
        foreach (var page in _pages)
        {
            var tab = new TabPage(page.Title);
            page.View.Dock = DockStyle.Fill;
            tab.Controls.Add(page.View);
            tabs.TabPages.Add(tab);
            page.Load(_working);
        }

        var saveButton = new Button { Text = "保存", DialogResult = DialogResult.None, Width = 82 };
        saveButton.Click += (_, _) => Save();
        var cancelButton = new Button { Text = "取消", Width = 82 };
        cancelButton.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        actions.Controls.AddRange([cancelButton, saveButton]);
        Controls.Add(tabs);
        Controls.Add(actions);
    }

    private void Save()
    {
        foreach (var page in _pages)
        {
            var error = page.Validate();
            if (error is not null)
            {
                MessageBox.Show(error, "NaniKore 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        foreach (var page in _pages)
        {
            page.Apply(_working);
        }

        var active = _working.Profiles.First(profile => string.Equals(profile.Id, _working.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_working.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase) && !active.SupportsVision)
        {
            MessageBox.Show("vision_oneshot 需要当前配置档声明支持图像输入。", "NaniKore 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _save(_working);
            _translationPage.SaveSecrets();
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "NaniKore 设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
