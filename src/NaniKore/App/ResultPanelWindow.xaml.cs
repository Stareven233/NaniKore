using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Interop;
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
    private bool _translationHasContent;

    public ResultPanelWindow()
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
        MaxHeight = SystemParameters.WorkArea.Height * 0.6;
        // Esc 仅在结果卡实际持有键盘焦点（即已激活）时响应；未激活时按键交还用户当前窗口。
        PreviewKeyDown += OnPanelPreviewKeyDown;
    }

    public event EventHandler? TranslateRequested;

    public event EventHandler? SettingsRequested;

    public string SourceText => SourceTextBox.Text;

    public void Reset(string source, string status)
    {
        SourceTextBox.Text = source;
        TranslationTextBox.Clear();
        ExplanationTextBox.Clear();
        _translationHasContent = false;
        StatusBar.Show(status, InfoSeverity.Neutral);
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
        var target = delta.Channel switch
        {
            Translation.DeltaChannel.SourceText => SourceTextBox,
            Translation.DeltaChannel.Translation => TranslationTextBox,
            Translation.DeltaChannel.Explanation => ExplanationTextBox,
            _ => TranslationTextBox,
        };

        var text = delta.Text;
        if (delta.Channel == Translation.DeltaChannel.Translation && !_translationHasContent)
        {
            // 不让模型流式输出的换行把译文框顶出两行空白；跨增量保留“尚未出现正文”的状态。
            text = text.TrimStart();
            if (text.Length == 0)
            {
                return;
            }

            _translationHasContent = true;
        }

        target.AppendText(text);
        target.ScrollToEnd();
    }

    /// <summary>状态文本统一走内联提示条；失败信息用错误语义配色，成功与进行中保持中性。</summary>
    public void SetStatus(string status, bool isError = false) =>
        StatusBar.Show(status, isError ? InfoSeverity.Error : InfoSeverity.Neutral);

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

    /// <summary>结果卡持有键盘焦点时，Esc 等同关闭按钮；失焦后按键交还给当前焦点窗口。</summary>
    private void OnPanelPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private static int ClampToWorkArea(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private void OnTranslateClick(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
