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
    private const nint HwndTopmost = -1;
    private const nint HwndNoTopmost = -2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int PlacementGapPixels = 8;

    private const int SourceRegionRow = 1;
    private const int TranslationRegionRow = 4;
    private const int ExplanationRegionRow = 6;

    private readonly TextAnnotatorRegistry _annotators;
    private nint _windowHandle;
    private int _translationInsertIndex;
    private bool _translationHasContent;
    private string _sourcePlain = string.Empty;
    private IReadOnlyList<AnnotationSpan> _sourceSpans = [];
    private string _llmSourceInput = string.Empty;
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
    private bool _autoAnnotate = true;

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

    /// <summary>精讲区是否展开：展开时第 6 行切为星号行，独享窗口剩余高度。</summary>
    private bool _isExplanationExpanded;

    public event EventHandler? TranslateRequested;
    public event EventHandler? ExplanationRequested;
    public event EventHandler<bool>? AutoExplainChanged;
    public event EventHandler<bool>? PinChanged;
    public event EventHandler<SettingsTab>? SettingsRequested;

    public string SourceText => _isEditingSource
        ? (SourceTextBox?.Text ?? string.Empty)
        : _sourcePlain;

    public string TranslationText => TranslationTextBox?.Text ?? string.Empty;

    public string ExplanationText => ExplanationView?.RawText ?? string.Empty;

    public Task? PendingAnnotation => _annotationTask;

    public string AnnotatedSourceText => AnnotatedTextFormatter.Compose(_sourcePlain, _sourceSpans);

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

    public ResultPanelWindow() : this(PhoneticAnnotatorRegistryHolder.Default) { }

    internal ResultPanelWindow(TextAnnotatorRegistry annotators)
    {
        _annotators = annotators;
        InitializeComponent();
        Icon = ApplicationIcon.Load();
        TitleBarIcon.Source = ApplicationIcon.LoadBitmap();
        ThemeManager.ApplyWindowChrome(this);

        // 监听全局注音点击复制通知
        RubyInlineControl.StatusNotificationRequested += OnRubyStatusNotification;

        // 监听精讲展开事件
        ExplanationExpander.PropertyChanged += (sender, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
            {
                ApplyExplanationRowSizing(e.GetNewValue<bool>());
                if (e.GetNewValue<bool>())
                {
                    OnExplanationExpanderExpanded(ExplanationExpander, null!);
                }
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

        // 收藏库异常就地展示：星标与列表保持真实落库状态，应用继续可用
        ExplanationView.FavoriteFailed += (_, message) => SetStatus(message, isError: true);
        SelectionBubble.FavoriteFailed += (_, message) => SetStatus(message, isError: true);

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

        // 页面不足一屏时撑满滚动视口，星号行才有真实剩余空间可分配
        ContentScrollViewer.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
            {
                SyncPageMinHeight();
            }
        };
    }

    /// <summary>把页面最小高度对齐视口扣除内边距后的尺寸，分配窗口剩余空间。</summary>
    private void SyncPageMinHeight() => ApplyPageMinHeight(ContentScrollViewer.Viewport.Height);

    /// <summary>按内容视口高度更新页面下限，忽略尚未完成布局的无效尺寸。</summary>
    internal void ApplyPageMinHeight(double viewportHeight)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            return;
        }

        // ScrollContentPresenter 的 Viewport 包含 Padding，页面需扣除上下内边距。
        var available = _isExplanationExpanded
            ? Math.Max(0, viewportHeight - ContentScrollViewer.Padding.Top - ContentScrollViewer.Padding.Bottom)
            : 0;
        if (Math.Abs(PageGrid.MinHeight - available) > 0.5)
        {
            PageGrid.MinHeight = available;
        }
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
        UpdateDetectedSourceLanguage();
        if (_isEditingSource)
        {
            return;
        }

        var showRuby = _isRubyVisible;
        SourceViewer.LineHeight = showRuby ? 36 : 26;

        SourceViewer.Inlines = RubyDocumentBuilder.BuildInlines(_sourcePlain,
            showRuby ? _sourceSpans : [], SourceViewer.FontSize);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
        {
            _windowHandle = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        }

        // 每次打开都按按钮状态重建置顶：打开前 SetPinned 设置的 Topmost 可能未真正生效，
        // 这里在窗口已拥有句柄后同步一次，避免“按钮选中却不置顶”
        Topmost = PinButton.IsChecked == true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 静态事件会持有整个窗口；关闭时退订，并阻止未完成的注音回写已关闭控件。
        RubyInlineControl.StatusNotificationRequested -= OnRubyStatusNotification;
        SetFavoriteService(null);
        _annotationCancellation?.Cancel();
        base.OnClosed(e);
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
        var next = string.IsNullOrWhiteSpace(targetLanguage) ? "中文（简体）" : targetLanguage;
        if (_targetLanguage != next)
        {
            _targetLanguage = next;
            InvalidateAnnotation(clearSpans: true);
            UpdateSourceViewerDisplay();
        }
    }

    /// <summary>同步注音配置项。</summary>
    public void SetPhoneticsOptions(PhoneticsConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_phonetics.Enabled == options.Enabled && _phonetics.RareOnly == options.RareOnly) return;
        _phonetics = new PhoneticsConfig { Enabled = options.Enabled, RareOnly = options.RareOnly };
        InvalidateAnnotation(clearSpans: true);
        UpdateSourceViewerDisplay();
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

    /// <summary>自动模式显示本轮探测结果，保留空语言代码，避免把探测结果变成手动锁定。</summary>
    private void UpdateDetectedSourceLanguage()
    {
        var plain = _sourcePlain;
        var displayName = string.IsNullOrWhiteSpace(plain)
            ? "自动探测"
            : $"{PhoneticLanguages.DisplayName(DetectSourceLanguageCode(plain))}（自动）";
        if (SourceLanguageBox.Items[0] is SourceLanguageChoice choice && choice.DisplayName == displayName)
        {
            return;
        }

        // 替换选项会触发 SelectionChanged；刷新标签期间不重注音，也不改变用户手选。
        _loadingSourceLanguages = true;
        try
        {
            SourceLanguageBox.Items[0] = new SourceLanguageChoice(null, displayName);
            if (_selectedSourceLanguage is null)
            {
                SourceLanguageBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _loadingSourceLanguages = false;
        }
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
        MetricsTextBlock.IsVisible = true;
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
        MetricsTextBlock.IsVisible = false;
    }

    /// <summary>重置面板并装载初始原文与状态。</summary>
    public void Reset(string source, string status, bool autoTranslate = false, bool autoExplain = false, bool autoAnnotate = true)
    {
        _annotationCancellation?.Cancel();
        // 新输入立即恢复全部操作按钮，不依赖已取消旧请求的 finally。
        SetTranslationInProgress(false);
        _autoAnnotate = autoAnnotate;
        SetPlainSource(source);
        SourceTextBox.Text = source;
        _originalEditingText = SourceTextBox.Text ?? string.Empty;
        _receivedAnnotatedSource = false;
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
        SetStatus(status);

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

        if (string.IsNullOrEmpty(_sourcePlain) && !string.IsNullOrEmpty(SourceTextBox.Text))
        {
            SetPlainSource(SourceTextBox.Text);
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
        var placement = ResolvePanelPlacement(selectionOnDesktop, workArea, windowWidth, windowHeight);

        // 定位后按当前置顶状态选择 Z 序锚：pinned 时必须用 HWND_TOPMOST，
        // 否则会像旧实现那样用 HWND_NOTOPMOST 把窗口的置顶剥掉，导致按钮显示激活却不置顶
        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            Topmost ? HwndTopmost : HwndNoTopmost,
            placement.X,
            placement.Y,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>
    /// 面板定位纯决策：优先落在选区下方，下方空间不足时上移到选区上方，横向夹在工作区内。
    /// 窗口大于工作区时（小屏、任务栏压缩、大屏拉宽后移至小屏），夹取上界会小于下界，
    /// <see cref="Math.Clamp(int, int, int)"/> 直接抛异常；此时把上界收敛到下界本身，
    /// 退化为对齐工作区左上角——与系统对超大窗口的处理一致，标题栏拖动、左上缩放握柄、
    /// 内容滚动与 Esc 关闭始终可达，不会因定位失败丢掉整个面板。
    /// </summary>
    internal static PixelPoint ResolvePanelPlacement(
        PixelRect selectionOnDesktop,
        PixelRect workArea,
        int windowWidth,
        int windowHeight)
    {
        var maxX = Math.Max(workArea.X, workArea.X + workArea.Width - windowWidth);
        var maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - windowHeight);

        var x = Math.Clamp(selectionOnDesktop.X, workArea.X, maxX);
        var y = selectionOnDesktop.Bottom + PlacementGapPixels;

        if (y + windowHeight > workArea.Y + workArea.Height)
        {
            y = selectionOnDesktop.Y - PlacementGapPixels - windowHeight;
        }

        y = Math.Clamp(y, workArea.Y, maxY);
        return new PixelPoint(x, y);
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

        if (!manual && !_autoAnnotate)
        {
            return;
        }

        var plain = SourceTextSafe();
        if (string.IsNullOrWhiteSpace(plain))
        {
            return;
        }

        if (!manual && string.Equals(_annotationSourcePlain, plain, StringComparison.Ordinal))
        {
            return;
        }

        var languageCode = _selectedSourceLanguage ?? DetectSourceLanguageCode(plain);
        var displayName = PhoneticLanguages.DisplayName(languageCode) ?? languageCode;
        var readingKind = ReadingKindForLanguage(languageCode);
        var annotator = _annotators.GetAnnotator(languageCode);
        if (annotator is null)
        {
            if (manual)
            {
                ShowStatusSafe($"暂不支持{displayName}注音。");
            }
            return;
        }

        _annotationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _annotationCancellation = cancellation;

        var enabled = _phonetics.Enabled;
        var rareOnly = _phonetics.RareOnly;
        try
        {
            var spans = await Task.Run(() =>
            {
                // 空闲释放后首次查询会重载词典，连同可用性探测一起放到后台，避免阻塞窗口。
                if (!annotator.IsAvailable)
                {
                    return null;
                }
                var wordCount = annotator.CountWords(plain);
                var profile = AnnotationProfile.Load(languageCode, enabled, rareOnly, wordCount);
                return annotator.Annotate(plain, new AnnotationContext(profile));
            }, cancellation.Token);

            if (spans is null)
            {
                if (manual && !cancellation.IsCancellationRequested)
                {
                    ShowStatusSafe($"未安装{displayName}词典，请前往设置-通用-原文注音下载；当前以纯文本展示原文。");
                }
                return;
            }

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
            cancellation.Dispose();
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
        _sourcePlain = plain;
        _sourceSpans = spans;
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
        InvalidateAnnotation(clearSpans: true);
        UpdateSourceViewerDisplay();
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
                _llmSourceInput = string.Empty;
                _receivedAnnotatedSource = true;
            }

            InvalidateAnnotation(clearSpans: true);
            _llmSourceInput += delta.Text;
            // LLM 方括号是输入协议；解析后的原文与 spans 是后续显示、复制的唯一事实源。
            (_sourcePlain, _sourceSpans) = RubyDocumentBuilder.ParseInput(_llmSourceInput);
            UpdateSourceViewerDisplay();
            SourceTextBox.Text = _sourcePlain;
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

        StatusTextBlock.Text = status;
        StatusTextBlock.IsVisible = !string.IsNullOrWhiteSpace(status);
        // 常规提示使用次要文字色，仅错误提示强调颜色。
        StatusTextBlock.Classes.Set("status-error", isError);
    }

    public void SetError(string summary, string details)
    {
        ErrorDetailsTextBox.Text = details;
        ErrorDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(details);
        ErrorDetailsExpander.IsExpanded = false;
        SetStatus(summary, isError: true);
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
        SetPlainSource(normalized);
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
        var text = GetSourceCopyText(RubyCopyKind.Plain);

        if (!string.IsNullOrEmpty(text))
        {
            await RubyInlineControl.SetClipboardTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);
            SetStatus(hasSelection ? "已复制选中原文到剪贴板。" : "已复制纯原文到剪贴板。");
        }
    }

    private async void OnCopyAnnotatedSourceClick(object? sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.SelectionStart != SourceViewer.SelectionEnd;
        var text = GetSourceCopyText(RubyCopyKind.Annotated);

        if (!string.IsNullOrEmpty(text))
        {
            await RubyInlineControl.SetClipboardTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);
            SetStatus(hasSelection ? "已复制选中带注音文本到剪贴板。" : "已复制带注音文本到剪贴板。");
        }
    }

    private async void OnCopyReadingSourceClick(object? sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.SelectionStart != SourceViewer.SelectionEnd;
        var text = GetSourceCopyText(RubyCopyKind.ReadingOnly);

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
        InvalidateAnnotation();
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
            SetPlainSource(edited);
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
            SetPlainSource(edited);

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

        if (_sourceSpans.Count > 0)
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

    /// <summary>内容、设置和语言变更统一取消旧请求并清除命中缓存，防止同文旧结果回写。</summary>
    private void InvalidateAnnotation(bool clearSpans = false)
    {
        _annotationCancellation?.Cancel();
        _annotationSourcePlain = string.Empty;
        if (clearSpans) _sourceSpans = [];
    }

    private void SetPlainSource(string plain)
    {
        InvalidateAnnotation(clearSpans: true);
        _sourcePlain = plain;
    }

    /// <summary>全文复制独立于显隐；选区沿当前词级占位提取，收藏使用相同的纯文提取路径。</summary>
    internal string GetSourceCopyText(RubyCopyKind kind)
    {
        if (SourceViewer.SelectionStart != SourceViewer.SelectionEnd)
            return RubyDocumentBuilder.ExtractText(SourceViewer, kind);
        if (kind == RubyCopyKind.Plain) return _sourcePlain;
        if (kind == RubyCopyKind.Annotated) return AnnotatedSourceText;
        return AnnotatedTextFormatter.Compose(_sourcePlain, _sourceSpans, readingOnly: true);
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

    /// <summary>
    /// 精讲区高度策略：原文/译文由各自把手控制像素高度，窗口纵向伸缩带来的剩余空间全部让给精讲。
    /// 展开时第 6 行切为星号行（Auto + 星号并存时星号独占剩余空间），折叠时回到 Auto 避免空白撑着页面。
    /// </summary>
    private void ApplyExplanationRowSizing(bool expanded)
    {
        if (_isExplanationExpanded == expanded)
        {
            return;
        }

        _isExplanationExpanded = expanded;
        var row = PageGrid.RowDefinitions[ExplanationRegionRow];
        row.Height = expanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        // 折叠时释放页面下限；展开时立即使用现有视口，不等待下一次窗口缩放。
        if (!expanded)
        {
            PageGrid.MinHeight = 0;
        }
        else
        {
            SyncPageMinHeight();
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
                    // 备注只存正文，不写「读音：」前缀（前缀会污染收藏页关键词搜索）
                    var note = !string.IsNullOrEmpty(reading) && !string.Equals(reading, text, StringComparison.Ordinal)
                        ? reading
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
