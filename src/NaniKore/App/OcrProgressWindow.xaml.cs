using System.Windows;

namespace NaniKore.App;

/// <summary>
/// OCR 进度提示窗口。刻意不做取消按钮：一次识别的时长远短于用户理解取消语义的成本。
/// </summary>
internal sealed partial class OcrProgressWindow : Window
{
    public OcrProgressWindow()
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
    }
}