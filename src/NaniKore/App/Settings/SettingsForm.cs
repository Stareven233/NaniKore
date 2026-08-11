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

    public SettingsForm(
        AppConfig current,
        Action<AppConfig> save,
        Func<ProviderProfile, string?, Task<string>> testConnection,
        Action<bool>? suspendGlobalHotkey = null)
    {
        _working = AppConfigCloner.Clone(current);
        _save = save;
        Text = "NaniKore 设置";
        Icon = ApplicationIcon.LoadWinForms();
        MinimizeBox = false;
        // 结果卡本身是置顶窗口；设置从结果卡打开时也必须保持同等层级，避免被已有窗口遮挡。
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        // 尺寸随当前 DPI 与字体缩放，避免 150%、200% 下内容溢出固定像素窗口。
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = LogicalToDevice(new Size(600, 520));
        MinimumSize = LogicalToDevice(new Size(520, 420));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new GeneralPage();
        _translationPage = new TranslationPage { TestConnection = testConnection };
        var hotkeys = new HotkeyPage();
        // 录制期间必须临时注销已生效的全局热键，否则系统会把按键交给 NaniKore 自己的截图流程。
        hotkeys.RecordingChanged += (_, recording) => suspendGlobalHotkey?.Invoke(recording);
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

        var saveButton = SettingsLayout.CreateActionButton("保存", LogicalToDevice(new Size(82, 0)).Width);
        saveButton.DialogResult = DialogResult.None;
        saveButton.Click += (_, _) => Save();
        var cancelButton = SettingsLayout.CreateActionButton("取消", LogicalToDevice(new Size(82, 0)).Width);
        cancelButton.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            WrapContents = false,
        };
        actions.Controls.AddRange([cancelButton, saveButton]);
        Controls.Add(tabs);
        Controls.Add(actions);
    }

    /// <summary>把设计用的逻辑像素换算到当前 DPI，避免在高缩放下窗口比内容小。</summary>
    private Size LogicalToDevice(Size logical) => new(
        (int)Math.Round(logical.Width * DeviceDpi / 96.0),
        (int)Math.Round(logical.Height * DeviceDpi / 96.0));

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
