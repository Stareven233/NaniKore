using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private readonly Func<string, IProgress<string>, CancellationToken, Task>? _downloadPhoneticPack;
    private string _loadedBackgroundImagePath = string.Empty;

    /// <summary>语言代码 → 该语言的语言包卡片（按钮 + 行内说明，集中装配）。</summary>
    private readonly Dictionary<string, PhoneticPackCard> _packCards;

    public GeneralPage(
        Func<IProgress<string>, CancellationToken, Task>? downloadDependencies = null,
        Func<string, IProgress<string>, CancellationToken, Task>? downloadPhoneticPack = null)
    {
        InitializeComponent();
        _downloadDependencies = downloadDependencies;
        _downloadPhoneticPack = downloadPhoneticPack;

        _packCards = new Dictionary<string, PhoneticPackCard>(StringComparer.OrdinalIgnoreCase)
        {
            [PhoneticLanguages.JapaneseCode] = new(PhoneticsPackJa, PhoneticsPackJaRow),
            [PhoneticLanguages.EnglishCode] = new(PhoneticsPackEn, PhoneticsPackEnRow),
            [PhoneticLanguages.ChineseCode] = new(PhoneticsPackZh, PhoneticsPackZhRow),
        };

        // 字体输入（issue 033）：输入框即搜索框；候选源挂在展开下拉时才取，避免无意义的字体表枚举。
        UiFontBox.DropDownOpening += OnFontDropDownOpening;
        PromptFontBox.DropDownOpening += OnFontDropDownOpening;
        UiFontBox.TextChanged += OnFontTextChanged;
        PromptFontBox.TextChanged += OnFontTextChanged;
        // 模板套用晚于 Load（无头自测甚至可能不套用）：在模板就绪时补一次自渲染预览。
        UiFontBox.TemplateApplied += OnFontBoxTemplateApplied;
        PromptFontBox.TemplateApplied += OnFontBoxTemplateApplied;

        LocalResourceIdleTimeoutBox.ItemsSource = new[] { "0", "30s", "10m", "24h" };

        DownloadDependenciesButton.Click += OnDownloadDependenciesClick;
        foreach (var card in _packCards.Values)
        {
            card.Button.Click += OnDownloadPhoneticPackClick;
        }

        // 首屏秒开：初始化按钮禁用/启用状态，将耗时磁盘哈希探测移入后台异步执行
        DownloadDependenciesButton.Content = "检测中…";
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;
        foreach (var card in _packCards.Values)
        {
            card.Button.Content = "检测中…";
            card.Button.IsEnabled = _downloadPhoneticPack is not null;
        }

        _ = RefreshDependenciesAsync();
        SetPipelineMode("local_ocr");
    }

    public string Title => "通用";

    public string Glyph => "\uE713";

    public Control View => this;

    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        LocalResourceIdleTimeoutBox.Text = config.General.LocalResourceIdleTimeout;
        BackgroundImageBox.Text = config.Windows.BackgroundImage;
        _loadedBackgroundImagePath = config.Windows.BackgroundImage?.Trim() ?? string.Empty;
        BackgroundBlurBox.Value = (decimal)config.Windows.BackgroundBlur;
        BackgroundOpacityBox.Value = (decimal)(config.Windows.BackgroundOpacity * 100);
        PanelBackgroundOpacityBox.Value = (decimal)(config.Windows.PanelBackgroundOpacity * 100);
        PhoneticsEnabledToggle.IsChecked = config.Phonetics.Enabled;
        PhoneticsRareOnlyToggle.IsChecked = config.Phonetics.RareOnly;

        // 字体键空串合法：直接写进输入框，不在此做任何默认字体兜底。
        UiFontBox.Text = config.Fonts.Ui;
        PromptFontBox.Text = config.Fonts.Prompt;
        ApplyFontPreview(UiFontBox);
        ApplyFontPreview(PromptFontBox);
        // 字体表枚举与缺失警告放到后台：不阻塞设置页首帧，完成后回主线程刷新。
        _ = RefreshFontWarningAsync();

        _ = RefreshDependenciesAsync();
        SetPipelineMode(config.Pipeline.Mode);
    }

    public string? Validate()
    {
        if (!ResourceIdleDuration.TryParse(LocalResourceIdleTimeoutBox.Text, out _))
        {
            return "本地资源闲置回收时长必须为非负整数秒，或整数加 s / m / h（如 30s、10m、24h）；0 表示永不回收，时长超出支持范围时无法保存";
        }

        if (BackgroundBlurBox.Value is not (>= 0 and <= 100) ||
            BackgroundOpacityBox.Value is not (>= 0 and <= 100) ||
            PanelBackgroundOpacityBox.Value is not (>= 0 and <= 100))
        {
            return "图片模糊、图片不透明度和面板底色不透明度必须在 0–100 之间";
        }

        var backgroundImagePath = BackgroundImageBox.Text?.Trim() ?? string.Empty;
        if (backgroundImagePath.Length > 0 &&
            !string.Equals(backgroundImagePath, _loadedBackgroundImagePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // 保存前真实解码，避免把不存在的路径或非图片文件静默保存为有效背景。
                using var image = WindowBackgroundResources.LoadImage(backgroundImagePath);
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return $"无法读取背景图片：{exception.Message}";
            }
        }

        return null;
    }

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 由 Validate 拦截非法输入；读 Text 保留自定义时长，不能把未选中的手填值换成预设。
        config.General.LocalResourceIdleTimeout = LocalResourceIdleTimeoutBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        config.Windows.BackgroundImage = BackgroundImageBox.Text?.Trim() ?? string.Empty;
        config.Windows.BackgroundBlur = (double)(BackgroundBlurBox.Value ?? 0);
        config.Windows.BackgroundOpacity = (double)(BackgroundOpacityBox.Value ?? 100) / 100;
        config.Windows.PanelBackgroundOpacity = (double)(PanelBackgroundOpacityBox.Value ?? 100) / 100;
        config.Phonetics.Enabled = PhoneticsEnabledToggle.IsChecked == true;
        config.Phonetics.RareOnly = PhoneticsRareOnlyToggle.IsChecked == true;
        // 一律读 Text：手填的逗号回退链不会被选中项逻辑吞掉；空串合法（平台默认字体）。
        config.Fonts.Ui = UiFontBox.Text ?? string.Empty;
        config.Fonts.Prompt = PromptFontBox.Text ?? string.Empty;
    }

    private async void OnBrowseBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            // 系统选择器返回本地绝对路径；取消时保留输入内容，仍由「保存」统一校验并应用。
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                BackgroundImageBox.Text = path;
            }
        }
        catch (Exception exception)
        {
            MessageDialog.Show(topLevel as Window, "无法选择背景图片", exception.Message, InfoSeverity.Error);
        }
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

    internal void SetPipelineMode(string mode)
    {
        // 依赖下载只服务于本地 OCR，切换到视觉流程时隐藏整组设置，避免造成无关干扰。
        var isLocalOcr = string.Equals(mode, "local_ocr", StringComparison.OrdinalIgnoreCase);
        LocalOcrDependenciesGroup.IsVisible = isLocalOcr;
    }

    private async void OnDownloadDependenciesClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadDependencies is null ||
            !MessageDialog.Confirm(
                TopLevel.GetTopLevel(this) as Window,
                "下载本地 OCR 依赖？",
                "将从 ModelScope 下载 OCR 模型权重和 ONNX Runtime DLL，并保存到当前程序目录的 deps 文件夹",
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
    /// 同步刷新本地 OCR 依赖卡片与三语语言包卡片（自测与下载完成后调用）。
    /// </summary>
    internal void RefreshDependenciesSync()
    {
        DownloadDependenciesButton.Content = OcrButtonLabel(OcrDependencyManifest.IsComplete());
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;
        ApplyPackStates(ReadPackStates());
    }

    /// <summary>后台异步探测 OCR 依赖与三语语言包，完成时安全更新 UI，杜绝阻塞主线程首屏秒开。</summary>
    internal async Task RefreshDependenciesAsync()
    {
        var (isOcrComplete, packStates) = await Task.Run(() => (OcrDependencyManifest.IsComplete(), ReadPackStates())).ConfigureAwait(false);

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyOcrState(isOcrComplete);
            ApplyPackStates(packStates);
        }
        else
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyOcrState(isOcrComplete);
                ApplyPackStates(packStates);
            });
        }
    }

    private void ApplyOcrState(bool isOcrComplete)
    {
        DownloadDependenciesButton.Content = OcrButtonLabel(isOcrComplete);
        DownloadDependenciesButton.IsEnabled = _downloadDependencies is not null;
    }

    /// <summary>本地 OCR 依赖就绪状态 → 按钮文案（页面与自测共用）。</summary>
    internal static string OcrButtonLabel(bool isComplete) => isComplete ? "已就绪" : "下载";

    /// <summary>
    /// 三语语言包状态快照：日英来自原生包宿主的状态机，中文来自拼音数据的磁盘哈希判定。
    /// 两者的磁盘状态统一映射到 <see cref="NativePhoneticPackState"/>，卡片因此只维护一套文案。
    /// </summary>
    internal static IReadOnlyDictionary<string, NativePhoneticPackState> ReadPackStates()
    {
        var states = new Dictionary<string, NativePhoneticPackState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (languageCode, state) in NativePhoneticPackHostHolder.Default.GetStates())
        {
            states[languageCode] = state;
        }

        states[PhoneticLanguages.ChineseCode] = ChinesePinyinData.GetState() switch
        {
            PhoneticDataState.Ready => NativePhoneticPackState.Ready,
            PhoneticDataState.NeedsUpdate => NativePhoneticPackState.NeedsUpdate,
            _ => NativePhoneticPackState.Missing,
        };
        return states;
    }

    /// <summary>语言包状态 → 按钮文案（页面与自测共用同一映射）。</summary>
    internal static string PackButtonLabel(NativePhoneticPackState state) => NativePhoneticPackStateText.ButtonLabel(state);

    /// <summary>刷新语言包卡片：按钮文案跟状态，失败原因写回行内说明（恢复后还原）。</summary>
    internal void ApplyPackStates(IReadOnlyDictionary<string, NativePhoneticPackState> states)
    {
        var host = NativePhoneticPackHostHolder.Default;
        foreach (var (languageCode, state) in states)
        {
            if (!_packCards.TryGetValue(languageCode, out var card))
            {
                continue;
            }

            card.Button.Content = PackButtonLabel(state);
            card.Button.IsEnabled = _downloadPhoneticPack is not null && state != NativePhoneticPackState.Downloading;

            // 失败时把原因钉在该语言行内：主人不用去翻日志就知道是哈希、架构、ABI 还是缺导出。
            var detail = state == NativePhoneticPackState.Failed ? host.GetDetail(languageCode) : null;
            card.Row.Description = string.IsNullOrWhiteSpace(detail)
                ? card.Description
                : card.Description + "\n当前状态：" + NativePhoneticPackStateText.Description(state) + "（" + detail + "）";
        }
    }

    /// <summary>下载入口三语同形：语言代码交给宿主侧统一分派（原生包或拼音数据），卡片不区分机制。</summary>
    private async void OnDownloadPhoneticPackClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadPhoneticPack is null ||
            sender is not Button { Tag: string languageCode } button ||
            string.IsNullOrEmpty(languageCode) ||
            !await MessageDialog.ConfirmAsync(
                TopLevel.GetTopLevel(this) as Window,
                $"下载{PhoneticLanguages.DisplayName(languageCode) ?? languageCode}注音语言包？",
                "将从 ModelScope 下载该语言的注音数据到程序目录的 deps/phonetics 下，校验哈希后即时生效；" +
                "若原生包当前正在使用，新版本会暂存并提示重启生效",
                "下载",
                InfoSeverity.Warning))
        {
            return;
        }

        button.IsEnabled = false;
        PhoneticsStatus.Show("正在准备下载…", InfoSeverity.Neutral);
        var progress = new Progress<string>(message => PhoneticsStatus.Show(message, InfoSeverity.Neutral));
        string message;
        InfoSeverity severity;
        try
        {
            await _downloadPhoneticPack(languageCode, progress, CancellationToken.None);
            var state = ReadPackStates()[languageCode];
            message = NativePhoneticPackStateText.Description(state);
            severity = state is NativePhoneticPackState.Ready or NativePhoneticPackState.RestartPending
                ? InfoSeverity.Success
                : InfoSeverity.Warning;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            severity = InfoSeverity.Error;
        }

        // 先刷新卡片（会把失败原因写进行内说明），再压上本次操作的结论，避免被刷新清掉。
        RefreshDependenciesSync();
        PhoneticsStatus.Show(message, severity);
    }

    /// <summary>语言包卡片：按钮与行内说明；<see cref="Description"/> 是失败原因追加前的原文。</summary>
    private sealed record PhoneticPackCard(Button Button, SettingsRow Row)
    {
        public string Description { get; } = Row.Description;
    }
}
