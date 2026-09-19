using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Interop;
using NaniKore.Translation;
using FormsScreen = System.Windows.Forms.Screen;

namespace NaniKore.App;

/// <summary>单例普通窗口的结果面板；ShowActivated=false 保证新结果展示不抢焦点，点击交互交给系统原生激活规则。</summary>
internal sealed partial class ResultPanelWindow : Window
{
    private const nint HwndTopmost = -1;
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
    }

    private void OnRubyStatusNotification(string message) => SetStatus(message);

    public event EventHandler? TranslateRequested;

    public event EventHandler? ExplanationRequested;

    public event EventHandler<bool>? AutoExplainChanged;

    public event EventHandler? SettingsRequested;

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
    }

    public void SetTranslationInProgress(bool inProgress)
    {
        NormalizeSourceButton.IsEnabled = !inProgress;
        TranslateButton.IsEnabled = !inProgress;
        if (!inProgress)
        {
            TranslateButton.Content = "翻译";
        }
    }

    /// <summary>开始一轮新的译文流；已有译文会在首个有效结果到达时保留在新结果之后。</summary>
    public void BeginTranslation()
    {
        _translationInsertIndex = 0;
        _translationHasContent = false;
        _receivedAnnotatedSource = false;
        ClearMetrics();
        TranslateButton.Content = "翻译";
        ExplanationView.Clear();
        ExplanationExpander.IsExpanded = false;
        ExplanationExpander.IsEnabled = false;
        ExplanationExpander.Header = "📖 精讲与语法解析 (点击展开)";

        // 发起翻译时切回假名阅读态
        if (_isEditingSource)
        {
            _isEditingSource = false;
            SourceTextBox.Visibility = Visibility.Collapsed;
            SourceViewerBorder.Visibility = Visibility.Visible;
            SourceCaptionTextBlock.Text = "双击可编辑";
        }

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

        // SetWindowPos 使用物理像素坐标，能和 WinForms 选区及混合 DPI 的虚拟桌面坐标直接对齐。
        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            HwndTopmost,
            x,
            y,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>两个 Topmost 窗口之间系统不保证新窗口在上；新结果必须显式声明一次当前置顶层顶端。</summary>
    public void BringToTopmostBand()
    {
        if (!IsVisible || _windowHandle == nint.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public void Append(Translation.UnderstandDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
        {
            return;
        }

        if (delta.Channel == Translation.DeltaChannel.SourceText)
        {
            if (!_receivedAnnotatedSource)
            {
                // 收到模型首个注音片段时平滑替换初始文本，避免直接追加在 OCR 占位文本后面
                _annotatedSource = string.Empty;
                _receivedAnnotatedSource = true;
            }

            _annotatedSource += delta.Text;
            SourceViewer.Document = RubyDocumentBuilder.Build(_annotatedSource);
            SourceTextBox.Text = RubyTextParser.ToPlainText(_annotatedSource);
            _originalEditingText = SourceTextBox.Text;
            return;
        }

        if (delta.Channel == Translation.DeltaChannel.Explanation)
        {
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
            SetStatus(hasSelection ? "已复制选中日文到剪贴板。" : "已复制纯日文到剪贴板。");
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
            SetStatus(hasSelection ? "已复制选中日文到剪贴板。" : "已复制纯日文到剪贴板。");
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
            SetStatus(hasSelection ? "已复制选中纯假名读音到剪贴板。" : "已复制纯假名读音到剪贴板。");
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

    private void ExitEditMode()
    {
        _isEditingSource = false;
        SourceTextBox.Visibility = Visibility.Collapsed;
        SourceViewerBorder.Visibility = Visibility.Visible;
        SourceCaptionTextBlock.Text = "双击可编辑";
        TranslateButton.Content = "翻译";
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

    private void OnTranslateClick(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearTranslationClick(object sender, RoutedEventArgs e) => ClearTranslation();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

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

    /// <summary>第二阶段精讲开始生成：重置内容并更新标题为生成中状态。</summary>
    public void BeginExplanation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BeginExplanation);
            return;
        }

        ExplanationExpander.Header = "📖 精讲与语法解析 (正在生成…)";
        ExplanationExpander.IsExpanded = true;
        ExplanationView.Clear();
    }

    /// <summary>第二阶段精讲流式结束：恢复常态标题或提示重试。</summary>
    public void CompleteExplanation(bool success)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => CompleteExplanation(success));
            return;
        }

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

    private void OnTranslationTextBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TranslationTextBox.SelectionLength is >= 1 and <= 100)
        {
            var text = TranslationTextBox.SelectedText.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var rect = TranslationTextBox.GetRectFromCharacterIndex(TranslationTextBox.SelectionStart + TranslationTextBox.SelectionLength);
                var pt = TranslationTextBox.PointToScreen(new System.Windows.Point(rect.Right, rect.Bottom));
                SelectionBubble.ShowAt(pt, text);
            }
        }
    }

    private void OnSourceTextBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SourceTextBox.SelectionLength is >= 1 and <= 100)
        {
            var text = SourceTextBox.SelectedText.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var rect = SourceTextBox.GetRectFromCharacterIndex(SourceTextBox.SelectionStart + SourceTextBox.SelectionLength);
                var pt = SourceTextBox.PointToScreen(new System.Windows.Point(rect.Right, rect.Bottom));
                SelectionBubble.ShowAt(pt, text);
            }
        }
    }

    private void OnSourceViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SourceViewer.Selection is { IsEmpty: false } selection)
        {
            var text = RubyDocumentBuilder.ExtractFromSelection(selection, RubyCopyKind.Plain)?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length <= 100)
            {
                var pt = PointToScreen(e.GetPosition(this));
                SelectionBubble.ShowAt(pt, text);
            }
        }
    }
}
