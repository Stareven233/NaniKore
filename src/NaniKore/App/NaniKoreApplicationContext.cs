using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NaniKore.Capture;
using NaniKore.Input;
using NaniKore.Ocr;
using NaniKore.Overlay;

namespace NaniKore.App;

internal sealed class NaniKoreApplicationContext : ApplicationContext
{
    private readonly GlobalHotkeyWindow _hotkeyWindow;
    private OcrEngine? _ocrEngine;
    private bool _selecting;

    public NaniKoreApplicationContext()
    {
        _hotkeyWindow = new GlobalHotkeyWindow(() => _ = CaptureSelectAndRecognizeAsync());

        Console.WriteLine("NaniKore C# migration preview");
        Console.WriteLine("Press Ctrl+Shift+A to capture, select, and OCR a region.");
        Console.WriteLine("Press Esc or right-click to cancel the overlay.");
    }

    private async Task CaptureSelectAndRecognizeAsync()
    {
        // 模态选区和 OCR 等待期间仍会泵送 Windows 消息；此标记防止重复热键重入第二条流水线。
        if (_selecting)
        {
            return;
        }

        _selecting = true;
        try
        {
            using var frame = DesktopCapturer.CaptureVirtualScreen();
            using var overlay = new SelectionForm(frame.Image, frame.VirtualBounds);

            if (overlay.ShowDialog() != DialogResult.OK || overlay.Selection is not { } selection)
            {
                Console.WriteLine("Selection cancelled.");
                return;
            }

            using var cropped = frame.Image.Clone(selection, PixelFormat.Format32bppPArgb);
            Console.WriteLine($"Selection completed: {selection.Width}x{selection.Height}.");

            OcrResult result;
            using (var progress = new OcrProgressForm())
            {
                progress.Show();
                progress.Refresh();
                try
                {
                    // ONNX 推理放到工作线程，UI 消息循环继续响应，首次加载模型也不会呈现为程序卡死。
                    result = await Task.Run(() => GetOcrEngine().Recognize(cropped));
                }
                finally
                {
                    progress.Close();
                }
            }

            ShowOcrResult(result);
        }
        catch (Win32Exception exception)
        {
            ShowFailure("Windows screen capture failed.", exception);
        }
        catch (ExternalException exception)
        {
            ShowFailure("Image or clipboard operation failed.", exception);
        }
        catch (Exception exception)
        {
            ShowFailure("Capture or local OCR failed.", exception);
        }
        finally
        {
            _selecting = false;
        }
    }

    private OcrEngine GetOcrEngine() => _ocrEngine ??= OcrEngine.CreateDefault();

    private static void ShowOcrResult(OcrResult result)
    {
        if (result.Lines.Count == 0)
        {
            MessageBox.Show(
                "没有检测到可识别文本。",
                "NaniKore OCR",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Console.WriteLine(result.Text);
        Console.WriteLine($"OCR completed in {result.Elapsed.TotalMilliseconds:N0} ms; lines: {result.Lines.Count}.");

        var copied = false;
        try
        {
            Clipboard.SetText(result.Text);
            copied = true;
        }
        catch (ExternalException)
        {
            // 结果窗口仍可手动重试复制；剪贴板被其他进程短暂占用不应让整次 OCR 失败。
        }

        using var resultForm = new OcrResultForm(result, copied);
        resultForm.ShowDialog();
    }

    private static void ShowFailure(string message, Exception exception)
    {
        Console.Error.WriteLine($"{message} {exception}");
        MessageBox.Show(
            $"{message}\n\n{exception.Message}",
            "NaniKore",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ocrEngine?.Dispose();
            _hotkeyWindow.Dispose();
        }

        base.Dispose(disposing);
    }
}