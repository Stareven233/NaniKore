using System;
using System.Collections.Generic;

using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Interop;
using NaniKore.Phonetics;
using NaniKore.Translation;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace NaniKore.App;

/// <summary>
/// 结果面板主窗口：单例普通窗口，ShowActivated=false 保证不抢占前台焦点。
/// 承载原文双模注音区、译文流式区、微结构化精讲卡片、划词收藏气泡与指标栏。
/// </summary>
internal sealed partial class ResultPanelWindow : Window
{
    private const nint HwndTop = 0;
    private const nint HwndNoTopmost = -2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int PlacementGapPixels = 8;

    private const int SourceRegionRow = 1;
    private const int TranslationRegionRow = 4;

    private nint _windowHandle;
    private int _translationInsertIndex;
    private bool _translationHasContent;
    private string _annotatedSource = string.Empty;
    private string _originalEditingText = string.Empty;
    private bool _isEditingSource;
    private bool _receivedAnnotatedSource;
    private string _targetLanguage = "中文（简体）";
    private readonly StringBuilder _thinkingBuffer = new();
    private long _lastThinkingUpdateTick;
    private bool _isDisplayingThinking;
    private string? _savedTranslationForExplanation;

    private CancellationTokenSource? _annotationCancellation;
    private Task? _annotationTask;
    private string _annotationSourcePlain = string.Empty;
    private PhoneticsConfig _phonetics = new();
    private string? _selectedSourceLanguage;
    private bool _loadingSourceLanguages;

    private RubyReadingKind _sourceReadingKind = RubyReadingKind.Unknown;

    /// <summary>原文注音显隐状态：由注音按钮直接翻转控制，与配置项 AutoAnnotate 联动。</summary>
    private bool _isRubyVisible = true;

    /// <summary>当前注音是否可见。</summary>
    public bool IsRubyVisible => _isRubyVisible;

    // 拖动把手状态记录
    private bool _isDraggingSourceGrip;
    private Point _sourceGripStartPoint;
    private double _sourceGripStartHeight;

    private bool _isDraggingTranslationGrip;
    private Point _translationGripStartPoint;
    private double _translationGripStartHeight;

    private bool _isSwitchingEditMode;

    public event EventHandler? TranslateRequested;
    public event EventHandler? ExplanationRequested;
    public event EventHandler<bool>? AutoExplainChanged;
    public event EventHandler<bool>? PinChanged;
    public event EventHandler<SettingsTab>? SettingsRequested;

    public string SourceText => _isEditingSource
        ? (SourceTextBox?.Text ?? string.Empty)
        : (RubyTextParser.ToPlainText(_annotatedSource) is { Length: > 0 } plain ? plain : (SourceTextBox?.Text ?? string.Empty));

    public string TranslationText => TranslationTextBox?.Text ?? string.Empty;

    public string ExplanationText => ExplanationView?.RawText ?? string.Empty;

    public Task? PendingAnnotation => _annotationTask;

    public string AnnotatedSourceText => _annotatedSource;

    public bool IsAutoExplainEnabled
    {
        get => AutoExplainToggle?.IsChecked == true;
        set
        {
            if (AutoExplainToggle != null)
            {
                AutoExplainToggle.IsChecked = value;
            }
        }
    }

    internal string CurrentSourceLanguage
    {
        get
        {
            var detection = LanguageDetector.Detect(SourceText, _targetLanguage);
            return LanguageChoices.FromDetection(detection);
        }
    }

    public ResultPanelWindow()
    {
        InitializeComponent();
        Icon = ApplicationIcon.Load();
        TitleBarIcon.Source = ApplicationIcon.LoadBitmap();
        ThemeManager.ApplyWindowChrome(this);

        // 监听全局注音点击复制通知
        RubyInlineControl.StatusNotificationRequested += OnRubyStatusNotification;

        // 监听精讲展开事件
        ExplanationExpander.PropertyChanged += (sender, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && e.GetNewValue<bool>())
            {
                OnExplanationExpanderExpanded(ExplanationExpander, null!);
            }
        };

        // 精讲降级与收藏反馈
        ExplanationView.DegradedChanged += (_, isDegraded) =>
        {
            if (isDegraded)
            {
                SetStatus("未能完全识别为微结构化卡片，已展示原始排版精讲。");
            }
        };

        ExplanationView.SetSourceLanguageProvider(() => CurrentSourceLanguage);
        ExplanationView.FavoriteToggled += (_, args) =>
        {
            SetStatus(args.Added ? $"已收藏{args.Category}：{args.Content}" : $"已取消收藏{args.Category}：{args.Content}");
        };

        SelectionBubble.FavoriteRequested += (_, item) =>
        {
            SetStatus($"已收藏{item.Category}：{item.Content}");
        };

        SelectionBubble.FavoriteRemoved += (_, content) =>
        {
            SetStatus($"已取消收藏：{content}");
        };

        // 填充源语言下拉列表
        PopulateSourceLanguageBox();

        // 监听按键（Esc 返回或收起）
        KeyDown += OnPanelKeyDown;

        // 监听失焦自动保存编辑
        Deactivated += OnPanelDeactivated;

        // 监听全局指针按下：点击窗口其他区域时隐藏气泡或失焦保存
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);

        // 默认未翻译时译文框静默，防止误点与光标居中闪烁
        TranslationTextBox.IsHitTestVisible = false;
        TranslationTextBox.PlaceholderText = "等待翻译...";
    }

    private void OnPanelDeactivated(object? sender, EventArgs e)
    {
        // 若失焦时正处于编辑模式，提交保存修改
        if (_isEditingSource)
        {
            CommitEditingSourceIfActive();
        }
        // 结果面板无论置顶与否，失焦绝不自动关闭，仅由 Esc 或关闭按钮退出
    }

    internal void OnPanelDeactivatedForTest() => OnPanelDeactivated(this, EventArgs.Empty);

    internal void OnPanelPointerPressedForTest(PointerPressedEventArgs e) => OnPanelPointerPressed(this, e);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public void SetPinned(bool pinned)
    {
        PinButton.IsChecked = pinned;
        Topmost = pinned;
    }

    private void OnPinButtonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var pinned = PinButton.IsChecked == true;
        Topmost = pinned;
        PinChanged?.Invoke(this, pinned);
    }

    private void OnMinimizeButtonClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateSourceViewerDisplay()
    {
        if (_isEditingSource)
        {
            return;
        }

        var showRuby = _isRubyVisible;
        SourceViewer.LineHeight = showRuby ? 40 : 28;

        if (showRuby)
        {
            SourceViewer.Inlines = RubyDocumentBuilder.BuildInlines(_annotatedSource, SourceViewer.FontSize);
        }
        else
        {
            var plain = RubyTextParser.ToPlainText(_annotatedSource);
            SourceViewer.Inlines = RubyDocumentBuilder.BuildInlines(plain, SourceViewer.FontSize);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
        {
            _windowHandle = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        }
    }

    private void OnRubyStatusNotification(string message) => SetStatus(message);

    /// <summary>注入生词与知识点收藏服务。</summary>
    public void SetFavoriteService(IFavoriteService? service)
    {
        ExplanationView.SetFavoriteService(service);
        SelectionBubble.SetFavoriteService(service);
    }

    /// <summary>同步当前的目标语言。</summary>
    public void SetTargetLanguage(string? targetLanguage)
    {
        _targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "中文（简体）" : targetLanguage;
    }

    /// <summary>同步注音配置项。</summary>
    public void SetPhoneticsOptions(PhoneticsConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _phonetics = options;
    }

    private void PopulateSourceLanguageBox()
    {
        _loadingSourceLanguages = true;
        SourceLanguageBox.Items.Clear();
        SourceLanguageBox.Items.Add(new SourceLanguageChoice(null, "自动探测"));
        foreach (var language in PhoneticLanguages.All)
        {
            SourceLanguageBox.Items.Add(new SourceLanguageChoice(language.Code, language.DisplayName));
        }

        SourceLanguageBox.SelectedIndex = 0;
        _loadingSourceLanguages = false;
    }

    private sealed record SourceLanguageChoice(string? LanguageCode, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>更新并显示本次调用的 Token 消耗与性能指标。</summary>
    public void SetMetrics(TranslationMetrics metrics)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => SetMetrics(metrics));
            return;
        }

        MetricsTextBlock.Text = metrics.FormatSummary();
        MetricsBar.IsVisible = true;
    }

    /// <summary>清空并折叠本次指标栏。</summary>
    public void ClearMetrics()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(ClearMetrics);
            return;
        }

        MetricsTextBlock.Text = string.Empty;
        MetricsBar.IsVisible = false;
    }

    /// <summary>重置面板并装载初始原文与状态。</summary>
    public void Reset(string source, string status, bool autoTranslate = false, bool autoExplain = false, bool autoAnnotate = true)
    {
        _annotatedSource = source;
        SourceTextBox.Text = RubyTextParser.ToPlainText(source);
        _originalEditingText = SourceTextBox.Text ?? string.Empty;
        _receivedAnnotatedSource = false;
        NormalizeSourceButton.IsEnabled = true;
        AnnotateSourceButton.IsEnabled = true;
        _isRubyVisible = autoAnnotate;
        TranslateButton.Content = "翻译";
        TranslationTextBox.Clear();
        TranslationTextBox.IsHitTestVisible = false;
        TranslationTextBox.PlaceholderText = "等待翻译...";
        ExplanationView.Clear();
        ExplanationExpander.IsExpanded = false;
        ExplanationExpander.IsEnabled = false;
        ExplanationExpander.Header = "📖 精讲与语法解析 (点击展开)";
        AutoExplainToggle.IsChecked = autoExplain;
        ClearMetrics();
        _translationInsertIndex = 0;
        _translationHasContent = false;
        _isDisplayingThinking = false;
        _thinkingBuffer.Clear();
        _lastThinkingUpdateTick = 0;
        _savedTranslationForExplanation = null;
        ErrorDetailsTextBox.Clear();
        ErrorDetailsExpander.IsVisible = false;
        ErrorDetailsExpander.IsExpanded = false;
        StatusBar.Show(status, InfoSeverity.Neutral);

        // 无论是否 autoTranslate，打开时均默认停留在注音渲染视图，双击才进入编辑
        _isEditingSource = false;
        SourceViewerBorder.IsVisible = true;
        SourceTextBox.IsVisible = false;
        SourceCaptionTextBlock.Text = "双击可编辑";
        UpdateSourceViewerDisplay();

        _annotationSourcePlain = string.Empty;
        if (autoAnnotate && !string.IsNullOrWhiteSpace(source))
        {
            _ = AnnotateSourceAsync();
        }
    }

    public void SetTranslationInProgress(bool inProgress)
    {
        NormalizeSourceButton.IsEnabled = !inProgress;
        AnnotateSourceButton.IsEnabled = !inProgress;
        TranslateButton.IsEnabled = !inProgress;
        if (!inProgress)
        {
            TranslateButton.Content = "翻译";
            if (_isDisplayingThinking)
            {
                TranslationTextBox.Clear();
                _isDisplayingThinking = false;
            }
        }
    }

    public void BeginTranslation()
    {
        _translationInsertIndex = 0;
        _translationHasContent = false;
        _receivedAnnotatedSource = false;
        _isDisplayingThinking = false;
        _thinkingBuffer.Clear();
        _lastThinkingUpdateTick = 0;
        _savedTranslationForExplanation = null;
        ClearMetrics();
        TranslateButton.Content = "翻译";
        TranslationTextBox.IsHitTestVisible = true;
        TranslationTextBox.PlaceholderText = null;
        ExplanationView.Clear();
        ExplanationExpander.IsExpanded = false;
        ExplanationExpander.IsEnabled = false;
        ExplanationExpander.Header = "📖 精讲与语法解析 (点击展开)";

        CommitEditingSourceIfActive();

        if (string.IsNullOrEmpty(_annotatedSource) && !string.IsNullOrEmpty(SourceTextBox.Text))
        {
            _annotatedSource = SourceTextBox.Text;
            UpdateSourceViewerDisplay();
        }
    }

    public void ClearTranslation()
    {
        TranslationTextBox.Clear();
        TranslationTextBox.IsHitTestVisible = false;
        TranslationTextBox.PlaceholderText = "等待翻译...";
        _translationInsertIndex = 0;
        _translationHasContent = false;
        _isDisplayingThinking = false;
        _thinkingBuffer.Clear();
        _lastThinkingUpdateTick = 0;
        _savedTranslationForExplanation = null;
    }

    /// <summary>在 Windows 物理像素下将窗口对齐桌面选区附近（不抢焦点）。</summary>
    public void PositionNearSelection(PixelRect selectionOnDesktop)
    {
        if (!IsVisible || _windowHandle == nint.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(_windowHandle, out var windowBounds))
        {
            return;
        }

        // 使用 Screens 获取工作区
        var screen = Screens.ScreenFromPoint(new PixelPoint(selectionOnDesktop.X, selectionOnDesktop.Y))
                     ?? Screens.Primary;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        var windowWidth = windowBounds.Right - windowBounds.Left;
        var windowHeight = windowBounds.Bottom - windowBounds.Top;
        var x = Math.Clamp(selectionOnDesktop.X, workArea.X, workArea.X + workArea.Width - windowWidth);
        var y = selectionOnDesktop.Bottom + PlacementGapPixels;

        if (y + windowHeight > workArea.Y + workArea.Height)
        {
            y = selectionOnDesktop.Y - PlacementGapPixels - windowHeight;
        }

        y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - windowHeight);

        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            HwndNoTopmost,
            x,
            y,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>将窗口提升到普通非置顶层顶端，不抢占焦点。</summary>
    public void BringToTopmostBand()
    {
        if (!IsVisible || _windowHandle == nint.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            HwndTop,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>单次异步注音计算与呈现。</summary>
    internal Task AnnotateSourceAsync(bool manual = false)
    {
        _annotationTask = RunAnnotationAsync(manual);
        return _annotationTask;
    }

    private async Task RunAnnotationAsync(bool manual)
    {
        if (!_phonetics.Enabled)
        {
            if (manual)
            {
                ShowStatusSafe("原文注音已在设置-通用中关闭。");
            }
            return;
        }

        var plain = SourceTextSafe();
        if (string.IsNullOrWhiteSpace(plain))
        {
            return;
        }

        if (!manual && _annotatedSource.Length > 0 && string.Equals(_annotationSourcePlain, plain, StringComparison.Ordinal))
        {
            return;
        }

        var languageCode = _selectedSourceLanguage ?? DetectSourceLanguageCode(plain);
        var displayName = PhoneticLanguages.DisplayName(languageCode) ?? languageCode;
        var readingKind = ReadingKindForLanguage(languageCode);
        var annotator = PhoneticAnnotatorRegistryHolder.Default.GetAnnotator(languageCode);
        if (annotator is null)
        {
            if (manual)
            {
                ShowStatusSafe($"暂不支持{displayName}注音。");
            }
            return;
        }

        if (!annotator.IsAvailable)
        {
            if (manual)
            {
                ShowStatusSafe($"未安装{displayName}词典，请前往设置-通用-原文注音下载；当前以纯文本展示原文。");
            }
            return;
        }

        _annotationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _annotationCancellation = cancellation;

        try
        {
            var spans = await Task.Run(() =>
            {
                var wordCount = annotator.CountWords(plain);
                var profile = AnnotationProfile.Load(languageCode, _phonetics.Enabled, _phonetics.RareOnly, wordCount);
                return annotator.Annotate(plain, new AnnotationContext(profile));
            }, cancellation.Token);

            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyAnnotationIfCurrent(plain, spans, displayName, readingKind, manual, cancellation.Token));
                return;
            }

            ApplyAnnotationIfCurrent(plain, spans, displayName, readingKind, manual, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (manual)
            {
                ShowStatusSafe($"原文注音失败：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_annotationCancellation, cancellation))
            {
                _annotationCancellation = null;
            }
        }
    }

    private void ApplyAnnotationIfCurrent(
        string plain,
        IReadOnlyList<AnnotationSpan> spans,
        string displayName,
        RubyReadingKind readingKind,
        bool manual,
        CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || !string.Equals(SourceText, plain, StringComparison.Ordinal))
        {
            return;
        }

        ApplyAnnotation(plain, spans, displayName, readingKind, manual);
    }

    private string SourceTextSafe() =>
        Dispatcher.UIThread.CheckAccess() ? SourceText : Dispatcher.UIThread.Invoke(new Func<string>(() => SourceText));

    private void ShowStatusSafe(string message, bool isError = false)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SetStatus(message, isError);
            return;
        }

        Dispatcher.UIThread.Post(() => SetStatus(message, isError));
    }

    private void ApplyAnnotation(string plain, IReadOnlyList<AnnotationSpan> spans, string displayName, RubyReadingKind readingKind, bool manual)
    {
        var ruby = spans.Count == 0 ? plain : AnnotatedTextFormatter.Compose(plain, spans);
        _annotatedSource = ruby;
        _annotationSourcePlain = plain;
        _sourceReadingKind = readingKind;
        _isRubyVisible = true;
        ApplySourceMenuWording();

        if (!_isEditingSource)
        {
            UpdateSourceViewerDisplay();
        }

        SourceTextBox.Text = plain;
        _originalEditingText = plain;

        if (manual)
        {
            SetStatus(spans.Count == 0
                ? $"已按{displayName}重新注音：没有可标注的单词。"
                : $"已按{displayName}重新注音（{spans.Count} 词）。");
        }
    }

    private void ApplySourceMenuWording()
    {
        var reading = RubyReadingTerms.Reading(_sourceReadingKind);
        CopyPlainSourceMenuItem.Header = "复制原文";
        CopyReadingSourceMenuItem.Header = $"复制{reading}";
        // CopyAnnotatedSourceMenuItem.Header = $"复制带注音文本 ({RubyReadingTerms.Base(_sourceReadingKind)}[{reading}])";
        CopyAnnotatedSourceMenuItem.Header = $"复制注音文本";
    }

    private static RubyReadingKind ReadingKindForLanguage(string? languageCode) => languageCode switch
    {
        "ja" => RubyReadingKind.Kana,
        "zh" => RubyReadingKind.Pinyin,
        "en" => RubyReadingKind.Ipa,
        _ => RubyReadingKind.Unknown
    };

    private string DetectSourceLanguageCode(string plain)
    {
        var detection = LanguageDetector.Detect(plain, _targetLanguage);
        if (detection.NeedsJapaneseRuby || detection.HasJapanese)
        {
            return "ja";
        }

        if (detection.HasSimplifiedChinese)
        {
            return "zh";
        }

        if (detection.NeedsEnglishIpa || detection.HasEnglish)
        {
            return "en";
        }

        return "zh";
    }

    private void OnSourceLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSourceLanguages)
        {
            return;
        }

        _selectedSourceLanguage = (SourceLanguageBox.SelectedItem as SourceLanguageChoice)?.LanguageCode;
        _ = AnnotateSourceAsync(manual: true);
    }

    public void Append(UnderstandDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
        {
            return;
        }

        if (delta.Channel == DeltaChannel.Thinking)
        {
            _thinkingBuffer.Append(delta.Text);
            var now = Environment.TickCount64;
            if (_lastThinkingUpdateTick == 0 || now - _lastThinkingUpdateTick >= 1200)
            {
                _lastThinkingUpdateTick = now;
                var snippet = ExtractThinkingSnippet(_thinkingBuffer.ToString());
                if (snippet.Length > 0)
                {
                    TranslationTextBox.Text = $"💭 [思考中] {snippet}";
                    _isDisplayingThinking = true;
                }
            }
            return;
        }

        if (delta.Channel == DeltaChannel.SourceText)
        {
            if (!_receivedAnnotatedSource)
            {
                _annotatedSource = string.Empty;
                _receivedAnnotatedSource = true;
            }

            _annotatedSource += delta.Text;
            _annotationSourcePlain = string.Empty;
            UpdateSourceViewerDisplay();
            SourceTextBox.Text = RubyTextParser.ToPlainText(_annotatedSource);
            _originalEditingText = SourceTextBox.Text;
            return;
        }

        if (delta.Channel == DeltaChannel.Explanation)
        {
            if (_isDisplayingThinking)
            {
                TranslationTextBox.Text = _savedTranslationForExplanation ?? string.Empty;
                _isDisplayingThinking = false;
            }

            ExplanationView.AppendChunk(delta.Text);
            return;
        }

        var text = delta.Text;
        if (delta.Channel == DeltaChannel.Translation)
        {
            if (_isDisplayingThinking)
            {
                TranslationTextBox.Clear();
                _isDisplayingThinking = false;
            }

            if (!_translationHasContent)
            {
                text = text.TrimStart();
                if (text.Length == 0)
                {
                    return;
                }

                var previous = TranslationTextBox.Text ?? string.Empty;
                TranslationTextBox.Text = previous.Length == 0
                    ? text
                    : text + Environment.NewLine + previous;
                _translationInsertIndex = text.Length;
                _translationHasContent = true;
                return;
            }

            var current = TranslationTextBox.Text ?? string.Empty;
            TranslationTextBox.Text = current.Insert(Math.Min(_translationInsertIndex, current.Length), text);
            _translationInsertIndex += text.Length;
            return;
        }

        TranslationTextBox.Text = (TranslationTextBox.Text ?? string.Empty) + text;
    }

    private static string ExtractThinkingSnippet(string thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return string.Empty;
        }

        var lines = thinking.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var latest = lines.Length > 0 ? lines[^1].Trim() : string.Empty;
        if (latest.Length == 0 && lines.Length > 1)
        {
            latest = lines[^2].Trim();
        }

        if (latest.Length == 0)
        {
            latest = thinking.Trim();
        }

        latest = Regex.Replace(latest, @"\s+", " ");
        const int maxLength = 60;
        return latest.Length > maxLength
            ? latest[..maxLength] + "..."
            : latest + "...";
    }

    public void SetStatus(string status, bool isError = false)
    {
        if (!isError)
        {
            ErrorDetailsTextBox.Clear();
            ErrorDetailsExpander.IsVisible = false;
            ErrorDetailsExpander.IsExpanded = false;
        }

        StatusBar.Show(status, isError ? InfoSeverity.Error : InfoSeverity.Neutral);
    }

    public void SetError(string summary, string details)
    {
        ErrorDetailsTextBox.Text = details;
        ErrorDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(details);
        ErrorDetailsExpander.IsExpanded = false;
        StatusBar.Show(summary, InfoSeverity.Error);
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SelectionBubble.PopupHost.IsOpen)
            {
                SelectionBubble.Hide();
                e.Handled = true;
                return;
            }

            if (_isEditingSource)
            {
                // 按 Esc 退出编辑模式并提交保存修改，绝不还原撤销
                ExitEditMode();
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
        }
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SelectionBubble.PopupHost.IsOpen)
        {
            // 精准判定：若点击落在气泡内容或其下拉菜单内部，绝不收起气泡，放行气泡内所有选项点击交互
            if (SelectionBubble.ContainsPointerEvent(e))
            {
                return;
            }

            // 仅在明确点击气泡外部区域时收起气泡
            SelectionBubble.Hide();
        }

        if (_isEditingSource && SourceTextBox.IsVisible)
        {
            var boxPoint = e.GetPosition(SourceTextBox);
            var inBox = boxPoint.X >= 0 && boxPoint.X <= SourceTextBox.Bounds.Width &&
                        boxPoint.Y >= 0 && boxPoint.Y <= SourceTextBox.Bounds.Height;
            if (!inBox)
            {
                ExitEditMode();
            }
        }
    }

    private void OnNormalizeSourceClick(object? sender, RoutedEventArgs e)
    {
        var original = SourceText;
        var normalized = SourceTextNormalizer.Normalize(original);
        if (string.Equals(original, normalized, StringComparison.Ordinal))
        {
            SetStatus(string.IsNullOrWhiteSpace(original) ? "没有可规范化的原文。" : "原文已经是规范格式。");
            return;
        }

        var caretIndex = SourceTextBox.CaretIndex;
        SourceTextBox.Text = normalized;
        SourceTextBox.CaretIndex = Math.Min(caretIndex, normalized.Length);
        _annotatedSource = normalized;
        _annotationSourcePlain = string.Empty;
        UpdateSourceViewerDisplay();

        if (_translationHasContent)
        {
            SetStatus("原文已规范化，请点击“重译”使用新文本。");
            TranslateButton.Content = "重译";
        }
        else
        {
            SetStatus("原文已规范化。");
            TranslateButton.Content = "翻译";
        }
    }

    private async void OnCopyPlainSourceClick(object? sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.SelectionStart != SourceViewer.SelectionEnd;
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractText(SourceViewer, RubyCopyKind.Plain)
            : RubyTextParser.ToPlainText(_annotatedSource);

        if (!string.IsNullOrEmpty(text))
        {
            await RubyInlineControl.SetClipboardTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);
            SetStatus(hasSelection ? "已复制选中原文到剪贴板。" : "已复制纯原文到剪贴板。");
        }
    }

    private async void OnCopyAnnotatedSourceClick(object? sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.SelectionStart != SourceViewer.SelectionEnd;
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractText(SourceViewer, RubyCopyKind.Annotated)
            : _annotatedSource;

        if (!string.IsNullOrEmpty(text))
        {
            await RubyInlineControl.SetClipboardTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);
            SetStatus(hasSelection ? "已复制选中带注音文本到剪贴板。" : "已复制带注音文本到剪贴板。");
        }
    }

    private async void OnCopyReadingSourceClick(object? sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.SelectionStart != SourceViewer.SelectionEnd;
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractText(SourceViewer, RubyCopyKind.ReadingOnly)
            : RubyTextParser.ToReadingText(_annotatedSource);

        if (!string.IsNullOrEmpty(text))
        {
            await RubyInlineControl.SetClipboardTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);
            var reading = RubyReadingTerms.Reading(_sourceReadingKind);
            SetStatus(hasSelection ? $"已复制选中纯{reading}到剪贴板。" : $"已复制纯{reading}到剪贴板。");
        }
    }

    private void OnSourceViewerDoubleTapped(object? sender, TappedEventArgs e)
    {
        var caretIndex = SourceTextBox.Text?.Length ?? 0;
        EnterEditMode(caretIndex);
        e.Handled = true;
    }

    internal void EnterEditMode(int caretIndex = -1)
    {
        if (_isEditingSource)
        {
            return;
        }

        // 双击平滑切入编辑模式：先平息划词气泡，清理选区防指针竞争，加装状态锁防撕裂
        SelectionBubble.Hide();
        SourceViewer.ClearSelection();
        _isSwitchingEditMode = true;

        _originalEditingText = SourceTextBox.Text ?? string.Empty;
        _isEditingSource = true;
        SourceViewerBorder.IsVisible = false;
        SourceTextBox.IsVisible = true;
        SourceCaptionTextBlock.Text = "编辑中 · 失焦自动保存";

        var textLen = SourceTextBox.Text?.Length ?? 0;
        if (caretIndex < 0 || caretIndex > textLen)
        {
            caretIndex = textLen;
        }

        // 同步直接精准赋初值，确保即时断言与光标定位瞬时就绪
        SourceTextBox.CaretIndex = caretIndex;
        SourceTextBox.SelectionStart = caretIndex;
        SourceTextBox.SelectionEnd = caretIndex;

        // 异步一帧平滑聚焦与二次校准，避开双击鼠标抬起脉冲的瞬间竞争
        Dispatcher.UIThread.Post(() =>
        {
            if (_isEditingSource && SourceTextBox.IsVisible)
            {
                SourceTextBox.Focus();
                SourceTextBox.CaretIndex = caretIndex;
                SourceTextBox.SelectionStart = caretIndex;
                SourceTextBox.SelectionEnd = caretIndex;
            }
            _isSwitchingEditMode = false;
        }, DispatcherPriority.Input);
    }

    internal void CommitEditingSourceIfActive()
    {
        if (!_isEditingSource)
        {
            return;
        }

        var edited = SourceTextBox.Text ?? string.Empty;
        _isEditingSource = false;
        SourceTextBox.IsVisible = false;
        SourceViewerBorder.IsVisible = true;
        SourceCaptionTextBlock.Text = "双击可编辑";

        if (!string.Equals(edited, _originalEditingText, StringComparison.Ordinal))
        {
            _annotatedSource = edited;
            _annotationSourcePlain = string.Empty;
            _originalEditingText = edited;
            UpdateSourceViewerDisplay();
            if (_translationHasContent)
            {
                TranslateButton.Content = "重译";
            }
        }
    }

    private void ExitEditMode()
    {
        var wasModified = !string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal);
        CommitEditingSourceIfActive();

        if (wasModified)
        {
            if (_translationHasContent)
            {
                TranslateButton.Content = "重译";
                SetStatus("原文已修改，请点击“重译”。");
            }
            else
            {
                TranslateButton.Content = "翻译";
            }

            _ = AnnotateSourceAsync();
        }
    }

    private void OnSourceTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 按 Esc 退出编辑模式并提交保存修改，绝不还原撤销
            ExitEditMode();
            e.Handled = true;
        }
    }

    private void OnSourceTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal))
        {
            ExitEditMode();
        }
        else
        {
            // 实质修改后失焦：立即同步保存事实源，杜绝丢失修改
            var edited = SourceTextBox.Text ?? string.Empty;
            _annotatedSource = edited;
            _annotationSourcePlain = string.Empty;

            if (_translationHasContent)
            {
                TranslateButton.Content = "重译";
                SourceCaptionTextBlock.Text = "原文已修改，请重译";
            }
            else
            {
                TranslateButton.Content = "翻译";
                SourceCaptionTextBlock.Text = "原文已修改，请翻译";
            }
        }
    }

    private void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        CommitEditingSourceIfActive();
        TranslateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAnnotateSourceClick(object? sender, RoutedEventArgs e)
    {
        CommitEditingSourceIfActive();
        if (string.IsNullOrWhiteSpace(SourceText))
        {
            SetStatus("没有可注音的原文。");
            return;
        }

        if (!_phonetics.Enabled)
        {
            SetStatus("原文注音已在设置-通用中关闭。");
            return;
        }

        if (_annotatedSource.Length > 0 && !string.Equals(_annotatedSource, RubyTextParser.ToPlainText(_annotatedSource), StringComparison.Ordinal))
        {
            _isRubyVisible = !_isRubyVisible;
            UpdateSourceViewerDisplay();
            SetStatus(_isRubyVisible ? "已开启原文注音显示。" : "已切换为纯文本显示。");
        }
        else
        {
            _isRubyVisible = true;
            _ = AnnotateSourceAsync(manual: true);
        }
    }

    private void OnClearTranslationClick(object? sender, RoutedEventArgs e) => ClearTranslation();

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, SettingsTab.Translation);
    }

    // 拖动把手调节行高
    private void OnSourceResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDraggingSourceGrip = true;
        _sourceGripStartPoint = e.GetPosition(this);
        _sourceGripStartHeight = PageGrid.RowDefinitions[SourceRegionRow].Height.Value;
        e.Pointer.Capture(SourceResizeThumb);
        e.Handled = true;
    }

    private void OnSourceResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSourceGrip) return;
        var cur = e.GetPosition(this);
        var delta = cur.Y - _sourceGripStartPoint.Y;
        var nextHeight = Math.Max(80, _sourceGripStartHeight + delta);
        PageGrid.RowDefinitions[SourceRegionRow].Height = new GridLength(nextHeight, GridUnitType.Pixel);
    }

    private void OnSourceResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingSourceGrip)
        {
            _isDraggingSourceGrip = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnTranslationResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDraggingTranslationGrip = true;
        _translationGripStartPoint = e.GetPosition(this);
        _translationGripStartHeight = PageGrid.RowDefinitions[TranslationRegionRow].Height.Value;
        e.Pointer.Capture(TranslationResizeThumb);
        e.Handled = true;
    }

    private void OnTranslationResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingTranslationGrip) return;
        var cur = e.GetPosition(this);
        var delta = cur.Y - _translationGripStartPoint.Y;
        var nextHeight = Math.Max(80, _translationGripStartHeight + delta);
        PageGrid.RowDefinitions[TranslationRegionRow].Height = new GridLength(nextHeight, GridUnitType.Pixel);
    }

    private void OnTranslationResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingTranslationGrip)
        {
            _isDraggingTranslationGrip = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    public void SetExplanationEnabled(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => SetExplanationEnabled(enabled));
            return;
        }

        ExplanationExpander.IsEnabled = enabled;
    }

    public void BeginExplanation()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(BeginExplanation);
            return;
        }

        _savedTranslationForExplanation = TranslationTextBox.Text;
        _isDisplayingThinking = false;
        _thinkingBuffer.Clear();
        _lastThinkingUpdateTick = 0;

        ExplanationExpander.Header = "📖 精讲与语法解析 (正在生成…)";
        ExplanationExpander.IsExpanded = true;
        ExplanationView.Clear();
    }

    public void CompleteExplanation(bool success)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => CompleteExplanation(success));
            return;
        }

        if (_isDisplayingThinking)
        {
            TranslationTextBox.Text = _savedTranslationForExplanation ?? string.Empty;
            _isDisplayingThinking = false;
        }
        _savedTranslationForExplanation = null;

        ExplanationExpander.Header = success
            ? "📖 精讲与语法解析"
            : "📖 精讲与语法解析 (点击重试)";
        ExplanationView.Complete(success);
    }

    private void OnExplanationExpanderExpanded(object? sender, RoutedEventArgs e)
    {
        ExplanationRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoExplain(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => SetAutoExplain(enabled));
            return;
        }

        AutoExplainToggle.IsChecked = enabled;
    }

    public void ExpandExplanation()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(ExpandExplanation);
            return;
        }

        ExplanationExpander.IsExpanded = true;
    }

    private void OnAutoExplainToggleChanged(object? sender, RoutedEventArgs e)
    {
        AutoExplainChanged?.Invoke(this, AutoExplainToggle.IsChecked == true);
    }

    private void OnTranslationTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var selLen = Math.Abs(TranslationTextBox.SelectionEnd - TranslationTextBox.SelectionStart);
        if (selLen is >= 1 and <= 100)
        {
            var text = TranslationTextBox.SelectedText?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var pt = e.GetPosition(TranslationTextBox);
                SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                SelectionBubble.ShowAt(TranslationTextBox, new Rect(pt.X, pt.Y, 1, 1), text);
            }
        }
    }

    private void OnSourceTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isSwitchingEditMode)
        {
            return;
        }

        var selLen = Math.Abs(SourceTextBox.SelectionEnd - SourceTextBox.SelectionStart);
        if (selLen is >= 1 and <= 100)
        {
            var text = SourceTextBox.SelectedText?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var pt = e.GetPosition(SourceTextBox);
                SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                SelectionBubble.ShowAt(SourceTextBox, new Rect(pt.X, pt.Y, 1, 1), text);
            }
        }
    }

    private void OnSourceViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            var selLen = Math.Abs(SourceViewer.SelectionEnd - SourceViewer.SelectionStart);
            if (selLen >= 1)
            {
                var text = RubyDocumentBuilder.ExtractText(SourceViewer, RubyCopyKind.Plain)?.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length <= 100)
                {
                    var mousePos = e.GetPosition(SourceViewer);
                    var reading = RubyDocumentBuilder.ExtractText(SourceViewer, RubyCopyKind.ReadingOnly)?.Trim();
                    var note = !string.IsNullOrEmpty(reading) && !string.Equals(reading, text, StringComparison.Ordinal)
                        ? $"读音: {reading}"
                        : null;
                    SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                    SelectionBubble.ShowAt(SourceViewer, new Rect(mousePos.X, mousePos.Y, 1, 1), text, note);
                }
            }
        }
        catch
        {
            // 防御性安全隔离：划选松开定位与气泡呼出全程安全兜底，绝不打崩 UI 线程
        }
    }
}
