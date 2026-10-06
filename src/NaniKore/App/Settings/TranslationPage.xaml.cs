using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Translation;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class TranslationPage : UserControl, ISettingsPage
{
    // 提示词与配置档共用同一次「保存」事务，取消设置时不会污染运行中配置。
    // 密钥索引一律用档名（name 是配置档唯一身份，issue 017）。
    private readonly Dictionary<string, string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedProfileKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _renamedProfileKeys = new(StringComparer.OrdinalIgnoreCase);
    private AppConfig? _config;
    private ProviderProfile? _editingProfile;
    private bool _isApiKeyVisible;
    private bool _loading;

    public TranslationPage()
    {
        InitializeComponent();
        KindBox.Items.Add("openai");
        KindBox.Items.Add("claude");
        KindBox.Items.Add("gemini");
        ApiTypeBox.Items.Add("completions");
        ApiTypeBox.Items.Add("responses");

        ProfileBox.SelectionChanged += (_, _) => SwitchProfile();
        KindBox.SelectionChanged += (_, _) => UpdateKindControls();
        AddButton.Click += (_, _) => AddProfile();
        RemoveButton.Click += (_, _) => RemoveProfile();
        DuplicateButton.Click += (_, _) => DuplicateProfile();
        TestButton.Click += async (_, _) => await TestConnectionAsync();
        ToggleApiKeyVisibilityButton.Click += (_, _) => ToggleApiKeyVisibility();
        RestorePromptButton.Click += (_, _) => RestoreDefaultPrompt();
        RestoreVisionPromptButton.Click += (_, _) => RestoreDefaultVisionPrompt();
        OpenDataDirectoryButton.Click += (_, _) => OpenDataDirectory();
        ClearSecretButton.Click += (_, _) => ClearCurrentSecret();
    }

    public string Title => "翻译";

    public string Glyph => "\uE8C1";

    public FrameworkElement View => this;

    /// <summary>由宿主注入，不保存临时 key，避免测试按钮把未确认输入落盘。</summary>
    public Func<ProviderProfile, string?, Task<string>>? TestConnection { get; set; }

    /// <summary>密钥单文件（.data/keys.bin）；自测注入临时目录，避免触碰真实用户数据。</summary>
    public SecretStore? SecretStoreOverride { get; set; }

    private SecretStore Secrets => SecretStoreOverride ??= new(AppPaths.DataDirectory);

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        PromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.Base)
            ? PromptTemplates.DefaultTranslationBasePrompt
            : config.Prompts.Base;

        // 视觉 Base：单独组装成视觉链路 system prompt，不再有语言插件同拼
        VisionPromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.VisionBase)
            ? PromptTemplates.DefaultVisionBasePrompt
            : config.Prompts.VisionBase;
        _editingProfile = null;
        _loading = true;
        ProfileBox.Items.Clear();
        foreach (var profile in config.Profiles)
        {
            ProfileBox.Items.Add(new ProfileChoice(profile));
        }

        var index = config.Profiles.FindIndex(profile => string.Equals(profile.Name, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        ProfileBox.SelectedIndex = Math.Max(index, 0);
        _loading = false;
        DataDirectoryBox.Text = AppPaths.DataDirectory;
        LoadSelectedProfile();
    }

    public string? Validate()
    {
        SaveFields();
        if (_config is null || _config.Profiles.Count == 0)
        {
            return "至少需要保留一个翻译配置档。";
        }

        // 档名是唯一身份：重名会让活动档与密钥索引产生歧义，保存前必须拦下。
        var duplicateName = _config.Profiles.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            return $"配置档名称重复：{duplicateName.Key}";
        }

        foreach (var profile in _config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) ||
                !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(profile.Model))
            {
                return "配置档必须包含名称、有效的 Base URL 和模型名。";
            }
        }

        return null;
    }

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SaveFields();
        config.Pipeline.ActiveProfile = SelectedProfile?.Name ?? config.Profiles[0].Name;
        config.Prompts.Base = PromptBox.Text.Trim();
        config.Prompts.VisionBase = VisionPromptBox.Text.Trim();
        // 三个语言注音插件键已退役（issue 008 Phase 3）：配置里的旧值原样保留，不在此覆写
    }

    public void SaveSecrets()
    {
        // 先搬键名（重命名）、再删除、最后写入：_pendingKeys 与 _renamedProfileKeys 都以最新档名为准。
        foreach (var (oldName, newName) in _renamedProfileKeys)
        {
            Secrets.Rename(oldName, newName);
        }

        foreach (var profileName in _deletedProfileKeys)
        {
            Secrets.Delete(profileName);
        }

        foreach (var (profileName, apiKey) in _pendingKeys)
        {
            Secrets.Save(profileName, apiKey);
        }

        _renamedProfileKeys.Clear();
        _deletedProfileKeys.Clear();
        _pendingKeys.Clear();
    }

    private ProviderProfile? SelectedProfile => (ProfileBox.SelectedItem as ProfileChoice)?.Profile;

    private void SwitchProfile()
    {
        if (_loading)
        {
            return;
        }

        // SelectionChanged 触发时下拉框已指向新项，必须通过 _editingProfile 保存离开前的配置档。
        SaveFields();
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        _loading = true;
        _editingProfile = profile;
        NameBox.Text = profile.Name;
        KindBox.SelectedItem = profile.Kind;
        if (KindBox.SelectedIndex < 0)
        {
            KindBox.SelectedItem = "openai";
        }

        ApiTypeBox.SelectedItem = profile.ApiType;
        if (ApiTypeBox.SelectedIndex < 0)
        {
            ApiTypeBox.SelectedItem = "completions";
        }

        BaseUrlBox.Text = profile.BaseUrl;
        ModelBox.Text = profile.Model;
        TimeoutBox.Value = profile.TimeoutSeconds;
        SupportsVisionToggle.IsChecked = profile.SupportsVision;
        EnableThinkingToggle.IsChecked = profile.EnableThinking;
        // 优先回显本次设置会话中尚未提交的修改，避免切换配置档后丢失编辑内容。
        SetApiKey(_pendingKeys.GetValueOrDefault(profile.Name) ?? Secrets.Load(profile.Name) ?? string.Empty);
        SetApiKeyVisibility(false);
        RemoveButton.IsEnabled = _config is { Profiles.Count: > 1 };
        TestStatus.Show(
            _pendingKeys.ContainsKey(profile.Name) || Secrets.Exists(profile.Name)
                ? "该配置档已保存 API Key。"
                : "该配置档尚未保存 API Key。",
            InfoSeverity.Neutral);
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
        var name = NameBox.Text.Trim();
        var previousName = profile.Name;
        var nameChanged = !string.Equals(previousName, name, StringComparison.Ordinal);
        profile.Name = name;
        profile.Kind = KindBox.SelectedItem as string ?? "openai";
        profile.ApiType = ApiTypeBox.SelectedItem as string ?? "completions";
        profile.BaseUrl = BaseUrlBox.Text.Trim();
        profile.Model = ModelBox.Text.Trim();
        profile.TimeoutSeconds = TimeoutBox.Value;
        profile.SupportsVision = SupportsVisionToggle.IsChecked == true;
        profile.EnableThinking = EnableThinkingToggle.IsChecked == true;
        if (GetApiKey() is { Length: > 0 } apiKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            _pendingKeys[profile.Name] = apiKey;
        }

        if (nameChanged)
        {
            // 密钥索引跟着档名走：记录 old→new，保存时把 keys.bin 里的键名搬过去（否则一改名 Key 就丢）。
            _renamedProfileKeys[previousName] = name;
            if (_pendingKeys.Remove(previousName, out var pendingKey))
            {
                _pendingKeys[name] = pendingKey;
            }

            RefreshProfileNames();
        }
    }

    /// <summary>ProfileChoice 只在 ToString 里读名称；重命名后需要重建条目才能刷新下拉文本。</summary>
    private void RefreshProfileNames()
    {
        var selectedIndex = ProfileBox.SelectedIndex;
        var items = ProfileBox.Items.Cast<ProfileChoice>().Select(choice => choice.Profile).ToList();
        _loading = true;
        ProfileBox.Items.Clear();
        foreach (var profile in items)
        {
            ProfileBox.Items.Add(new ProfileChoice(profile));
        }

        ProfileBox.SelectedIndex = selectedIndex;
        _editingProfile = SelectedProfile;
        _loading = false;
    }

    private void UpdateKindControls() =>
        ApiTypeRow.IsEnabled = string.Equals(KindBox.SelectedItem as string, "openai", StringComparison.OrdinalIgnoreCase);

    /// <summary>显示状态只影响输入控件；密钥值始终保留在当前编辑会话内，保存时仍由 DPAPI 加密。</summary>
    private void ToggleApiKeyVisibility() => SetApiKeyVisibility(!_isApiKeyVisible);

    private void SetApiKeyVisibility(bool visible)
    {
        var apiKey = GetApiKey();
        _isApiKeyVisible = visible;
        SetApiKey(apiKey);
        ApiKeyTextBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyVisibilityGlyph.Text = visible ? "\uE7B3" : "\uE890";
        ToggleApiKeyVisibilityButton.ToolTip = visible ? "隐藏 API Key" : "显示 API Key";
    }

    private void SetApiKey(string apiKey)
    {
        ApiKeyBox.Password = apiKey;
        ApiKeyTextBox.Text = apiKey;
    }

    private string GetApiKey() => _isApiKeyVisible ? ApiKeyTextBox.Text : ApiKeyBox.Password;

    private void RestoreDefaultPrompt()
    {
        // 默认内容不再隐藏；恢复后立即展示可继续编辑的完整提示词。
        PromptBox.Text = PromptTemplates.DefaultTranslationBasePrompt;
        _ = PromptBox.Focus();
    }

    private void RestoreDefaultVisionPrompt()
    {
        VisionPromptBox.Text = PromptTemplates.DefaultVisionBasePrompt;
        _ = VisionPromptBox.Focus();
    }
    private void AddProfile()
    {
        SaveFields();
        if (_config is null)
        {
            return;
        }

        // 新建档只生成唯一档名，不再造 GUID id（name 即唯一身份，issue 017）。
        var profile = ProviderProfile.CreateOpenAiDefault();
        profile.Name = CreateUniqueProfileName("新配置档");
        _config.Profiles.Add(profile);
        var item = new ProfileChoice(profile);
        ProfileBox.Items.Add(item);
        ProfileBox.SelectedItem = item;
    }

    private void DuplicateProfile()
    {
        SaveFields();
        if (_config is null || SelectedProfile is not { } selected)
        {
            return;
        }

        // 复制档只继承字段与生成唯一档名，不拼 -copy id。
        var clone = selected.Clone();
        clone.Name = CreateUniqueProfileName($"{selected.Name} 副本");
        _config.Profiles.Add(clone);
        var item = new ProfileChoice(clone);
        ProfileBox.Items.Add(item);
        ProfileBox.SelectedItem = item;
    }

    private void RemoveProfile()
    {
        SaveFields();
        if (_config is null || _config.Profiles.Count <= 1 || SelectedProfile is not { } selected)
        {
            return;
        }

        if (!MessageDialog.Confirm(
                Window.GetWindow(this),
                $"删除配置档「{selected.Name}」？",
                "该配置档已保存的 API Key 会在保存设置时一并删除。",
                "删除",
                InfoSeverity.Warning))
        {
            return;
        }

        // 删除配置档即删除其档名下的密钥；未提交的待存密钥一并作废。
        // 若该档刚被重命名过：SaveSecrets 先搬键再删除，搬过去的影子键会被紧随的 Delete 清掉。
        _deletedProfileKeys.Add(selected.Name);
        _renamedProfileKeys.Remove(selected.Name);
        _pendingKeys.Remove(selected.Name);
        _config.Profiles.Remove(selected);
        ProfileBox.Items.Remove(ProfileBox.SelectedItem!);
        _editingProfile = null;
        ProfileBox.SelectedIndex = 0;
    }

    /// <summary>资源管理器定位数据目录 .data：密钥、配置与收藏库都在那里，用户要能一键到达。</summary>
    private void OpenDataDirectory()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppPaths.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MessageDialog.Show(Window.GetWindow(this), "打开数据目录失败。", exception.Message, InfoSeverity.Error);
        }
    }

    /// <summary>清除当前配置档的密钥：确认后进删除集，保存设置时从 keys.bin 移除该键。</summary>
    private void ClearCurrentSecret()
    {
        if (_editingProfile is not { } profile)
        {
            return;
        }

        if (!MessageDialog.Confirm(
                Window.GetWindow(this),
                $"清除配置档「{profile.Name}」的 API Key？",
                "保存设置后从 .data\\keys.bin 中移除该密钥，之后需要重新粘贴。",
                "清除",
                InfoSeverity.Warning))
        {
            return;
        }

        _deletedProfileKeys.Add(profile.Name);
        _pendingKeys.Remove(profile.Name);
        SetApiKey(string.Empty);
        TestStatus.Show("已标记清除该配置档的 API Key，保存设置后生效。", InfoSeverity.Neutral);
    }

    private async Task TestConnectionAsync()
    {
        SaveFields();
        if (SelectedProfile is not { } profile || TestConnection is null)
        {
            return;
        }

        TestButton.IsEnabled = false;
        TestStatus.Show("正在测试连接…", InfoSeverity.Neutral);
        try
        {
            var pendingKey = GetApiKey() is { Length: > 0 } typedKey && !string.IsNullOrWhiteSpace(typedKey)
                ? typedKey
                : _pendingKeys.GetValueOrDefault(profile.Name);
            var message = await TestConnection(profile.Clone(), pendingKey);
            // 宿主只返回一段可展示文本；用「连接成功」前缀判定语义，避免在此重复错误分类逻辑。
            TestStatus.Show(
                message,
                message.StartsWith("连接成功", StringComparison.Ordinal) ? InfoSeverity.Success : InfoSeverity.Error);
        }
        finally
        {
            TestButton.IsEnabled = true;
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

    /// <summary>下拉框只显示可编辑名称；档名同时是活动档与密钥索引的唯一身份（issue 017）。</summary>
    private sealed class ProfileChoice(ProviderProfile profile)
    {
        public ProviderProfile Profile { get; } = profile;

        public override string ToString() => Profile.Name;
    }
}
