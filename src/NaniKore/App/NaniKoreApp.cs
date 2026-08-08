using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using NaniKore.App.Settings;
using NaniKore.Capture;
using NaniKore.Config;
using NaniKore.Input;
using NaniKore.Ocr;
using NaniKore.Overlay;
using NaniKore.Translation;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NaniKore.App;

/// <summary>应用宿主统一管理热键、托盘、配置、面板和可抢占的翻译流。</summary>
internal sealed class NaniKoreApp : Application
{
    private readonly ConfigStore _configStore = new(AppContext.BaseDirectory);
    private GlobalHotkeyWindow? _hotkeyWindow;
    private OcrEngine? _ocrEngine;
    private ProviderFactory? _providerFactory;
    private TrayIconHost? _trayIcon;
    private ResultPanelWindow? _resultPanel;
    private SettingsForm? _settingsForm;
    private CancellationTokenSource? _translationCancellation;
    private byte[]? _lastImagePng;
    private bool _selecting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _providerFactory = new ProviderFactory(_configStore.Current);
        _hotkeyWindow = new GlobalHotkeyWindow(_configStore.Current.Hotkeys.Capture, () => _ = CaptureSelectAndTranslateAsync());
        _trayIcon = new TrayIconHost(() => _ = CaptureSelectAndTranslateAsync(), ShowSettings, Shutdown);

        if (!string.IsNullOrWhiteSpace(_configStore.LoadWarning))
        {
            _trayIcon.ShowWarning(_configStore.LoadWarning);
        }

        Console.WriteLine("NaniKore translation pipeline is ready.");
        Console.WriteLine($"Press {_configStore.Current.Hotkeys.Capture} to capture and translate a region.");
    }

    private async Task CaptureSelectAndTranslateAsync()
    {
        if (_selecting)
        {
            return;
        }

        _selecting = true;
        try
        {
            using var frame = DesktopCapturer.CaptureVirtualScreen();
            using var overlay = new SelectionForm(frame.Image, frame.VirtualBounds);
            if (overlay.ShowDialog() != System.Windows.Forms.DialogResult.OK || overlay.Selection is not { } selection)
            {
                return;
            }

            using var cropped = frame.Image.Clone(selection, PixelFormat.Format32bppPArgb);
            if (string.Equals(_configStore.Current.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase))
            {
                var image = EncodePng(cropped);
                _lastImagePng = image;
                var visionPanel = EnsureResultPanel();
                visionPanel.Reset(string.Empty, "正在识别并翻译图像…");
                _ = StartTranslationAsync(string.Empty, image);
                return;
            }

            var progress = new OcrProgressWindow();
            progress.Show();
            OcrResult result;
            try
            {
                result = await Task.Run(() => GetOcrEngine().Recognize(cropped));
            }
            finally
            {
                progress.Close();
            }

            if (result.Lines.Count == 0)
            {
                MessageBox.Show("没有检测到可识别文本。", "NaniKore OCR", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _lastImagePng = null;
            var panel = EnsureResultPanel();
            panel.Reset(result.Text, $"OCR 完成：{result.Lines.Count} 行 · {result.Elapsed.TotalMilliseconds:N0} ms");
            TryCopyToClipboard(result.Text);
            if (_configStore.Current.General.AutoTranslate)
            {
                _ = StartTranslationAsync(result.Text, null);
            }
        }
        catch (Win32Exception exception)
        {
            ShowFailure("Windows 截图失败。", exception);
        }
        catch (ExternalException exception)
        {
            ShowFailure("图像或剪贴板操作失败。", exception);
        }
        catch (Exception exception)
        {
            ShowFailure("选区或本地 OCR 失败。", exception);
        }
        finally
        {
            _selecting = false;
        }
    }

    private async Task StartTranslationAsync(string source, ReadOnlyMemory<byte>? imagePng)
    {
        CancelTranslation();
        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        var panel = EnsureResultPanel();
        panel.SetStatus("正在连接翻译服务…");
        try
        {
            var config = _configStore.Current;
            var request = new UnderstandRequest(UnderstandKind.Translate, source, imagePng, config.General.TargetLanguage);
            var provider = (_providerFactory ?? throw new InvalidOperationException("翻译服务尚未初始化。")).GetProvider(config.Pipeline.ActiveProfile, request);
            await foreach (var delta in provider.StreamAsync(request, cancellation.Token))
            {
                await Dispatcher.InvokeAsync(() => panel.Append(delta));
            }

            if (!cancellation.IsCancellationRequested)
            {
                panel.SetStatus("翻译完成。");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 新选区、重译或面板关闭会主动抢占旧请求，不应显示失败提示。
        }
        catch (Exception exception)
        {
            var profile = _configStore.GetActiveProfile();
            panel.SetStatus(ProviderErrorMapper.ToUserMessage(exception, profile.Id, profile.Model, profile.BaseUrl));
        }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation))
            {
                _translationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private ResultPanelWindow EnsureResultPanel()
    {
        if (_resultPanel is not null)
        {
            if (!_resultPanel.IsVisible)
            {
                _resultPanel.Show();
            }

            return _resultPanel;
        }

        var panel = new ResultPanelWindow();
        panel.TranslateRequested += (_, _) => _ = StartTranslationAsync(panel.SourceText, _lastImagePng);
        panel.SettingsRequested += (_, _) => ShowSettings();
        panel.Closed += (_, _) =>
        {
            CancelTranslation();
            _resultPanel = null;
        };
        _resultPanel = panel;
        panel.Show();
        return panel;
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Show();
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        var form = new SettingsForm(_configStore.Current, ApplySettings, TestConnectionAsync);
        form.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm = form;
        form.Show();
    }

    private void ApplySettings(AppConfig next)
    {
        var oldConfig = _configStore.Current;
        if (!string.Equals(oldConfig.Hotkeys.Capture, next.Hotkeys.Capture, StringComparison.OrdinalIgnoreCase))
        {
            ReplaceHotkey(next.Hotkeys.Capture, oldConfig.Hotkeys.Capture);
        }

        _configStore.AtomicWrite(next);
        _providerFactory?.Dispose();
        _providerFactory = new ProviderFactory(next);
    }

    private void ReplaceHotkey(string nextGesture, string previousGesture)
    {
        _hotkeyWindow?.Dispose();
        try
        {
            _hotkeyWindow = new GlobalHotkeyWindow(nextGesture, () => _ = CaptureSelectAndTranslateAsync());
        }
        catch
        {
            _hotkeyWindow = new GlobalHotkeyWindow(previousGesture, () => _ = CaptureSelectAndTranslateAsync());
            throw;
        }
    }

    private async Task<string> TestConnectionAsync(ProviderProfile profile, string? pendingApiKey)
    {
        try
        {
            var apiKey = string.IsNullOrWhiteSpace(pendingApiKey) ? SecretStore.Load(profile.Id) : pendingApiKey;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            await ProviderFactory.TestConnectionAsync(profile, apiKey ?? string.Empty, cancellation.Token);
            return "连接成功：服务已返回流式内容。";
        }
        catch (Exception exception)
        {
            return ProviderErrorMapper.ToUserMessage(exception, profile.Id, profile.Model, profile.BaseUrl);
        }
    }

    private OcrEngine GetOcrEngine() => _ocrEngine ??= OcrEngine.CreateDefault();

    private void CancelTranslation()
    {
        if (_translationCancellation is { } cancellation)
        {
            _translationCancellation = null;
            cancellation.Cancel();
        }
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // 剪贴板短暂占用不影响面板和翻译；用户可自行从原文框复制。
        }
    }

    private static void ShowFailure(string message, Exception exception)
    {
        Console.Error.WriteLine($"{message} {exception}");
        MessageBox.Show($"{message}\n\n{exception.Message}", "NaniKore", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CancelTranslation();
        _settingsForm?.Dispose();
        _resultPanel?.Close();
        _trayIcon?.Dispose();
        _providerFactory?.Dispose();
        _ocrEngine?.Dispose();
        _hotkeyWindow?.Dispose();
        base.OnExit(e);
    }
}
