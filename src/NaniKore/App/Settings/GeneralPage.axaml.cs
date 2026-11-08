using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
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

        // 字体输入（issue 033）：输入框即搜索框；候选源挂在展开下拉时才取，避免无意义的字体表枚举。
        UiFontBox.DropDownOpening += OnFontDropDownOpening;
        PromptFontBox.DropDownOpening += OnFontDropDownOpening;
        UiFontBox.TextChanged += OnFontTextChanged;
        PromptFontBox.TextChanged += OnFontTextChanged;
        // 模板套用晚于 Load（无头自测甚至可能不套用）：在模板就绪时补一次自渲染预览。
        UiFontBox.TemplateApplied += OnFontBoxTemplateApplied;
        PromptFontBox.TemplateApplied += OnFontBoxTemplateApplied;

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

        // 字体键空串合法：直接写进输入框，不在此做任何默认字体兜底。
        UiFontBox.Text = config.Fonts.Ui;
        PromptFontBox.Text = config.Fonts.Prompt;
        ApplyFontPreview(UiFontBox);
        ApplyFontPreview(PromptFontBox);
        // 字体表枚举与缺失警告放到后台：不阻塞设置页首帧，完成后回主线程刷新。
        _ = RefreshFontWarningAsync();

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
        // 一律读 Text：手填的逗号回退链不会被选中项逻辑吞掉；空串合法（平台默认字体）。
        config.Fonts.Ui = UiFontBox.Text ?? string.Empty;
        config.Fonts.Prompt = PromptFontBox.Text ?? string.Empty;
    }

    private void OnFontDropDownOpening(object? sender, CancelEventArgs e)
    {
        if (sender is AutoCompleteBox { ItemsSource: null } box)
        {
            // 两个输入框共用同一份已枚举快照，只枚举一次。
            box.ItemsSource = FontResources.InstalledFamilies;
        }
    }

    private void OnFontTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox box)
        {
            return;
        }

        ApplyFontPreview(box);
        UpdateFontWarning();
    }

    private void OnFontBoxTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is AutoCompleteBox box)
        {
            ApplyFontPreview(box);
        }
    }

    /// <summary>
    /// 自渲染预览（issue 033 C6）：把当前字体族直接写到模板内 TextBox 上——
    /// 隐式 TextBox 样式的 Setter 会压掉继承值，单设 AutoCompleteBox.FontFamily 不会生效。
    /// 候选列表不受影响（ItemTemplate 已钉回 Font.Ui）。
    /// </summary>
    private static void ApplyFontPreview(AutoCompleteBox box)
    {
        // 模板尚未套用时找不到内部 TextBox，静默跳过；TemplateApplied 会补一次。
        if (box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } input)
        {
            input.FontFamily = FontResources.TryCreate(box.Text) ?? FontFamily.Default;
        }
    }

    /// <summary>
    /// 缺失字体警告：只在本机字体表已就绪时同步判断（查内存快照，不阻塞输入）；
    /// 未就绪则由后台预热任务完成后统一刷新。漏字不阻止保存——用户可能就想写只在本机存在的族名。
    /// </summary>
    private void UpdateFontWarning()
    {
        if (!FontResources.IsInstalledFamiliesLoaded)
        {
            return;
        }

        var missing = new[] { UiFontBox.Text, PromptFontBox.Text }
            .Where(text => !FontResources.IsInstalled(text))
            .Select(text => text!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
        {
            FontStatus.Clear();
            return;
        }

        // 未安装的族名不阻止保存，但必须说清保存后的渲染行为：系统会回退，可能仍看起来像其它可用字体。
        FontStatus.Show(
            $"未在本机找到字体{string.Join("、", missing.Select(name => $"「{name}」"))}，此时由回退为系统字体",
            InfoSeverity.Warning);
    }

    private async Task RefreshFontWarningAsync()
    {
        await FontResources.WarmUpAsync().ConfigureAwait(false);
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            UpdateFontWarning();
        }
        else
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(UpdateFontWarning);
        }
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
