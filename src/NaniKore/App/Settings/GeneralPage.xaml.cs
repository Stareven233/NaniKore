using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Ocr.Models;
using NaniKore.Phonetics;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class GeneralPage : UserControl, ISettingsPage
{
    private readonly Func<IProgress<string>, CancellationToken, Task>? _downloadDependencies;
    private readonly Func<string, IProgress<string>, CancellationToken, Task>? _downloadPhoneticDictionary;

    /// <summary>语言代码 → 该语言词典卡片的下载按钮（XAML 显式命名，此处集中装配）。</summary>
    private readonly Dictionary<string, Button> _phoneticDownloadButtons;

    public GeneralPage(
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null,
        Func<string, IProgress<string>, CancellationToken, Task>? downloadPhoneticDictionary = null)
    {
        InitializeComponent();
        _downloadDependencies = downloadDependencies;
        RefreshOcrDependencyCard();

        // 注音词典卡片：下载入口缺失（自测/托盘预览）时整组按钮禁用，状态文案仍照常刷新。
        _downloadPhoneticDictionary = downloadPhoneticDictionary;
        _phoneticDownloadButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = PhoneticsDownloadJa,
            ["en"] = PhoneticsDownloadEn,
            ["zh"] = PhoneticsDownloadZh,
        };
        foreach (var language in LanguageChoices.All)
        {
            TargetLanguageBox.Items.Add(language);
        }

        PipelineModeBox.Items.Add("local_ocr");
        PipelineModeBox.Items.Add("vision_oneshot");
        RefreshPhoneticDictionaryCards();
        UpdateLocalOcrDependenciesVisibility();
    }

    public string Title => "通用";

    public string Glyph => "\uE713";

    public FrameworkElement View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AutoTranslateToggle.IsChecked = config.General.AutoTranslate;
        PasteAutoTranslateToggle.IsChecked = config.General.PasteAutoTranslate;
        AutoExplainToggle.IsChecked = config.General.AutoExplain;
        PhoneticsEnabledToggle.IsChecked = config.Phonetics.Enabled;
        PhoneticsRareOnlyToggle.IsChecked = config.Phonetics.RareOnly;
        TargetLanguageBox.SelectedItem = LanguageChoices.Find(config.General.TargetLanguage) ?? LanguageChoices.All[0];
        PipelineModeBox.SelectedItem = config.Pipeline.Mode;
        if (PipelineModeBox.SelectedIndex < 0)
        {
            PipelineModeBox.SelectedItem = "local_ocr";
        }

        RefreshPhoneticDictionaryCards();
        RefreshOcrDependencyCard();
        UpdateLocalOcrDependenciesVisibility();
    }

    public string? Validate() => TargetLanguageBox.SelectedItem is LanguageChoice
        ? null
        : "请选择翻译目标语言。";

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.General.AutoTranslate = AutoTranslateToggle.IsChecked == true;
        config.General.PasteAutoTranslate = PasteAutoTranslateToggle.IsChecked == true;
        config.General.AutoExplain = AutoExplainToggle.IsChecked == true;
        config.Phonetics.Enabled = PhoneticsEnabledToggle.IsChecked == true;
        config.Phonetics.RareOnly = PhoneticsRareOnlyToggle.IsChecked == true;
        config.General.TargetLanguage = ((LanguageChoice)TargetLanguageBox.SelectedItem!).Name;
        config.Pipeline.Mode = PipelineModeBox.SelectedItem as string ?? "local_ocr";
    }

    private void OnPipelineModeChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLocalOcrDependenciesVisibility();

    private void UpdateLocalOcrDependenciesVisibility()
    {
        // 依赖下载只服务于本地 OCR，切换到视觉流程时隐藏整组设置，避免造成无关干扰。
        var isLocalOcr = string.Equals(PipelineModeBox.SelectedItem as string, "local_ocr", StringComparison.OrdinalIgnoreCase);
        LocalOcrDependenciesGroup.Visibility = isLocalOcr ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDownloadDependenciesClick(object sender, RoutedEventArgs e)
    {
        if (_downloadDependencies is null ||
            !MessageDialog.Confirm(
                Window.GetWindow(this),
                "下载本地 OCR 依赖？",
                "将从 ModelScope 下载 OCR 模型权重和 ONNX Runtime DLL，并保存到当前程序目录的 deps 文件夹。",
                "下载",
                InfoSeverity.Warning))
        {
            return;
        }

        DownloadDependenciesButton.IsEnabled = false;
        DependencyStatus.Show("正在准备下载…", InfoSeverity.Neutral);
        var progress = new Progress<string>(message => DependencyStatus.Show(message, InfoSeverity.Neutral));
        try
        {
            await _downloadDependencies(progress, CancellationToken.None);
            RefreshOcrDependencyCard();
            DependencyStatus.Clear();
        }
        catch (Exception exception)
        {
            DependencyStatus.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            RefreshOcrDependencyCard();
        }
    }

    /// <summary>
    /// 刷新本地 OCR 依赖卡片：本地状态直接映射为按钮文案（下载 / 已就绪），
    /// 完成后文字改为就绪，不单独显示一行提示，无下载入口时按钮禁用（自测场景）但文案保持可读。
    /// </summary>
    private void RefreshOcrDependencyCard()
    {
        DownloadDependenciesButton.Content = OcrButtonLabel(OcrDependencyManifest.IsComplete());
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;
    }

    /// <summary>本地 OCR 依赖就绪状态 → 按钮文案（页面与自测共用）。</summary>
    internal static string OcrButtonLabel(bool isComplete) => isComplete ? "已就绪" : "下载";

    private static LanguageChoice? FindLanguage(string configuredValue) => LanguageChoices.Find(configuredValue);

    /// <summary>
    /// 按语言刷新词典卡片：本地状态直接映射为按钮文案（下载 / 更新 / 已就绪），
    /// 无下载入口时按钮禁用（自测场景）但状态文案保持可读。「已就绪」仍允许点击——
    /// 下载器对已存在且哈希相符的文件会自动跳过，等效于一次校验/修复动作。
    /// </summary>
    private void RefreshPhoneticDictionaryCards()
    {
        foreach (var (languageCode, button) in _phoneticDownloadButtons)
        {
            button.Content = PhoneticButtonLabel(PhoneticDependencyManifest.GetState(languageCode));
            button.IsEnabled = _downloadPhoneticDictionary is not null;
        }
    }

    /// <summary>词典状态 → 按钮文案（页面与自测共用同一映射，避免两处各写一份 switch）。</summary>
    internal static string PhoneticButtonLabel(PhoneticDictionaryState state) => state switch
    {
        PhoneticDictionaryState.Ready => "已就绪",
        PhoneticDictionaryState.NeedsUpdate => "更新",
        PhoneticDictionaryState.Missing => "下载",
        _ => "不可用",
    };

    private async void OnDownloadPhoneticsClick(object sender, RoutedEventArgs e)
    {
        if (_downloadPhoneticDictionary is null ||
            sender is not Button { Tag: string languageCode } button ||
            string.IsNullOrEmpty(languageCode) ||
            !MessageDialog.Confirm(
                Window.GetWindow(this),
                $"下载{PhoneticLanguages.DisplayName(languageCode) ?? languageCode}注音词典？",
                "将从 ModelScope 下载该语言的注音词典并保存到当前程序目录的 deps/phonetics 文件夹，下载完成后立即生效。",
                "下载",
                InfoSeverity.Warning))
        {
            return;
        }

        button.IsEnabled = false;
        PhoneticsStatus.Show("正在准备下载…", InfoSeverity.Neutral);
        var progress = new Progress<string>(message => PhoneticsStatus.Show(message, InfoSeverity.Neutral));
        try
        {
            await _downloadPhoneticDictionary(languageCode, progress, CancellationToken.None);
            RefreshPhoneticDictionaryCards();
            var state = PhoneticDependencyManifest.GetState(languageCode);
            PhoneticsStatus.Show(
                state == PhoneticDictionaryState.Ready ? "词典已就绪，即时生效。" : "下载结束，但词典仍未完整就绪，请重试。",
                state == PhoneticDictionaryState.Ready ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        catch (Exception exception)
        {
            PhoneticsStatus.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            RefreshPhoneticDictionaryCards();
        }
    }
}
