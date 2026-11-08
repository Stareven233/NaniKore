using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Translation;

namespace NaniKore.App.Settings;

internal sealed partial class TranslationPage : UserControl, ISettingsPage
{
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
        TargetLanguageBox.ItemsSource = LanguageChoices.All;
        PipelineModeBox.ItemsSource = new[] { "local_ocr", "vision_oneshot" };
        KindBox.ItemsSource = new[] { "openai", "claude", "gemini" };
        ApiTypeBox.ItemsSource = new[] { "completions", "responses" };

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

    public Control View => this;

    internal string PipelineMode => PipelineModeBox.SelectedItem as string ?? "local_ocr";

    /// <summary>由宿主注入，不保存临时 key，避免测试按钮把未确认输入落盘。</summary>
    public Func<ProviderProfile, string?, Task<string>>? TestConnection { get; set; }

    /// <summary>密钥单文件（.data/keys.bin）；自测注入临时目录，避免触碰真实用户数据。</summary>
    public SecretStore? SecretStoreOverride { get; set; }

    private SecretStore Secrets => SecretStoreOverride ??= new(AppPaths.DataDirectory);

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        AutoTranslateToggle.IsChecked = config.General.AutoTranslate;
        PasteAutoTranslateToggle.IsChecked = config.General.PasteAutoTranslate;
        AutoAnnotateToggle.IsChecked = config.General.AutoAnnotate;
        AutoExplainToggle.IsChecked = config.General.AutoExplain;
        TargetLanguageBox.SelectedItem = LanguageChoices.Find(config.General.TargetLanguage) ?? LanguageChoices.All[0];
        PipelineModeBox.SelectedItem = config.Pipeline.Mode;
        if (PipelineModeBox.SelectedIndex < 0)
        {
            PipelineModeBox.SelectedItem = "local_ocr";
        }

        PromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.Base)
            ? PromptTemplates.DefaultTranslationBasePrompt
            : config.Prompts.Base;

        VisionPromptBox.Text = string.IsNullOrWhiteSpace(config.Prompts.VisionBase)
            ? PromptTemplates.DefaultVisionBasePrompt
            : config.Prompts.VisionBase;
        _editingProfile = null;
        _loading = true;

        var choices = config.Profiles.Select(p => new ProfileChoice(p)).ToList();
        ProfileBox.ItemsSource = choices;

        var index = config.Profiles.FindIndex(profile => string.Equals(profile.Name, config.Pipeline.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        ProfileBox.SelectedIndex = Math.Max(index, 0);
        _loading = false;
        DataDirectoryBox.Text = AppPaths.DataDirectory;
        LoadSelectedProfile();
    }

    public string? Validate()
    {
        SaveFields();
        if (TargetLanguageBox.SelectedItem is not LanguageChoice)
        {
            return "请选择翻译目标语言。";
        }

        if (_config is null || _config.Profiles.Count == 0)
        {
            return "至少需要保留一个翻译配置档。";
        }

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
        config.General.AutoTranslate = AutoTranslateToggle.IsChecked == true;
        config.General.PasteAutoTranslate = PasteAutoTranslateToggle.IsChecked == true;
        config.General.AutoAnnotate = AutoAnnotateToggle.IsChecked == true;
        config.General.AutoExplain = AutoExplainToggle.IsChecked == true;
        config.General.TargetLanguage = ((LanguageChoice)TargetLanguageBox.SelectedItem!).Name;
        config.Pipeline.Mode = PipelineMode;
        config.Pipeline.ActiveProfile = SelectedProfile?.Name ?? config.Profiles[0].Name;
        config.Prompts.Base = (PromptBox.Text ?? string.Empty).Trim();
        config.Prompts.VisionBase = (VisionPromptBox.Text ?? string.Empty).Trim();
    }

    public void SaveSecrets()
    {
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
        var name = (NameBox.Text ?? string.Empty).Trim();
        var previousName = profile.Name;
        var nameChanged = !string.Equals(previousName, name, StringComparison.Ordinal);
        profile.Name = name;
        profile.Kind = KindBox.SelectedItem as string ?? "openai";
        profile.ApiType = ApiTypeBox.SelectedItem as string ?? "completions";
        profile.BaseUrl = (BaseUrlBox.Text ?? string.Empty).Trim();
        profile.Model = (ModelBox.Text ?? string.Empty).Trim();
        profile.TimeoutSeconds = (int)(TimeoutBox.Value ?? 60);
        profile.SupportsVision = SupportsVisionToggle.IsChecked == true;
        profile.EnableThinking = EnableThinkingToggle.IsChecked == true;
        if (GetApiKey() is { Length: > 0 } apiKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            _pendingKeys[profile.Name] = apiKey;
        }

        if (nameChanged)
        {
            _renamedProfileKeys[previousName] = name;
            if (_pendingKeys.Remove(previousName, out var pendingKey))
            {
                _pendingKeys[name] = pendingKey;
            }

            RefreshProfileNames();
        }
    }

    private void RefreshProfileNames()
    {
        var selectedIndex = ProfileBox.SelectedIndex;
        if (ProfileBox.ItemsSource is not IEnumerable<ProfileChoice> choices)
        {
            return;
        }

        var items = choices.Select(choice => choice.Profile).ToList();
        _loading = true;
        ProfileBox.ItemsSource = items.Select(p => new ProfileChoice(p)).ToList();
        ProfileBox.SelectedIndex = selectedIndex;
        _editingProfile = SelectedProfile;
        _loading = false;
    }

    private void UpdateKindControls() =>
        ApiTypeRow.IsEnabled = string.Equals(KindBox.SelectedItem as string, "openai", StringComparison.OrdinalIgnoreCase);

    private void ToggleApiKeyVisibility() => SetApiKeyVisibility(!_isApiKeyVisible);

    private void SetApiKeyVisibility(bool visible)
    {
        var apiKey = GetApiKey();
        _isApiKeyVisible = visible;
        SetApiKey(apiKey);
        ApiKeyTextBox.IsVisible = visible;
        ApiKeyBox.IsVisible = !visible;
        ApiKeyVisibilityGlyph.Text = visible ? "\uE7B3" : "\uE890";
        ToolTip.SetTip(ToggleApiKeyVisibilityButton, visible ? "隐藏 API Key" : "显示 API Key");
    }

    private void SetApiKey(string apiKey)
    {
        ApiKeyBox.Text = apiKey;
        ApiKeyTextBox.Text = apiKey;
    }

    private string GetApiKey() => _isApiKeyVisible ? (ApiKeyTextBox.Text ?? string.Empty) : (ApiKeyBox.Text ?? string.Empty);

    private void RestoreDefaultPrompt()
    {
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

        var profile = ProviderProfile.CreateOpenAiDefault();
        profile.Name = CreateUniqueProfileName("新配置档");
        _config.Profiles.Add(profile);

        var list = (_config.Profiles).Select(p => new ProfileChoice(p)).ToList();
        ProfileBox.ItemsSource = list;
        ProfileBox.SelectedIndex = list.Count - 1;
    }

    private void DuplicateProfile()
    {
        SaveFields();
        if (_config is null || SelectedProfile is not { } selected)
        {
            return;
        }

        var clone = selected.Clone();
        clone.Name = CreateUniqueProfileName($"{selected.Name} 副本");
        _config.Profiles.Add(clone);

        var list = (_config.Profiles).Select(p => new ProfileChoice(p)).ToList();
        ProfileBox.ItemsSource = list;
        ProfileBox.SelectedIndex = list.Count - 1;
    }

    private void RemoveProfile()
    {
        SaveFields();
        if (_config is null || _config.Profiles.Count <= 1 || SelectedProfile is not { } selected)
        {
            return;
        }

        if (!MessageDialog.Confirm(
                TopLevel.GetTopLevel(this) as Window,
                $"删除配置档「{selected.Name}」？",
                "该配置档已保存的 API Key 会在保存设置时一并删除。",
                "删除",
                InfoSeverity.Warning))
        {
            return;
        }

        _deletedProfileKeys.Add(selected.Name);
        _renamedProfileKeys.Remove(selected.Name);
        _pendingKeys.Remove(selected.Name);
        _config.Profiles.Remove(selected);

        _editingProfile = null;
        var list = (_config.Profiles).Select(p => new ProfileChoice(p)).ToList();
        ProfileBox.ItemsSource = list;
        ProfileBox.SelectedIndex = 0;
    }

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
            MessageDialog.Show(TopLevel.GetTopLevel(this) as Window, "打开数据目录失败。", exception.Message, InfoSeverity.Error);
        }
    }

    private void ClearCurrentSecret()
    {
        if (_editingProfile is not { } profile)
        {
            return;
        }

        if (!MessageDialog.Confirm(
                TopLevel.GetTopLevel(this) as Window,
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

    internal sealed class ProfileChoice(ProviderProfile profile)
    {
        public ProviderProfile Profile { get; } = profile;

        public override string ToString() => Profile.Name;
    }
}
