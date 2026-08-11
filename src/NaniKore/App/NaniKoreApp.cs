using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Capture;
using NaniKore.Config;
using NaniKore.Input;
using NaniKore.Ocr;
using NaniKore.Overlay;
using NaniKore.Translation;
using Application = System.Windows.Application;

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
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _translationCancellation;
    private byte[]? _lastImagePng;
    private bool _selecting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 主题资源必须先于任何窗口创建就位，否则首个窗口会以系统默认样式闪现一帧。
        ThemeManager.Initialize(this);
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
            var selectionOnDesktop = new System.Drawing.Rectangle(
                frame.VirtualBounds.X + selection.X,
                frame.VirtualBounds.Y + selection.Y,
                selection.Width,
                selection.Height);
            if (string.Equals(_configStore.Current.Pipeline.Mode, "vision_oneshot", StringComparison.OrdinalIgnoreCase))
            {
                var image = EncodePng(cropped);
                _lastImagePng = image;
                var visionPanel = EnsureResultPanel(selectionOnDesktop);
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
                MessageDialog.Show(_resultPanel, "没有检测到可识别文本。", "可尝试放大选区或换用 vision_oneshot 流程。", InfoSeverity.Neutral);
                return;
            }

            _lastImagePng = null;
            var panel = EnsureResultPanel(selectionOnDesktop);
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
            panel.SetStatus(ProviderErrorMapper.ToUserMessage(exception, profile.Id, profile.Model, profile.BaseUrl), isError: true);
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

    private ResultPanelWindow EnsureResultPanel(System.Drawing.Rectangle? selectionOnDesktop = null)
    {
        if (_resultPanel is not null)
        {
            if (!_resultPanel.IsVisible)
            {
                _resultPanel.Show();
            }

            if (selectionOnDesktop is { } selectedBounds)
            {
                _resultPanel.PositionNearSelection(selectedBounds);
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
        if (selectionOnDesktop is { } newPanelBounds)
        {
            panel.PositionNearSelection(newPanelBounds);
        }

        return panel;
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { } existing)
        {
            // 单例设置窗：重复触发时只把已有窗口提到前台，不再叠开第二份编辑副本。
            existing.Show();
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(_configStore.Current, ApplySettings, TestConnectionAsync, SuspendCaptureHotkey);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
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

    /// <summary>热键录制期间临时注销全局热键，避免录制正在生效的组合时反而触发截图。</summary>
    private void SuspendCaptureHotkey(bool suspend)
    {
        if (suspend)
        {
            _hotkeyWindow?.Dispose();
            _hotkeyWindow = null;
            return;
        }

        if (_hotkeyWindow is not null)
        {
            return;
        }

        try
        {
            _hotkeyWindow = new GlobalHotkeyWindow(_configStore.Current.Hotkeys.Capture, () => _ = CaptureSelectAndTranslateAsync());
        }
        catch (Exception exception)
        {
            // 恢复失败不能打断设置窗；托盘提示后仍可用托盘菜单截图。
            _trayIcon?.ShowWarning($"恢复截图热键失败：{exception.Message}");
        }
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

    private void ShowFailure(string message, Exception exception)
    {
        Console.Error.WriteLine($"{message} {exception}");
        MessageDialog.Show(_resultPanel, message, exception.Message, InfoSeverity.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CancelTranslation();
        _settingsWindow?.Close();
        _resultPanel?.Close();
        _trayIcon?.Dispose();
        _providerFactory?.Dispose();
        _ocrEngine?.Dispose();
        _hotkeyWindow?.Dispose();
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}
