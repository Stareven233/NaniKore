using System.ComponentModel;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Config;
using NaniKore.Interop;
using NaniKore.Phonetics;
using NaniKore.Translation;
using FormsScreen = System.Windows.Forms.Screen;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace NaniKore.App;

/// <summary>单例普通窗口的结果面板；ShowActivated=false 保证新结果展示不抢焦点，点击交互交给系统原生激活规则。</summary>
internal sealed partial class ResultPanelWindow : Window
{
    private const nint HwndTop = 0;
    private const nint HwndNoTopmost = -2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int PlacementGapPixels = 8;

    private nint _windowHandle;
    private int _translationInsertIndex;
    private bool _translationHasContent;
    private string _annotatedSource = string.Empty;
    private string _originalEditingText = string.Empty;
    private bool _isEditingSource;
    private bool _receivedAnnotatedSource;
    private string _targetLanguage = "中文（简体）";
    private readonly System.Text.StringBuilder _thinkingBuffer = new();
    private long _lastThinkingUpdateTick;
    private bool _isDisplayingThinking;
    private string? _savedTranslationForExplanation;

    // 本地注音（issue 008 Phase 3）：单一事实源是纯文本原文（SourceText 属性），
    // 注音在翻译流结束后（或换语言/编辑文本时）单次异步叠加，杜绝流式抖动重算。
    private CancellationTokenSource? _annotationCancellation;
    private Task? _annotationTask;
    private string _annotationSourcePlain = string.Empty;
    private PhoneticsConfig _phonetics = new();
    private string? _selectedSourceLanguage;
    private bool _loadingSourceLanguages;

    // 当前原文的注音种类：原文区右键菜单与状态提示据此说「假名/拼音/音标」，不再一律按日语措辞
    private RubyReadingKind _sourceReadingKind = RubyReadingKind.Unknown;

    public ResultPanelWindow()
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
        // 不限制 MaxHeight，允许用户自由拖拽调整结果卡高度以浏览长文本与精讲内容。
        // Esc 仅在结果卡实际持有键盘焦点（即已激活）时响应；未激活时按键交还用户当前窗口。
        PreviewKeyDown += OnPanelPreviewKeyDown;
        SourceViewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnSourceViewerCopy));
        RubyInlineControl.StatusNotificationRequested += OnRubyStatusNotification;
        SizeChanged += (_, _) =>
        {
            if (ActualHeight > 0)
            {
                ExplanationView.MaxHeight = Math.Max(160, ActualHeight * 0.45);
            }
        };

        ExplanationView.DegradedChanged += (_, isDegraded) =>
        {
            if (isDegraded)
            {
                SetStatus("未能完全识别为微结构化卡片，已展示原始排版精讲。");
            }
        };
        // 卡片收藏的默认语言由面板按本轮原文探测现算后透传（issue 015 / 2.3-C）：词汇/语法卡不做选择 UI。
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

        // 文本框内部会在拖选时将 MouseUp 标记为 Handled，必须以 handledEventsToo: true 监听，确保划词松开鼠标时立即可见气泡
        TranslationTextBox.AddHandler(
            UIElement.MouseUpEvent,
            new MouseButtonEventHandler(OnTranslationTextBoxMouseUp),
            handledEventsToo: true);

        SourceTextBox.AddHandler(
            UIElement.MouseUpEvent,
            new MouseButtonEventHandler(OnSourceTextBoxMouseUp),
            handledEventsToo: true);

        // 源语言下拉：默认「自动探测」+ 三语（与设置页词典卡片同一份清单）
        PopulateSourceLanguageBox();

        // 点击窗口内其他文字、移动窗口或失焦时立即隐藏收藏气泡
        PreviewMouseDown += OnPanelPreviewMouseDown;
        LocationChanged += (_, _) =>
        {
            // 移动窗口会带走气泡的定位基准，直接收起。
            if (SelectionBubble.BubblePopup.IsOpen)
            {
                SelectionBubble.Hide();
            }
        };
        Deactivated += (_, _) =>
        {
            // 延迟判定（issue 015 / 1.1-B）：点气泡（独立 PopupRoot HWND）会抢激活并触发 Deactivated，
            // 立即 Hide 正是“点编辑框/下拉/收藏按钮气泡就消失”的根因；推迟到输入优先级回读，
            // 仅当气泡未持有键盘焦点（用户真的切到别处）时才收起。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (SelectionBubble.BubblePopup.IsOpen && !SelectionBubble.ContainsLogicalFocus())
                {
                    SelectionBubble.Hide();
                }
            }), DispatcherPriority.Input);
        };
    }

    private void OnRubyStatusNotification(string message) => SetStatus(message);

    public event EventHandler? TranslateRequested;

    public event EventHandler? ExplanationRequested;

    public event EventHandler<bool>? AutoExplainChanged;

    public event EventHandler<SettingsTab>? SettingsRequested;

    public string SourceText => _isEditingSource
        ? SourceTextBox.Text
        : (RubyTextParser.ToPlainText(_annotatedSource) is { Length: > 0 } plain ? plain : SourceTextBox.Text);

    public string TranslationText => TranslationTextBox.Text;

    public string ExplanationText => ExplanationView.RawText;

    /// <summary>注入生词与知识点收藏服务。</summary>
    public void SetFavoriteService(Favorites.IFavoriteService? service)
    {
        ExplanationView.SetFavoriteService(service);
        SelectionBubble.SetFavoriteService(service);
    }

    /// <summary>同步设置页当前的目标语言（源语言探测的兜底口径与提示词展开保持一致）。</summary>
    public void SetTargetLanguage(string? targetLanguage)
    {
        _targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "中文（简体）" : targetLanguage;
    }

    /// <summary>
    /// 本轮原文的源语言：对当前纯文本原文现算探测，映射回共享语言列表的母语写法展示名。
    /// 卡片收藏与划词气泡的语言默认值都取这里，保证「探测 → 映射 → 落库」单点维护。
    /// </summary>
    internal string CurrentSourceLanguage
    {
        get
        {
            var detection = LanguageDetector.Detect(SourceText, _targetLanguage);
            return LanguageChoices.FromDetection(detection);
        }
    }

    /// <summary>同步设置页的注音开关（保存设置后由宿主刷新，不打断当前展示）。</summary>
    public void SetPhoneticsOptions(PhoneticsConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _phonetics = options;
    }

    /// <summary>填充源语言下拉：默认「自动探测」+ 三语展示名（与设置页词典卡片同一清单）。</summary>
    private void PopulateSourceLanguageBox()
    {
        _loadingSourceLanguages = true;
        SourceLanguageBox.Items.Add(new SourceLanguageChoice(null, "自动探测"));
        foreach (var language in PhoneticLanguages.All)
        {
            SourceLanguageBox.Items.Add(new SourceLanguageChoice(language.Code, language.DisplayName));
        }

        SourceLanguageBox.SelectedIndex = 0;
        _loadingSourceLanguages = false;
    }

    /// <summary>源语言下拉项：LanguageCode 为 null 表示按原文自动探测。</summary>
    private sealed record SourceLanguageChoice(string? LanguageCode, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>更新并显示本次调用的 Token 消耗与性能指标。</summary>
    public void SetMetrics(TranslationMetrics metrics)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetMetrics(metrics));
            return;
        }

        MetricsTextBlock.Text = metrics.FormatSummary();
        MetricsBar.Visibility = Visibility.Visible;
    }

    /// <summary>清空并折叠本次指标栏。</summary>
    public void ClearMetrics()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearMetrics);
            return;
        }

        MetricsTextBlock.Text = string.Empty;
        MetricsBar.Visibility = Visibility.Collapsed;
    }

    public void Reset(string source, string status, bool autoTranslate = false, bool autoExplain = false)
    {
        _annotatedSource = source;
        SourceTextBox.Text = RubyTextParser.ToPlainText(source);
        _originalEditingText = SourceTextBox.Text;
        _receivedAnnotatedSource = false;
        NormalizeSourceButton.IsEnabled = true;
        AnnotateSourceButton.IsEnabled = true;
        TranslateButton.Content = "翻译"; // 初次进入始终为“翻译”
        TranslationTextBox.Clear();
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
        ErrorDetailsExpander.Visibility = Visibility.Collapsed;
        ErrorDetailsExpander.IsExpanded = false;
        StatusBar.Show(status, InfoSeverity.Neutral);

        if (autoTranslate)
        {
            // 已开启“OCR 后自动翻译”：OCR 识别完成后直接以 FlowDocument 呈现，流式接收注音，杜绝跳闪
            _isEditingSource = false;
            SourceViewerBorder.Visibility = Visibility.Visible;
            SourceTextBox.Visibility = Visibility.Collapsed;
            SourceCaptionTextBlock.Text = "双击可编辑";
            SourceViewer.Document = RubyDocumentBuilder.Build(source);
        }
        else
        {
            // 未开启“OCR 后自动翻译”：保持原生只读/编辑 TextBox，用户可原地编辑后再点击翻译
            _isEditingSource = true;
            SourceViewerBorder.Visibility = Visibility.Collapsed;
            SourceTextBox.Visibility = Visibility.Visible;
            SourceCaptionTextBlock.Text = "点击可编辑";
            SourceViewer.Document = RubyDocumentBuilder.Build(source);
        }

        // 新原文落地即单次异步注音（issue 008 Phase 3）：与是否翻译解耦，
        // 粘贴文本/OCR 文本进入面板后即可看到注音视图。
        _annotationSourcePlain = string.Empty;
        _ = AnnotateSourceAsync();
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

    /// <summary>开始一轮新的译文流；已有译文会在首个有效结果到达时保留在新结果之后。</summary>
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
        ExplanationView.Clear();
        ExplanationExpander.IsExpanded = false;
        ExplanationExpander.IsEnabled = false;
        ExplanationExpander.Header = "📖 精讲与语法解析 (点击展开)";

        // 发起翻译时切回假名阅读态；若编辑框有改动，立即提交为当前原文事实源
        CommitEditingSourceIfActive();

        // 保留现有原文（OCR识别文本或用户当前编辑文本），禁止在翻译中原文区瞬间变白或被清空
        if (string.IsNullOrEmpty(_annotatedSource) && !string.IsNullOrEmpty(SourceTextBox.Text))
        {
            _annotatedSource = SourceTextBox.Text;
            SourceViewer.Document = RubyDocumentBuilder.Build(_annotatedSource);
        }
    }

    /// <summary>清空译文及当前流式片段的插入位置。</summary>
    public void ClearTranslation()
    {
        TranslationTextBox.Clear();
        _translationInsertIndex = 0;
        _translationHasContent = false;
        _isDisplayingThinking = false;
        _thinkingBuffer.Clear();
        _lastThinkingUpdateTick = 0;
        _savedTranslationForExplanation = null;
    }

    /// <summary>将结果卡贴在本次桌面选区下方；下方空间不足时自动翻到选区上方。</summary>
    public void PositionNearSelection(Rectangle selectionOnDesktop)
    {
        if (!IsVisible || _windowHandle == nint.Zero ||
            !NativeMethods.GetWindowRect(_windowHandle, out var windowBounds))
        {
            return;
        }

        var workArea = FormsScreen.FromRectangle(selectionOnDesktop).WorkingArea;
        var windowWidth = windowBounds.Right - windowBounds.Left;
        var windowHeight = windowBounds.Bottom - windowBounds.Top;
        var x = ClampToWorkArea(selectionOnDesktop.Left, workArea.Left, workArea.Right - windowWidth);
        var y = selectionOnDesktop.Bottom + PlacementGapPixels;

        if (y + windowHeight > workArea.Bottom)
        {
            y = selectionOnDesktop.Top - PlacementGapPixels - windowHeight;
        }

        y = ClampToWorkArea(y, workArea.Top, workArea.Bottom - windowHeight);

        // SetWindowPos 使用物理像素坐标，能和 WinForms 选区及混合 DPI 的虚拟桌面坐标直接对齐；
        // 使用 HwndNoTopmost 确保结果卡处于普通窗口顶层，不再强制全局置顶。
        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            HwndNoTopmost,
            x,
            y,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>新结果展示时，将普通窗口提升到非置顶层顶端，避免被其他普通窗口完全遮盖，同时不抢焦点也不全局置顶。</summary>
    public void BringToTopmostBand()
    {
        if (!IsVisible || _windowHandle == nint.Zero)
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

    /// <summary>
    /// 单次异步注音（issue 008 Phase 3）：对当前纯文本原文跑本地引擎，区间结果经
    /// 「区间 → 汉字[读音] 文本 → FlowDocument」既有链路叠加。单一事实源始终是纯文本，
    /// 注音形态只是派生视图；翻译流式过程中绝不重算，只在原文落地/翻译完成/换语言时单次触发。
    /// </summary>
    internal Task AnnotateSourceAsync(bool manual = false)
    {
        // 保留最近一次注音任务句柄：自测与宿主可等待其落地，避免异步结果竞态
        _annotationTask = RunAnnotationAsync(manual);
        return _annotationTask;
    }

    internal Task? PendingAnnotation => _annotationTask;

    /// <summary>当前原文的注音形态（汉字[读音]）；自测与复制菜单读取同一字段。</summary>
    internal string AnnotatedSourceText => _annotatedSource;

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

        // WPF 控件线程亲和：原文读取同样可能来自任意调用线程，统一走安全入口
        var plain = SourceTextSafe();
        if (string.IsNullOrWhiteSpace(plain))
        {
            return;
        }

        // 同一份纯文本已叠加过注音（且非手动重选语言）时跳过，避免重复计算
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
            // 软降级：保持纯文本展示 + 温和提示，绝不打断翻译流程
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
            // 词数决定过滤口径：候选词不足阈值时短文本全注（停用词与难词过滤一并让位），否则维持仅难词。
            // 计数与标注同一次后台执行，UI 线程只负责最终落地。
            var spans = await Task.Run(() =>
            {
                var wordCount = annotator.CountWords(plain);
                var profile = AnnotationProfile.Load(languageCode, _phonetics.Enabled, _phonetics.RareOnly, wordCount);
                return annotator.Annotate(plain, new AnnotationContext(profile));
            }, cancellation.Token);

            // await 续体未必回到面板线程（自测/无同步上下文场景）：整段落地逻辑显式跳回
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => ApplyAnnotationIfCurrent(plain, spans, displayName, readingKind, manual, cancellation.Token));
                return;
            }

            ApplyAnnotationIfCurrent(plain, spans, displayName, readingKind, manual, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 被更新的注音抢占，静默丢弃
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

    /// <summary>原文未被取消、也未被并发修改时才落地；只允许在面板线程调用。</summary>
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
            // 计算期间原文已变化：丢弃过期结果，等下一次单次注音
            return;
        }

        ApplyAnnotation(plain, spans, displayName, readingKind, manual);
    }

    /// <summary>线程安全读原文（供注音计算前的快照使用）。</summary>
    private string SourceTextSafe() =>
        Dispatcher.CheckAccess() ? SourceText : Dispatcher.Invoke(() => SourceText);

    /// <summary>线程安全写状态条：注音回调可能发生在任意线程，绝不允许跨线程触碰 WPF 控件。</summary>
    private void ShowStatusSafe(string message, bool isError = false)
    {
        if (Dispatcher.CheckAccess())
        {
            SetStatus(message, isError);
            return;
        }

        _ = Dispatcher.BeginInvoke(() => SetStatus(message, isError));
    }

    /// <summary>把注音区间叠加为注音形态：区间 → 汉字[读音] 文本 → FlowDocument，复用既有 ruby 链路。</summary>
    private void ApplyAnnotation(string plain, IReadOnlyList<AnnotationSpan> spans, string displayName, RubyReadingKind readingKind, bool manual)
    {
        var ruby = spans.Count == 0 ? plain : AnnotatedTextFormatter.Compose(plain, spans);
        _annotatedSource = ruby;
        _annotationSourcePlain = plain;
        _sourceReadingKind = readingKind;
        ApplySourceMenuWording();
        if (!_isEditingSource)
        {
            SourceViewer.Document = RubyDocumentBuilder.Build(ruby);
        }

        SourceTextBox.Text = plain;
        _originalEditingText = plain;
        if (manual)
        {
            // 短文本全注口径下「没有命中」指的是没有可标注的候选词，不再暗示难词过滤
            SetStatus(spans.Count == 0
                ? $"已按{displayName}重新注音：没有可标注的单词。"
                : $"已按{displayName}重新注音（{spans.Count} 词）。");
        }
    }

    /// <summary>
    /// 原文区右键菜单随注音种类改措辞：日语说假名、中文说拼音、英语说音标，
    /// 免得英语原文上也挂「复制纯日文 / 汉字[假名]」。
    /// </summary>
    private void ApplySourceMenuWording()
    {
        var reading = RubyReadingTerms.Reading(_sourceReadingKind);
        CopyPlainSourceMenuItem.Header = "复制纯原文";
        CopyAnnotatedSourceMenuItem.Header = $"复制带注音文本 ({RubyReadingTerms.Base(_sourceReadingKind)}[{reading}])";
        CopyReadingSourceMenuItem.Header = $"复制纯{reading}";
    }

    /// <summary>源语言代码 → 注音种类（原文区菜单与状态提示的措辞口径）。</summary>
    private static RubyReadingKind ReadingKindForLanguage(string? languageCode) => languageCode switch
    {
        "ja" => RubyReadingKind.Kana,
        "zh" => RubyReadingKind.Pinyin,
        "en" => RubyReadingKind.Ipa,
        _ => RubyReadingKind.Unknown
    };

    /// <summary>自动探测口径：假名优先判日语，其次简体中文，再拉丁英语；无声无息的纯汉字兜底按中文拼音。</summary>
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

    private void OnSourceLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSourceLanguages)
        {
            return;
        }

        _selectedSourceLanguage = (SourceLanguageBox.SelectedItem as SourceLanguageChoice)?.LanguageCode;
        // 手选语言必须强制重算：即便纯文本未变，也要按新语言重新叠加
        _ = AnnotateSourceAsync(manual: true);
    }

    public void Append(Translation.UnderstandDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
        {
            return;
        }

        if (delta.Channel == Translation.DeltaChannel.Thinking)
        {
            _thinkingBuffer.Append(delta.Text);
            var now = Environment.TickCount64;
            // 首次思考片段立即反馈，后续以约 1200ms 节流刷新
            if (_lastThinkingUpdateTick == 0 || now - _lastThinkingUpdateTick >= 1200)
            {
                _lastThinkingUpdateTick = now;
                var snippet = ExtractThinkingSnippet(_thinkingBuffer.ToString());
                if (snippet.Length > 0)
                {
                    TranslationTextBox.Text = $"💭 [思考中] {snippet}";
                    _isDisplayingThinking = true;
                    TranslationTextBox.ScrollToHome();
                }
            }
            return;
        }

        if (delta.Channel == Translation.DeltaChannel.SourceText)
        {
            if (!_receivedAnnotatedSource)
            {
                // 视觉链路首个转录片段到达时平滑替换初始占位文本，避免追加在空占位后面
                _annotatedSource = string.Empty;
                _receivedAnnotatedSource = true;
            }

            // 转录按纯文本累积（协议已不要求模型注音）；作废注音缓存，迫使流结束后重新叠加
            _annotatedSource += delta.Text;
            _annotationSourcePlain = string.Empty;
            SourceViewer.Document = RubyDocumentBuilder.Build(_annotatedSource);
            SourceTextBox.Text = RubyTextParser.ToPlainText(_annotatedSource);
            _originalEditingText = SourceTextBox.Text;
            return;
        }

        if (delta.Channel == Translation.DeltaChannel.Explanation)
        {
            if (_isDisplayingThinking)
            {
                TranslationTextBox.Text = _savedTranslationForExplanation ?? string.Empty;
                _isDisplayingThinking = false;
            }

            ExplanationView.AppendChunk(delta.Text);
            return;
        }

        var target = delta.Channel switch
        {
            Translation.DeltaChannel.SourceText => SourceTextBox,
            Translation.DeltaChannel.Translation => TranslationTextBox,
            _ => TranslationTextBox,
        };

        var text = delta.Text;
        if (delta.Channel == Translation.DeltaChannel.Translation)
        {
            if (_isDisplayingThinking)
            {
                TranslationTextBox.Clear();
                _isDisplayingThinking = false;
            }

            if (!_translationHasContent)
            {
                // 不让模型流式输出的换行把译文框顶出两行空白；跨增量保留“尚未出现正文”的状态。
                text = text.TrimStart();
                if (text.Length == 0)
                {
                    return;
                }

                // 新一轮译文插到最前面，并用换行和上一轮结果分隔。
                var previous = TranslationTextBox.Text;
                TranslationTextBox.Text = previous.Length == 0
                    ? text
                    : text + Environment.NewLine + previous;
                _translationInsertIndex = text.Length;
                _translationHasContent = true;
                TranslationTextBox.ScrollToHome();
                return;
            }

            TranslationTextBox.Text = TranslationTextBox.Text.Insert(_translationInsertIndex, text);
            _translationInsertIndex += text.Length;
            TranslationTextBox.ScrollToHome();
            return;
        }

        target.AppendText(text);
        target.ScrollToEnd();
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

    /// <summary>状态文本统一走内联提示条；新的非错误状态会清除上一轮的可展开错误详情。</summary>
    public void SetStatus(string status, bool isError = false)
    {
        if (!isError)
        {
            ErrorDetailsTextBox.Clear();
            ErrorDetailsExpander.Visibility = Visibility.Collapsed;
            ErrorDetailsExpander.IsExpanded = false;
        }

        StatusBar.Show(status, isError ? InfoSeverity.Error : InfoSeverity.Neutral);
    }

    /// <summary>摘要保持易读，完整服务端错误留在可展开区域供排障时查看。</summary>
    public void SetError(string summary, string details)
    {
        ErrorDetailsTextBox.Text = details;
        ErrorDetailsExpander.Visibility = string.IsNullOrWhiteSpace(details)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ErrorDetailsExpander.IsExpanded = false;
        StatusBar.Show(summary, InfoSeverity.Error);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 仅缓存句柄供 SetWindowPos 定位使用；不再干预扩展样式，激活完全遵循系统默认行为。
        _windowHandle = new WindowInteropHelper(this).Handle;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口意味着这次翻译不再需要；宿主会订阅 Closed 并取消请求。
        base.OnClosing(e);
    }

    /// <summary>结果卡持有键盘焦点时，若在未修改的编辑态按 Esc 则还原阅读态；其余情况 Esc 等同关闭按钮。</summary>
    private void OnPanelPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SelectionBubble.BubblePopup.IsOpen)
            {
                SelectionBubble.Hide();
                e.Handled = true;
                return;
            }

            if (_isEditingSource && string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal))
            {
                ExitEditMode();
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private static int ClampToWorkArea(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private void OnNormalizeSourceClick(object sender, RoutedEventArgs e)
    {
        var original = SourceText;
        var normalized = Translation.SourceTextNormalizer.Normalize(original);
        if (string.Equals(original, normalized, StringComparison.Ordinal))
        {
            SetStatus(string.IsNullOrWhiteSpace(original) ? "没有可规范化的原文。" : "原文已经是规范格式。");
            return;
        }

        // 规范化可能改变文本长度；尽量保留用户当前编辑位置，避免按钮操作打断校对。
        var caretIndex = SourceTextBox.CaretIndex;
        SourceTextBox.Text = normalized;
        SourceTextBox.CaretIndex = Math.Min(caretIndex, normalized.Length);
        _annotatedSource = normalized;
        _annotationSourcePlain = string.Empty;
        SourceViewer.Document = RubyDocumentBuilder.Build(normalized);
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

    private void OnSourceViewerCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var hasSelection = SourceViewer.Selection is { IsEmpty: false };
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Plain)
            : RubyTextParser.ToPlainText(_annotatedSource);

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus(hasSelection ? "已复制选中原文到剪贴板。" : "已复制纯原文到剪贴板。");
            e.Handled = true;
        }
    }

    private void OnCopyPlainSourceClick(object sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.Selection is { IsEmpty: false };
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Plain)
            : RubyTextParser.ToPlainText(_annotatedSource);

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus(hasSelection ? "已复制选中原文到剪贴板。" : "已复制纯原文到剪贴板。");
        }
    }

    private void OnCopyAnnotatedSourceClick(object sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.Selection is { IsEmpty: false };
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Annotated)
            : _annotatedSource;

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus(hasSelection ? "已复制选中带注音文本到剪贴板。" : "已复制带注音文本到剪贴板。");
        }
    }

    private void OnCopyReadingSourceClick(object sender, RoutedEventArgs e)
    {
        var hasSelection = SourceViewer.Selection is { IsEmpty: false };
        var text = hasSelection
            ? RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.ReadingOnly)
            : RubyTextParser.ToReadingText(_annotatedSource);

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            var reading = RubyReadingTerms.Reading(_sourceReadingKind);
            SetStatus(hasSelection ? $"已复制选中纯{reading}到剪贴板。" : $"已复制纯{reading}到剪贴板。");
        }
    }

    private void OnSourceViewerPreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var caretIndex = SourceTextBox.Text.Length;
        if (SourceViewer.Document is not null && SourceViewer.Selection is { IsEmpty: false } selection)
        {
            var prefixRange = new TextRange(SourceViewer.Document.ContentStart, selection.Start);
            caretIndex = RubyDocumentBuilder.ExtractFromSelection(prefixRange, RubyCopyKind.Plain).Length;
        }

        EnterEditMode(caretIndex);
        e.Handled = true;
    }

    internal void EnterEditMode(int caretIndex = -1)
    {
        _originalEditingText = SourceTextBox.Text;
        _isEditingSource = true;
        SourceViewerBorder.Visibility = Visibility.Collapsed;
        SourceTextBox.Visibility = Visibility.Visible;
        SourceCaptionTextBlock.Text = "编辑中 · Esc/失焦还原";
        SourceTextBox.Focus();
        if (caretIndex < 0)
        {
            caretIndex = SourceTextBox.Text.Length;
        }
        SourceTextBox.CaretIndex = Math.Clamp(caretIndex, 0, SourceTextBox.Text.Length);
        SourceTextBox.SelectionLength = 0; // 严禁全选，防键盘误触丢失整句
    }

    /// <summary>
    /// 若当前处于原文编辑态，将编辑框内容提交为原文单一事实源，切出编辑态并更新阅读态底字。
    /// 无论通过点击翻译/重译按钮、快捷键还是宿主调用 BeginTranslation，均以此收口保证原文不回滚。
    /// </summary>
    internal void CommitEditingSourceIfActive()
    {
        if (!_isEditingSource)
        {
            return;
        }

        var edited = SourceTextBox.Text;
        _isEditingSource = false;
        SourceTextBox.Visibility = Visibility.Collapsed;
        SourceViewerBorder.Visibility = Visibility.Visible;
        SourceCaptionTextBlock.Text = "双击可编辑";
        TranslateButton.Content = "翻译";

        if (!string.Equals(edited, _originalEditingText, StringComparison.Ordinal))
        {
            _annotatedSource = edited;
            _annotationSourcePlain = string.Empty;
            _originalEditingText = edited;
            SourceViewer.Document = RubyDocumentBuilder.Build(edited);
        }
    }

    private void ExitEditMode()
    {
        var wasModified = !string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal);
        CommitEditingSourceIfActive();

        if (wasModified)
        {
            _ = AnnotateSourceAsync();
        }
    }

    private void OnSourceTextBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal))
            {
                ExitEditMode();
                e.Handled = true;
            }
        }
    }

    private void OnSourceTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (string.Equals(SourceTextBox.Text, _originalEditingText, StringComparison.Ordinal))
        {
            ExitEditMode();
        }
        else
        {
            // 实质修改了文本：保持 TextBox；若此前已产出译文则提示重译，未翻译过则保持翻译
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

    private void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        CommitEditingSourceIfActive();
        TranslateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 显式注音入口：与翻译彻底解耦——点击即把当前原文按源语言口径叠加读音并展示假名阅读态。
    /// 编辑态先提交为事实源（假名只能由 FlowDocument 承载，编辑框里只看得到纯文本）；
    /// 手动触发必须走 manual 重算，即便纯文本未变也按当前下拉语言重新叠加。
    /// </summary>
    private void OnAnnotateSourceClick(object sender, RoutedEventArgs e)
    {
        CommitEditingSourceIfActive();
        if (string.IsNullOrWhiteSpace(SourceText))
        {
            SetStatus("没有可注音的原文。");
            return;
        }

        _ = AnnotateSourceAsync(manual: true);
    }

    private void OnClearTranslationClick(object sender, RoutedEventArgs e) => ClearTranslation();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, SettingsTab.Translation);

    /// <summary>控制底部精讲 Expander 的启用状态（仅在产出有效译文后激活）。</summary>
    public void SetExplanationEnabled(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetExplanationEnabled(enabled));
            return;
        }

        ExplanationExpander.IsEnabled = enabled;
    }

    /// <summary>第二阶段精讲开始生成：重置内容并更新标题为生成中状态，暂存译文避免被思考提示覆盖。</summary>
    public void BeginExplanation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BeginExplanation);
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

    /// <summary>第二阶段精讲流式结束：恢复常态标题或提示重试，若译文区仍处于思考提示状态则无缝恢复暂存译文。</summary>
    public void CompleteExplanation(bool success)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => CompleteExplanation(success));
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

    private void OnExplanationExpanderExpanded(object sender, RoutedEventArgs e)
    {
        ExplanationRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>获取或设置当前面板是否开启“自动精讲”。</summary>
    public bool IsAutoExplainEnabled
    {
        get => AutoExplainToggle.IsChecked == true;
        set => AutoExplainToggle.IsChecked = value;
    }

    /// <summary>线程安全地设置自动精讲开关状态。</summary>
    public void SetAutoExplain(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetAutoExplain(enabled));
            return;
        }

        AutoExplainToggle.IsChecked = enabled;
    }

    /// <summary>展开精讲折叠卡（若未展开则置位，从而自然触发 Expanded 事件）。</summary>
    public void ExpandExplanation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ExpandExplanation);
            return;
        }

        ExplanationExpander.IsExpanded = true;
    }

    private void OnAutoExplainToggleClicked(object sender, RoutedEventArgs e)
    {
        AutoExplainChanged?.Invoke(this, AutoExplainToggle.IsChecked == true);
    }

    private static Rect GetSelectionOrMouseRect(WpfTextBox textBox, MouseButtonEventArgs e)
    {
        if (textBox.SelectionLength > 0)
        {
            var startChar = textBox.SelectionStart;
            var endChar = Math.Max(startChar, textBox.SelectionStart + textBox.SelectionLength - 1);
            var startRect = textBox.GetRectFromCharacterIndex(startChar);
            var endRect = textBox.GetRectFromCharacterIndex(endChar);

            // 同一行选区：取整段文字的包围盒，使气泡直接在选中文本的正下方弹出
            if (!startRect.IsEmpty && !endRect.IsEmpty && Math.Abs(startRect.Top - endRect.Top) < 4)
            {
                var width = Math.Max(4, endRect.Right - startRect.Left);
                var height = Math.Max(16, endRect.Height);
                return new Rect(startRect.Left, startRect.Top, width, height);
            }

            // 跨行选区：贴合最后一行选中文本的末尾字
            if (!endRect.IsEmpty && !double.IsNaN(endRect.X))
            {
                return new Rect(endRect.Left, endRect.Top, Math.Max(4, endRect.Width), Math.Max(16, endRect.Height));
            }
        }

        var pt = e.GetPosition(textBox);
        return new Rect(pt.X, pt.Y, 1, 1);
    }

    private void OnTranslationTextBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseUpInsideBubble(e))
        {
            return;
        }

        if (TranslationTextBox.SelectionLength is >= 1 and <= 100)
        {
            var text = TranslationTextBox.SelectedText.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var rect = GetSelectionOrMouseRect(TranslationTextBox, e);
                SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                SelectionBubble.ShowAt(TranslationTextBox, rect, text);
            }
        }
    }

    private void OnSourceTextBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseUpInsideBubble(e))
        {
            return;
        }

        if (SourceTextBox.SelectionLength is >= 1 and <= 100)
        {
            var text = SourceTextBox.SelectedText.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var rect = GetSelectionOrMouseRect(SourceTextBox, e);
                SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                SelectionBubble.ShowAt(SourceTextBox, rect, text);
            }
        }
    }

    private void OnSourceViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseUpInsideBubble(e))
        {
            return;
        }

        if (SourceViewer.Selection is { IsEmpty: false } selection)
        {
            var text = RubyDocumentBuilder.ExtractFromSelection(selection, RubyCopyKind.Plain)?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length <= 100)
            {
                var mousePos = e.GetPosition(SourceViewer);
                // 注音单词收藏补齐（issue：原文区收藏丢注音）：底字之外再取纯读音，
                // 当选区确实带注音（读音与底字不同）时，把读音作为备注随收藏一并落库；
                // 无注音的普通选区不写备注，与译文框/原文编辑框口径一致。
                var reading = RubyDocumentBuilder.ExtractFromSelection(selection, RubyCopyKind.ReadingOnly)?.Trim();
                var note = !string.IsNullOrEmpty(reading) && !string.Equals(reading, text, StringComparison.Ordinal)
                    ? $"读音: {reading}"
                    : null;
                SelectionBubble.SetDefaultLanguage(CurrentSourceLanguage);
                SelectionBubble.ShowAt(SourceViewer, new Rect(mousePos.X, mousePos.Y, 1, 1), text, note);
            }
        }
    }

    /// <summary>
    /// 当气泡可见且用户在主窗口内点击任何区域（包括其他文字、空白处、按钮）时立即隐藏气泡。
    /// 气泡处于独立 PopupRoot HWND 且打开时已被显式提升到 topmost band 顶端（1.1 修复），
    /// 内部点击不会触发此事件；ContainsScreenPoint 兜底防止置顶竞态把气泡内点击误判成窗口点击。
    /// </summary>
    private void OnPanelPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SelectionBubble.BubblePopup.IsOpen)
        {
            return;
        }

        if (TryGetMouseScreenPoint(e, out var point) && SelectionBubble.ContainsScreenPoint(point))
        {
            return;
        }

        // 点击窗口内其他文字/按钮/空白处：气泡在独立 PopupRoot HWND，其内部点击不会走到这里。
        SelectionBubble.Hide();
    }

    /// <summary>
    /// 划词 MouseUp 的第二道保险（issue 015 / 1.1）：鼠标松开点落在已打开气泡的矩形内时，
    /// 说明这次点击本来属于气泡（即便因置顶竞态漏到了文本框，选区也仍然完好），不再二次弹出新气泡。
    /// 选区被点击清空的正常路径（SelectionLength=0）本来就不会走到这里，不受影响。
    /// </summary>
    private bool IsMouseUpInsideBubble(MouseButtonEventArgs e) =>
        SelectionBubble.BubblePopup.IsOpen &&
        TryGetMouseScreenPoint(e, out var point) &&
        SelectionBubble.ContainsScreenPoint(point);

    /// <summary>把鼠标事件的窗口相对坐标换算成屏幕物理像素；窗口尚未落到 HwndSource（未 Show）时返回 false。</summary>
    private bool TryGetMouseScreenPoint(MouseButtonEventArgs e, out System.Windows.Point screenPoint)
    {
        if (PresentationSource.FromVisual(this) is null)
        {
            screenPoint = default;
            return false;
        }

        screenPoint = PointToScreen(e.GetPosition(this));
        return true;
    }
}
