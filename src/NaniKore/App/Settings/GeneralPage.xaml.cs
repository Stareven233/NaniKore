using System.Windows;
using System.Windows.Controls;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Ocr.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace NaniKore.App.Settings;

internal sealed partial class GeneralPage : UserControl, ISettingsPage
{
    private static readonly LanguageChoice[] TargetLanguages =
    [
        new("中文（简体）", "zh-CN", "中文", "简体中文", "Chinese", "Chinese (Simplified)"),
        new("中文（繁体）", "zh-TW", "繁體中文", "Chinese (Traditional)"),
        new("英语", "en", "English"),
        new("日语", "ja", "日本語", "Japanese"),
        new("韩语", "ko", "한국어", "Korean"),
        new("俄语", "ru", "Русский", "Russian"),
        new("法语", "fr", "Français", "French"),
        new("德语", "de", "Deutsch", "German"),
        new("西班牙语", "es", "Español", "Spanish"),
        new("葡萄牙语", "pt", "Português", "Portuguese"),
        new("意大利语", "it", "Italiano", "Italian"),
    ];

    private readonly Func<IProgress<string>, CancellationToken, Task>? _downloadDependencies;

    public GeneralPage(Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null)
    {
        InitializeComponent();
        _downloadDependencies = downloadDependencies;
        DownloadDependenciesButton.IsEnabled = downloadDependencies is not null;
        var dependenciesReady = OcrDependencyManifest.IsComplete();
        DependencyStatus.Show(
            dependenciesReady ? "本地 OCR 依赖已就绪。" : "本地 OCR 依赖尚未下载。",
            dependenciesReady ? InfoSeverity.Success : InfoSeverity.Warning);
        foreach (var language in TargetLanguages)
        {
            TargetLanguageBox.Items.Add(language);
        }

        PipelineModeBox.Items.Add("local_ocr");
        PipelineModeBox.Items.Add("vision_oneshot");
        UpdateLocalOcrDependenciesVisibility();
    }

    public string Title => "通用";

    public string Glyph => "\uE713";

    public FrameworkElement View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AutoTranslateToggle.IsChecked = config.General.AutoTranslate;
        AutoExplainToggle.IsChecked = config.General.AutoExplain;
        TargetLanguageBox.SelectedItem = FindLanguage(config.General.TargetLanguage) ?? TargetLanguages[0];
        PipelineModeBox.SelectedItem = config.Pipeline.Mode;
        if (PipelineModeBox.SelectedIndex < 0)
        {
            PipelineModeBox.SelectedItem = "local_ocr";
        }

        UpdateLocalOcrDependenciesVisibility();
    }

    public string? Validate() => TargetLanguageBox.SelectedItem is LanguageChoice
        ? null
        : "请选择翻译目标语言。";

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.General.AutoTranslate = AutoTranslateToggle.IsChecked == true;
        config.General.AutoExplain = AutoExplainToggle.IsChecked == true;
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
            DependencyStatus.Show("本地 OCR 依赖已就绪", InfoSeverity.Success);
        }
        catch (Exception exception)
        {
            DependencyStatus.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            DownloadDependenciesButton.IsEnabled = true;
        }
    }

    private static LanguageChoice? FindLanguage(string configuredValue) => TargetLanguages.FirstOrDefault(choice =>
        choice.Matches(configuredValue));

    /// <summary>保留旧版语言代码兼容；保存后统一改用适合提示词与设置页展示的自然语言名称。</summary>
    private sealed class LanguageChoice(string name, params string[] aliases)
    {
        public string Name { get; } = name;

        public bool Matches(string value) => aliases.Append(Name).Any(alias =>
            string.Equals(alias, value, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Name;
    }
}
