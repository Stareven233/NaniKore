using System.Runtime.InteropServices;
using System.Windows;
using NaniKore.App.Controls;
using NaniKore.App.Theme;
using NaniKore.Ocr;
using Clipboard = System.Windows.Clipboard;

namespace NaniKore.App;

/// <summary>
/// OCR 结果窗口。当前只呈现原文一段，阶段 3 会在此基础上扩成原文/译文/精讲三段。
/// </summary>
internal sealed partial class OcrResultWindow : Window
{
    public OcrResultWindow(OcrResult result, bool copiedToClipboard)
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);

        RecognizedText.Text = result.Text;
        StatusText.Text = $"{result.Lines.Count} 行 · {result.Elapsed.TotalMilliseconds:N0} ms" +
                          (copiedToClipboard ? " · 已复制到剪贴板" : string.Empty);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(RecognizedText.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(RecognizedText.Text);
            StatusText.Text = "已复制到剪贴板";
        }
        catch (COMException exception)
        {
            // 剪贴板被其他进程短暂占用时只提示，不让结果窗口连带失败。
            MessageDialog.Show(this, "剪贴板暂时不可用。", exception.Message, InfoSeverity.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
