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
    private readonly Dictionary<string, string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pluginDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string _currentPluginLanguage = "ja-JP";
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

        PluginLanguageBox.Items.Add("日语插件 (ja-JP)");
        PluginLanguageBox.Items.Add("英语插件 (en-US)");
        PluginLanguageBox.SelectedIndex = 0;
        PluginLanguageBox.SelectionChanged += (_, _) => SwitchPluginLanguage();

        ProfileBox.SelectionChanged += (_, _) => SwitchProfile();
        KindBox.SelectionChanged += (_, _) => UpdateKindControls();
        AddButton.Click += (_, _) => AddProfile();
        RemoveButton.Click += (_, _) => RemoveProfile();
        DuplicateButton.Click += (_, _) => DuplicateProfile();
        TestButton.Click += async (_, _) => await TestConnectionAsync();
        ToggleApiKeyVisibilityButton.Click += (_, _) => ToggleApiKeyVisibility();
        RestorePromptButton.Click += (_, _) => RestoreDefaultPrompt();
        RestorePluginPromptButton.Click += (_, _) => RestoreDefaultPluginPrompt();
        RestoreVisionPromptButton.Click += (_, _) => RestoreDefaultVisionPrompt();
    }

    public string Title => "翻译";

    public string Glyph => "\uE8C1";

    public FrameworkElement View => this;

    /// <summary>由宿主注入，不保存临时 key，避免测试按钮把未确认输入落盘。</summary>
    public Func<ProviderProfile, string?, Task<string>>? TestConnection { get; set; }

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        PromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.Base)
            ? PromptTemplates.DefaultTranslationBasePrompt
            : config.Prompts.Base;

        _pluginDrafts["ja-JP"] = string.IsNullOrWhiteSpace(config.Prompts.JapanesePlugin)
            ? PromptTemplates.JapanesePlugin
            : config.Prompts.JapanesePlugin;
        _pluginDrafts["en-US"] = string.IsNullOrWhiteSpace(config.Prompts.EnglishPlugin)
            ? PromptTemplates.EnglishPlugin
            : config.Prompts.EnglishPlugin;
        LoadCurrentPluginPrompt();

        VisionPromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.VisionOneshot)
            ? PromptTemplates.DefaultVisionOneshotPrompt
            : config.Prompts.VisionOneshot;
        _editingProfile = null;
        _loading = true;
        ProfileBox.Items.Clear();
        foreach (var profile in config.Profiles)
        {
            ProfileBox.Items.Add(new ProfileChoice(profile));
        }

        var index = config.Profiles.FindIndex(profile => string.Equals(profile.Id, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        ProfileBox.SelectedIndex = Math.Max(index, 0);
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
        ArgumentNullException.ThrowIfNull(config);
        SaveFields();
        SaveCurrentPluginPrompt();
        config.Pipeline.ActiveProfile = SelectedProfile?.Id ?? config.Profiles[0].Id;
        // 提示词模块化保存：Base 提示词与各语言插件独立落盘
        config.Prompts.Base = PromptBox.Text.Trim();
        config.Prompts.JapanesePlugin = _pluginDrafts.TryGetValue("ja-JP", out var ja) ? ja.Trim() : string.Empty;
        config.Prompts.EnglishPlugin = _pluginDrafts.TryGetValue("en-US", out var en) ? en.Trim() : string.Empty;
        config.Prompts.VisionOneshot = VisionPromptBox.Text.Trim();
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
        // 优先回显本次设置会话中尚未提交的修改，避免切换配置档后丢失编辑内容。
        SetApiKey(_pendingKeys.GetValueOrDefault(profile.Id) ?? SecretStore.Load(profile.Id) ?? string.Empty);
        SetApiKeyVisibility(false);
        RemoveButton.IsEnabled = _config is { Profiles.Count: > 1 };
        TestStatus.Show(
            _pendingKeys.ContainsKey(profile.Id) || SecretStore.Exists(profile.Id)
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
        var nameChanged = !string.Equals(profile.Name, name, StringComparison.Ordinal);
        profile.Name = name;
        profile.Kind = KindBox.SelectedItem as string ?? "openai";
        profile.ApiType = ApiTypeBox.SelectedItem as string ?? "completions";
        profile.BaseUrl = BaseUrlBox.Text.Trim();
        profile.Model = ModelBox.Text.Trim();
        profile.TimeoutSeconds = TimeoutBox.Value;
        profile.SupportsVision = SupportsVisionToggle.IsChecked == true;
        if (GetApiKey() is { Length: > 0 } apiKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            _pendingKeys[profile.Id] = apiKey;
        }

        if (nameChanged)
        {
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

    private void SwitchPluginLanguage()
    {
        SaveCurrentPluginPrompt();
        _currentPluginLanguage = PluginLanguageBox.SelectedIndex == 1 ? "en-US" : "ja-JP";
        LoadCurrentPluginPrompt();
    }

    private void SaveCurrentPluginPrompt()
    {
        if (!string.IsNullOrEmpty(_currentPluginLanguage))
        {
            _pluginDrafts[_currentPluginLanguage] = PluginPromptBox.Text;
        }
    }

    private void LoadCurrentPluginPrompt()
    {
        if (_pluginDrafts.TryGetValue(_currentPluginLanguage, out var text))
        {
            PluginPromptBox.Text = text;
        }
        else
        {
            PluginPromptBox.Text = _currentPluginLanguage == "en-US"
                ? PromptTemplates.EnglishPlugin
                : PromptTemplates.JapanesePlugin;
        }
    }

    private void RestoreDefaultPluginPrompt()
    {
        var defaultPlugin = _currentPluginLanguage == "en-US"
            ? PromptTemplates.EnglishPlugin
            : PromptTemplates.JapanesePlugin;
        PluginPromptBox.Text = defaultPlugin;
        _pluginDrafts[_currentPluginLanguage] = defaultPlugin;
        _ = PluginPromptBox.Focus();
    }

    private void RestoreDefaultVisionPrompt()
    {
        VisionPromptBox.Text = PromptTemplates.DefaultVisionOneshotPrompt;
        _ = VisionPromptBox.Focus();
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

        // 密钥索引始终使用不可变 ID；删除时才一并删除对应 DPAPI 文件。
        _deletedProfileIds.Add(selected.Id);
        _pendingKeys.Remove(selected.Id);
        _config.Profiles.Remove(selected);
        ProfileBox.Items.Remove(ProfileBox.SelectedItem!);
        _editingProfile = null;
        ProfileBox.SelectedIndex = 0;
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
                : _pendingKeys.GetValueOrDefault(profile.Id);
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

    /// <summary>下拉框只显示可编辑名称，稳定 ID 继续作为配置、活动档和密钥的内部索引。</summary>
    private sealed class ProfileChoice(ProviderProfile profile)
    {
        public ProviderProfile Profile { get; } = profile;

        public override string ToString() => Profile.Name;
    }
}
