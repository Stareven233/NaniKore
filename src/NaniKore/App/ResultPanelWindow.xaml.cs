using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NaniKore.Interop;
using FormsScreen = System.Windows.Forms.Screen;

namespace NaniKore.App;

/// <summary>单例非模态结果面板；新结果展示不抢焦点，用户点击原文后才显式进入编辑状态。</summary>
internal sealed partial class ResultPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int PlacementGapPixels = 8;

    private nint _windowHandle;
    private bool _sourceEditingActivated;

    public ResultPanelWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height * 0.6;
    }

    public event EventHandler? TranslateRequested;

    public event EventHandler? SettingsRequested;

    public string SourceText => SourceTextBox.Text;

    public void Reset(string source, string status)
    {
        SourceTextBox.Text = source;
        TranslationTextBox.Clear();
        ExplanationTextBox.Clear();
        StatusText.Text = status;
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

    public void Append(Translation.UnderstandDelta delta)
    {
        var target = delta.Channel switch
        {
            Translation.DeltaChannel.SourceText => SourceTextBox,
            Translation.DeltaChannel.Translation => TranslationTextBox,
            Translation.DeltaChannel.Explanation => ExplanationTextBox,
            _ => TranslationTextBox,
        };
        target.AppendText(delta.Text);
        target.ScrollToEnd();
    }

    public void SetStatus(string status) => StatusText.Text = status;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        SetNoActivate(true);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 编辑结束后恢复不抢焦点行为，后续 OCR 结果不会意外夺走正在使用的窗口。
        if (_sourceEditingActivated)
        {
            _sourceEditingActivated = false;
            SetNoActivate(true);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口意味着这次翻译不再需要；宿主会订阅 Closed 并取消请求。
        base.OnClosing(e);
    }

    private void OnSourceTextBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_sourceEditingActivated)
        {
            _sourceEditingActivated = true;
            SetNoActivate(false);
        }

        // 仅此显式点击会激活窗口，从而允许键盘输入、选中与粘贴原文。
        Activate();
        Keyboard.Focus(SourceTextBox);
    }

    private void SetNoActivate(bool enabled)
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(_windowHandle, GwlExStyle);
        var nextStyle = enabled ? style | WsExNoActivate : style & ~WsExNoActivate;
        if (nextStyle != style)
        {
            _ = NativeMethods.SetWindowLongPtr(_windowHandle, GwlExStyle, nextStyle);
        }
    }

    private static int ClampToWorkArea(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private void OnTranslateClick(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
