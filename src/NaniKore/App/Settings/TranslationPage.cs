using System.Windows.Forms;
using NaniKore.Config;
using NaniKore.Translation;

namespace NaniKore.App.Settings;

internal sealed class TranslationPage : ISettingsPage
{
    private readonly Panel _view = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _apiType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _model = new();
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true, PlaceholderText = "已保存的密钥不会回显；留空则不修改" };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 600, Value = 60 };
    private readonly CheckBox _supportsVision = new() { Text = "此模型支持图像输入", AutoSize = true };
    private readonly Button _add = new() { Text = "+", Width = 34 };
    private readonly Button _remove = new() { Text = "删除", Width = 62 };
    private readonly Button _duplicate = new() { Text = "复制", Width = 62 };
    private readonly Button _test = new() { Text = "测试连接", Width = 90 };
    private readonly Label _testStatus = new() { AutoSize = true };
    // 提示词与配置档共用同一次“保存”事务，取消设置时不会污染运行中配置。
    private readonly TextBox _translatePrompt = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _restoreDefaultPrompt = new() { Text = "恢复内置默认", Width = 112 };
    private readonly Label _promptHint = new()
    {
        Text = "默认提示词已显示，可直接修改；{{target_language}} 会替换为“通用”页选择的目标语言。",
        AutoSize = true,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Dictionary<string, string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private AppConfig? _config;
    private ProviderProfile? _editingProfile;
    private bool _loading;

    public TranslationPage()
    {
        _kind.Items.AddRange(["openai", "claude", "gemini"]);
        _apiType.Items.AddRange(["completions", "responses"]);
        AddRow("配置档：", _profiles, 16);
        _add.SetBounds(300, 16, 34, 28);
        _remove.SetBounds(340, 16, 62, 28);
        _duplicate.SetBounds(408, 16, 62, 28);
        _view.Controls.AddRange([_add, _remove, _duplicate]);
        AddRow("名称：", _name, 58);
        AddRow("类型：", _kind, 100);
        AddRow("API 形式：", _apiType, 142);
        AddRow("Base URL：", _baseUrl, 184);
        AddRow("模型：", _model, 226);
        AddRow("API Key：", _apiKey, 268);
        _test.SetBounds(300, 268, 90, 28);
        _view.Controls.Add(_test);
        AddRow("超时秒数：", _timeout, 310);
        _supportsVision.SetBounds(130, 352, 240, 28);
        _testStatus.SetBounds(16, 392, 510, 40);
        _view.Controls.AddRange([_supportsVision, _testStatus]);
        _view.Controls.Add(new Label { Text = "翻译提示词：", AutoSize = true, Left = 16, Top = 440 });
        _translatePrompt.SetBounds(16, 464, 540, 116);
        _restoreDefaultPrompt.SetBounds(16, 592, 112, 30);
        _promptHint.SetBounds(140, 598, 420, 20);
        _view.Controls.AddRange([_translatePrompt, _restoreDefaultPrompt, _promptHint]);

        _profiles.SelectedIndexChanged += (_, _) => SwitchProfile();
        _kind.SelectedIndexChanged += (_, _) => UpdateKindControls();
        _add.Click += (_, _) => AddProfile();
        _remove.Click += (_, _) => RemoveProfile();
        _duplicate.Click += (_, _) => DuplicateProfile();
        _test.Click += async (_, _) => await TestConnectionAsync();
        _restoreDefaultPrompt.Click += (_, _) => RestoreDefaultPrompt();
    }

    public string Title => "翻译";

    public Control View => _view;

    /// <summary>由宿主注入，不保存临时 key，避免测试按钮把未确认输入落盘。</summary>
    public Func<ProviderProfile, string?, Task<string>>? TestConnection { get; set; }

    public void Load(AppConfig config)
    {
        _config = config;
        _translatePrompt.Text = string.IsNullOrWhiteSpace(config.Prompts.Translate)
            ? PromptTemplates.DefaultTranslationPrompt
            : config.Prompts.Translate;
        _editingProfile = null;
        _loading = true;
        _profiles.Items.Clear();
        foreach (var profile in config.Profiles)
        {
            _profiles.Items.Add(new ProfileChoice(profile));
        }

        var index = config.Profiles.FindIndex(profile => string.Equals(profile.Id, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        _profiles.SelectedIndex = Math.Max(index, 0);
        _loading = false;
        LoadSelectedProfile();
    }

    public string? Validate()
    {
        SaveFields();
        if (_config is null || _config.Profiles.Count == 0)
        {
            return "至少需要保留一个翻译配置档。";
        }

        var duplicateId = _config.Profiles.GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            return $"配置档 ID 重复：{duplicateId.Key}";
        }

        var duplicateName = _config.Profiles.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            return $"配置档名称重复：{duplicateName.Key}";
        }

        foreach (var profile in _config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) ||
                !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(profile.Model))
            {
                return "配置档必须包含名称、有效的 Base URL 和模型名。";
            }
        }

        return null;
    }

    public void Apply(AppConfig config)
    {
        SaveFields();
        config.Pipeline.ActiveProfile = SelectedProfile?.Id ?? config.Profiles[0].Id;
        // 默认提示词以实际文本保存，主人可在配置文件或设置页中直接审阅和修改。
        config.Prompts.Translate = _translatePrompt.Text.Trim();
    }

    public void SaveSecrets()
    {
        foreach (var profileId in _deletedProfileIds)
        {
            SecretStore.Delete(profileId);
        }

        foreach (var (profileId, apiKey) in _pendingKeys)
        {
            SecretStore.Save(profileId, apiKey);
        }

        _deletedProfileIds.Clear();
        _pendingKeys.Clear();
    }

    private ProviderProfile? SelectedProfile => (_profiles.SelectedItem as ProfileChoice)?.Profile;

    private void SwitchProfile()
    {
        if (_loading)
        {
            return;
        }

        // SelectedIndexChanged 触发时下拉框已指向新项，必须通过 _editingProfile 保存离开前的配置档。
        SaveFields();
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        _editingProfile = profile;
        _loading = true;
        _name.Text = profile.Name;
        _kind.SelectedItem = profile.Kind;
        _apiType.SelectedItem = profile.ApiType;
        _baseUrl.Text = profile.BaseUrl;
        _model.Text = profile.Model;
        _timeout.Value = Math.Clamp(profile.TimeoutSeconds, (int)_timeout.Minimum, (int)_timeout.Maximum);
        _supportsVision.Checked = profile.SupportsVision;
        _apiKey.Clear();
        _testStatus.Text = SecretStore.Exists(profile.Id) ? "该配置档已保存 API Key。" : "该配置档尚未保存 API Key。";
        _loading = false;
        UpdateKindControls();
    }

    private void SaveFields()
    {
        if (_loading || _editingProfile is null)
        {
            return;
        }

        var profile = _editingProfile;
        var name = _name.Text.Trim();
        var nameChanged = !string.Equals(profile.Name, name, StringComparison.Ordinal);
        profile.Name = name;
        profile.Kind = _kind.SelectedItem as string ?? "openai";
        profile.ApiType = _apiType.SelectedItem as string ?? "completions";
        profile.BaseUrl = _baseUrl.Text.Trim();
        profile.Model = _model.Text.Trim();
        profile.TimeoutSeconds = Decimal.ToInt32(_timeout.Value);
        profile.SupportsVision = _supportsVision.Checked;
        if (!string.IsNullOrWhiteSpace(_apiKey.Text))
        {
            _pendingKeys[profile.Id] = _apiKey.Text;
        }

        if (nameChanged)
        {
            _profiles.Refresh();
        }
    }

    private void UpdateKindControls()
    {
        _apiType.Enabled = string.Equals(_kind.SelectedItem as string, "openai", StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreDefaultPrompt()
    {
        // 默认内容不再隐藏；恢复后立即展示可继续编辑的完整提示词。
        _translatePrompt.Text = PromptTemplates.DefaultTranslationPrompt;
        _translatePrompt.Focus();
    }

    private void AddProfile()
    {
        SaveFields();
        if (_config is null)
        {
            return;
        }

        var profile = ProviderProfile.CreateOpenAiDefault();
        profile.Id = $"profile-{Guid.NewGuid():N}"[..16];
        profile.Name = CreateUniqueProfileName("新配置档");
        _config.Profiles.Add(profile);
        var item = new ProfileChoice(profile);
        _profiles.Items.Add(item);
        _profiles.SelectedItem = item;
    }

    private void DuplicateProfile()
    {
        SaveFields();
        if (_config is null || SelectedProfile is not { } selected)
        {
            return;
        }

        var clone = selected.Clone();
        clone.Id = $"{selected.Id}-copy";
        var suffix = 2;
        while (_config.Profiles.Any(profile => string.Equals(profile.Id, clone.Id, StringComparison.OrdinalIgnoreCase)))
        {
            clone.Id = $"{selected.Id}-copy-{suffix++}";
        }

        clone.Name = CreateUniqueProfileName($"{selected.Name} 副本");
        _config.Profiles.Add(clone);
        var item = new ProfileChoice(clone);
        _profiles.Items.Add(item);
        _profiles.SelectedItem = item;
    }

    private void RemoveProfile()
    {
        SaveFields();
        if (_config is null || _config.Profiles.Count <= 1 || SelectedProfile is not { } selected)
        {
            return;
        }

        if (MessageBox.Show($"删除配置档“{selected.Name}”及其已保存 API Key？", "NaniKore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        // 密钥索引始终使用不可变 ID；删除时才一并删除对应 DPAPI 文件。
        _deletedProfileIds.Add(selected.Id);
        _pendingKeys.Remove(selected.Id);
        _config.Profiles.Remove(selected);
        _profiles.Items.Remove(_profiles.SelectedItem!);
        _editingProfile = null;
        _profiles.SelectedIndex = 0;
    }

    private async Task TestConnectionAsync()
    {
        SaveFields();
        if (SelectedProfile is not { } profile || TestConnection is null)
        {
            return;
        }

        _test.Enabled = false;
        _testStatus.Text = "正在测试连接…";
        try
        {
            var pendingKey = !string.IsNullOrWhiteSpace(_apiKey.Text)
                ? _apiKey.Text
                : _pendingKeys.GetValueOrDefault(profile.Id);
            _testStatus.Text = await TestConnection(profile.Clone(), pendingKey);
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private string CreateUniqueProfileName(string proposedName)
    {
        if (_config is null)
        {
            return proposedName;
        }

        var candidate = proposedName;
        var suffix = 2;
        while (_config.Profiles.Any(profile => string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{proposedName} ({suffix++})";
        }

        return candidate;
    }

    private void AddRow(string label, Control control, int top)
    {
        _view.Controls.Add(new Label { Text = label, AutoSize = true, Left = 16, Top = top + 5 });
        control.SetBounds(130, top, 164, 28);
        _view.Controls.Add(control);
    }

    /// <summary>下拉框只显示可编辑名称，稳定 ID 继续作为配置、活动档和密钥的内部索引。</summary>
    private sealed class ProfileChoice(ProviderProfile profile)
    {
        public ProviderProfile Profile { get; } = profile;

        public override string ToString() => Profile.Name;
    }
}