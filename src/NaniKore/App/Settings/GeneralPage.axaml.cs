using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NaniKore.App.Controls;
using NaniKore.Config;
using NaniKore.Ocr.Models;
using NaniKore.Phonetics;

namespace NaniKore.App.Settings;

internal sealed partial class GeneralPage : UserControl, ISettingsPage
{
    private readonly Func<IProgress<string>, CancellationToken, Task>? _downloadDependencies;
    private readonly Func<string, IProgress<string>, CancellationToken, Task>? _downloadPhoneticDictionary;

    /// <summary>语言代码 → 该语言词典卡片的下载按钮（集中装配）。</summary>
    private readonly Dictionary<string, Button> _phoneticDownloadButtons;

    public GeneralPage(
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null,
        Func<string, IProgress<string>, CancellationToken, Task>? downloadPhoneticDictionary = null)
    {
        InitializeComponent();
        _downloadDependencies = downloadDependencies;
        _downloadPhoneticDictionary = downloadPhoneticDictionary;

        _phoneticDownloadButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = PhoneticsDownloadJa,
            ["en"] = PhoneticsDownloadEn,
            ["zh"] = PhoneticsDownloadZh,
        };

        TargetLanguageBox.ItemsSource = LanguageChoices.All;
        PipelineModeBox.ItemsSource = new[] { "local_ocr", "vision_oneshot" };

        PipelineModeBox.SelectionChanged += (_, _) => UpdateLocalOcrDependenciesVisibility();
        DownloadDependenciesButton.Click += OnDownloadDependenciesClick;
        PhoneticsDownloadJa.Click += OnDownloadPhoneticsClick;
        PhoneticsDownloadEn.Click += OnDownloadPhoneticsClick;
        PhoneticsDownloadZh.Click += OnDownloadPhoneticsClick;

        // 首屏秒开：初始化按钮禁用/启用状态，将耗时磁盘哈希探测移入后台异步执行
        DownloadDependenciesButton.Content = "检测中…";
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;
        foreach (var button in _phoneticDownloadButtons.Values)
        {
            button.Content = "检测中…";
            button.IsEnabled = _downloadPhoneticDictionary is not null;
        }

        _ = RefreshDependenciesAsync();
        UpdateLocalOcrDependenciesVisibility();
    }

    public string Title => "通用";

    public string Glyph => "\uE713";

    public Control View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AutoTranslateToggle.IsChecked = config.General.AutoTranslate;
        PasteAutoTranslateToggle.IsChecked = config.General.PasteAutoTranslate;
        AutoAnnotateToggle.IsChecked = config.General.AutoAnnotate;
        AutoExplainToggle.IsChecked = config.General.AutoExplain;
        PhoneticsEnabledToggle.IsChecked = config.Phonetics.Enabled;
        PhoneticsRareOnlyToggle.IsChecked = config.Phonetics.RareOnly;
        TargetLanguageBox.SelectedItem = LanguageChoices.Find(config.General.TargetLanguage) ?? LanguageChoices.All[0];
        PipelineModeBox.SelectedItem = config.Pipeline.Mode;
        if (PipelineModeBox.SelectedIndex < 0)
        {
            PipelineModeBox.SelectedItem = "local_ocr";
        }

        _ = RefreshDependenciesAsync();
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
        config.General.AutoAnnotate = AutoAnnotateToggle.IsChecked == true;
        config.General.AutoExplain = AutoExplainToggle.IsChecked == true;
        config.Phonetics.Enabled = PhoneticsEnabledToggle.IsChecked == true;
        config.Phonetics.RareOnly = PhoneticsRareOnlyToggle.IsChecked == true;
        config.General.TargetLanguage = ((LanguageChoice)TargetLanguageBox.SelectedItem!).Name;
        config.Pipeline.Mode = PipelineModeBox.SelectedItem as string ?? "local_ocr";
    }

    private void UpdateLocalOcrDependenciesVisibility()
    {
        // 依赖下载只服务于本地 OCR，切换到视觉流程时隐藏整组设置，避免造成无关干扰。
        var isLocalOcr = string.Equals(PipelineModeBox.SelectedItem as string, "local_ocr", StringComparison.OrdinalIgnoreCase);
        LocalOcrDependenciesGroup.IsVisible = isLocalOcr;
    }

    private async void OnDownloadDependenciesClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadDependencies is null ||
            !MessageDialog.Confirm(
                TopLevel.GetTopLevel(this) as Window,
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
            RefreshDependenciesSync();
            DependencyStatus.Clear();
        }
        catch (Exception exception)
        {
            DependencyStatus.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            RefreshDependenciesSync();
        }
    }

    /// <summary>
    /// 同步刷新本地 OCR 依赖卡片与词典卡片（自测与下载完成后调用）。
    /// </summary>
    internal void RefreshDependenciesSync()
    {
        var isOcrComplete = OcrDependencyManifest.IsComplete();
        var dictStates = new Dictionary<string, PhoneticDictionaryState>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in _phoneticDownloadButtons.Keys)
        {
            dictStates[lang] = PhoneticDependencyManifest.GetState(lang);
        }
        ApplyDependencyStates(isOcrComplete, dictStates);
    }

    /// <summary>
    /// 后台异步探测磁盘 OCR 与词典依赖，完成时安全更新 UI，杜绝阻塞主线程首屏秒开。
    /// </summary>
    internal async Task RefreshDependenciesAsync()
    {
        var (isOcrComplete, dictStates) = await Task.Run(() =>
        {
            var ocr = OcrDependencyManifest.IsComplete();
            var states = new Dictionary<string, PhoneticDictionaryState>(StringComparer.OrdinalIgnoreCase);
            foreach (var lang in _phoneticDownloadButtons.Keys)
            {
                states[lang] = PhoneticDependencyManifest.GetState(lang);
            }
            return (ocr, states);
        }).ConfigureAwait(false);

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyDependencyStates(isOcrComplete, dictStates);
        }
        else
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyDependencyStates(isOcrComplete, dictStates));
        }
    }

    private void ApplyDependencyStates(bool isOcrComplete, IReadOnlyDictionary<string, PhoneticDictionaryState> states)
    {
        DownloadDependenciesButton.Content = OcrButtonLabel(isOcrComplete);
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;

        foreach (var (lang, state) in states)
        {
            if (_phoneticDownloadButtons.TryGetValue(lang, out var btn))
            {
                btn.Content = PhoneticButtonLabel(state);
                btn.IsEnabled = _downloadPhoneticDictionary is not null;
            }
        }
    }

    /// <summary>本地 OCR 依赖就绪状态 → 按钮文案（页面与自测共用）。</summary>
    internal static string OcrButtonLabel(bool isComplete) => isComplete ? "已就绪" : "下载";

    /// <summary>词典状态 → 按钮文案（页面与自测共用同一映射，避免两处各写一份 switch）。</summary>
    internal static string PhoneticButtonLabel(PhoneticDictionaryState state) => state switch
    {
        PhoneticDictionaryState.Ready => "已就绪",
        PhoneticDictionaryState.NeedsUpdate => "更新",
        PhoneticDictionaryState.Missing => "下载",
        _ => "不可用",
    };

    private async void OnDownloadPhoneticsClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadPhoneticDictionary is null ||
            sender is not Button { Tag: string languageCode } button ||
            string.IsNullOrEmpty(languageCode) ||
            !MessageDialog.Confirm(
                TopLevel.GetTopLevel(this) as Window,
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
            RefreshDependenciesSync();
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
            RefreshDependenciesSync();
        }
    }
}
