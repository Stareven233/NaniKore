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

    public ResultPanelWindow()
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
        MaxHeight = SystemParameters.WorkArea.Height * 0.6;
        // Esc 仅在结果卡实际持有键盘焦点（即已激活）时响应；未激活时按键交还用户当前窗口。
        PreviewKeyDown += OnPanelPreviewKeyDown;
        SourceViewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnSourceViewerCopy));
    }

    public event EventHandler? TranslateRequested;

    public event EventHandler? SettingsRequested;

    public string SourceText => _isEditingSource
        ? SourceTextBox.Text
        : (RubyTextParser.ToPlainText(_annotatedSource) is { Length: > 0 } plain ? plain : SourceTextBox.Text);

    public void Reset(string source, string status, bool autoTranslate = false)
    {
        _annotatedSource = source;
        SourceTextBox.Text = RubyTextParser.ToPlainText(source);
        _originalEditingText = SourceTextBox.Text;
        NormalizeSourceButton.IsEnabled = true;
        TranslationTextBox.Clear();
        ExplanationTextBox.Clear();
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
            SwitchToTextButton.Visibility = Visibility.Visible;
            SourceCaptionTextBlock.Text = "双击可编辑";
            TranslateButton.Content = "⟳ 重译";
            SourceViewer.Document = RubyDocumentBuilder.Build(source);
        }
        else
        {
            // 未开启“OCR 后自动翻译”：保持原生只读/编辑 TextBox，用户可原地编辑后再点击翻译
            _isEditingSource = true;
            SourceViewerBorder.Visibility = Visibility.Collapsed;
            SourceTextBox.Visibility = Visibility.Visible;
            SwitchToTextButton.Visibility = Visibility.Collapsed;
            SourceCaptionTextBlock.Text = "点击可编辑";
            TranslateButton.Content = "翻译";
            SourceViewer.Document = RubyDocumentBuilder.Build(source);
        }
    }

    public void SetTranslationInProgress(bool inProgress) => NormalizeSourceButton.IsEnabled = !inProgress;

    /// <summary>开始一轮新的译文流；已有译文会在首个有效结果到达时保留在新结果之后。</summary>
    public void BeginTranslation()
    {
        _translationInsertIndex = 0;
        _translationHasContent = false;

        // 发起翻译时，原文区升格/切回振假名阅读态，准备流式呈现注音
        if (_isEditingSource)
        {
            _isEditingSource = false;
            SourceTextBox.Visibility = Visibility.Collapsed;
            SourceViewerBorder.Visibility = Visibility.Visible;
            SwitchToTextButton.Visibility = Visibility.Visible;
            SourceCaptionTextBlock.Text = "双击可编辑";
            TranslateButton.Content = "⟳ 重译";
        }

        _annotatedSource = string.Empty;
        SourceViewer.Document = RubyDocumentBuilder.Build(string.Empty);
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
        if (delta.Channel == Translation.DeltaChannel.SourceText)
        {
            _annotatedSource += delta.Text;
            SourceViewer.Document = RubyDocumentBuilder.Build(_annotatedSource);
            SourceTextBox.Text = RubyTextParser.ToPlainText(_annotatedSource);
            _originalEditingText = SourceTextBox.Text;
            return;
        }

        var target = delta.Channel switch
        {
            Translation.DeltaChannel.SourceText => SourceTextBox,
            Translation.DeltaChannel.Translation => TranslationTextBox,
            Translation.DeltaChannel.Explanation => ExplanationTextBox,
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
        SetStatus("原文已规范化，请点击“重译”使用新文本。");
        TranslateButton.Content = "⟳ 重译";
    }

    private void OnSourceViewerCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var text = RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Plain);
        if (string.IsNullOrEmpty(text))
        {
            text = RubyTextParser.ToPlainText(_annotatedSource);
        }

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus("已复制纯日文到剪贴板。");
            e.Handled = true;
        }
    }

    private void OnCopyPlainSourceClick(object sender, RoutedEventArgs e)
    {
        var text = RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Plain);
        if (string.IsNullOrEmpty(text))
        {
            text = RubyTextParser.ToPlainText(_annotatedSource);
        }

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus("已复制纯日文到剪贴板。");
        }
    }

    private void OnCopyAnnotatedSourceClick(object sender, RoutedEventArgs e)
    {
        var text = RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.Annotated);
        if (string.IsNullOrEmpty(text))
        {
            text = _annotatedSource;
        }

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus("已复制带注音文本到剪贴板。");
        }
    }

    private void OnCopyReadingSourceClick(object sender, RoutedEventArgs e)
    {
        var text = RubyDocumentBuilder.ExtractFromSelection(SourceViewer.Selection, RubyCopyKind.ReadingOnly);
        if (string.IsNullOrEmpty(text))
        {
            text = RubyTextParser.ToReadingText(_annotatedSource);
        }

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus("已复制纯假名读音到剪贴板。");
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

    private void OnSwitchToTextClick(object sender, RoutedEventArgs e) =>
        EnterEditMode(SourceTextBox.Text.Length);

    private void EnterEditMode(int caretIndex)
    {
        _originalEditingText = SourceTextBox.Text;
        _isEditingSource = true;
        SourceViewerBorder.Visibility = Visibility.Collapsed;
        SourceTextBox.Visibility = Visibility.Visible;
        SwitchToTextButton.Visibility = Visibility.Collapsed;
        SourceCaptionTextBlock.Text = "编辑中 · Esc/失焦还原";
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = Math.Clamp(caretIndex, 0, SourceTextBox.Text.Length);
        SourceTextBox.SelectionLength = 0; // 严禁全选，防键盘误触丢失整句
    }

    private void ExitEditMode()
    {
        _isEditingSource = false;
        SourceTextBox.Visibility = Visibility.Collapsed;
        SourceViewerBorder.Visibility = Visibility.Visible;
        SwitchToTextButton.Visibility = Visibility.Visible;
        SourceCaptionTextBlock.Text = "双击可编辑";
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
            // 实质修改了文本：保持 TextBox，并激活右上角 [⟳ 重译]
            TranslateButton.Content = "⟳ 重译";
            SourceCaptionTextBlock.Text = "原文已修改，请重译";
        }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearTranslationClick(object sender, RoutedEventArgs e) => ClearTranslation();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
