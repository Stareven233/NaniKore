using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using NaniKore.Interop;

namespace NaniKore.App;

/// <summary>单例非模态结果面板；窗口扩展样式确保新结果出现时不抢走游戏/视频的焦点。</summary>
internal sealed partial class ResultPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;

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
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, GwlExStyle);
        NativeMethods.SetWindowLongPtr(handle, GwlExStyle, style | WsExNoActivate);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口意味着这次翻译不再需要；宿主会订阅 Closed 并取消请求。
        base.OnClosing(e);
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
