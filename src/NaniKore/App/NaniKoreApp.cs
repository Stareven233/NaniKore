using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Capture;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Input;
using NaniKore.Ocr;
using NaniKore.Ocr.Models;
using NaniKore.Overlay;
using NaniKore.Phonetics;
using NaniKore.Translation;
using Application = System.Windows.Application;

namespace NaniKore.App;

/// <summary>应用宿主统一管理热键、托盘、配置、面板和可抢占的翻译流。</summary>
internal sealed class NaniKoreApp : Application
{
    private readonly ConfigStore _configStore = new(AppContext.BaseDirectory);
    private readonly IFavoriteService _favoriteService = new SqliteFavoriteService();
    private GlobalHotkeyWindow? _hotkeyWindow;
    private OcrEngine? _ocrEngine;
    private ProviderFactory? _providerFactory;
    private TrayIconHost? _trayIcon;
    private ResultPanelWindow? _resultPanel;
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _explanationCancellation;
    private TranslationSession? _currentSession;
    private byte[]? _lastImagePng;
    private bool _selecting;
    private bool _isReplacingResultPanel;
    private double? _lastPanelWidth;
    private double? _lastPanelHeight;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 主题资源必须先于任何窗口创建就位，否则首个窗口会以系统默认样式闪现一帧。
        ThemeManager.Initialize(this);
        _providerFactory = new ProviderFactory(_configStore.Current);
        _hotkeyWindow = new GlobalHotkeyWindow(_configStore.Current.Hotkeys.Capture, OnCaptureHotkeyPressed);
        _trayIcon = new TrayIconHost(() => _ = CaptureSelectAndTranslateAsync(), () => ShowSettings(), Shutdown);

        if (!string.IsNullOrWhiteSpace(_configStore.LoadWarning))
        {
            _trayIcon.ShowWarning(_configStore.LoadWarning);
        }

        Console.WriteLine("NaniKore translation pipeline is ready.");
        Console.WriteLine($"Press {_configStore.Current.Hotkeys.Capture} to capture and translate a region.");
    }

    /// <summary>截图热键先抢占旧结果，再立即进入新一轮选区翻译；正在框选时保持忽略新触发。</summary>
    private void OnCaptureHotkeyPressed()
    {
        if (_resultPanel is { IsVisible: true } panel)
        {
            // 关闭事件会同时取消仍在进行的翻译请求，避免结果面板关闭后继续更新旧内容。
            // 热键替换属于例外：Close 的 Closed 通知可能晚于新流程启动，不能误杀刚创建的翻译流。
            _isReplacingResultPanel = true;
            panel.Close();
        }

        _ = CaptureSelectAndTranslateAsync();
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
                visionPanel.Reset(string.Empty, "正在识别并翻译图像…", autoTranslate: true, autoExplain: _configStore.Current.General.AutoExplain);
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
            var autoTranslate = _configStore.Current.General.AutoTranslate;
            var panel = EnsureResultPanel(selectionOnDesktop);
            panel.Reset(result.Text, $"OCR 完成：{result.Lines.Count} 行 · {result.Elapsed.TotalMilliseconds:N0} ms", autoTranslate, autoExplain: _configStore.Current.General.AutoExplain);
            TryCopyToClipboard(result.Text);
            if (autoTranslate)
            {
                _ = StartTranslationAsync(result.Text, null);
            }
        }
        catch (DirectoryNotFoundException exception)
        {
            ShowFailure("本地 OCR 依赖缺失，请打开设置-通用-本地ocr依赖进行下载", exception);
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
        panel.BeginTranslation();
        panel.SetStatus("正在连接翻译服务…");
        panel.SetTranslationInProgress(true);
        var receivedOutput = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long? ttftMs = null;
        TokenUsage? reportedUsage = null;

        try
        {
            var config = _configStore.Current;
            var request = new UnderstandRequest(UnderstandKind.Translate, source, imagePng, config.General.TargetLanguage);
            var sysPrompt = PromptTemplates.GetSystemPrompt(request, config.Prompts);
            var session = new TranslationSession(sysPrompt, source, imagePng);
            request = request with { Session = session };
            _currentSession = session;

            var provider = (_providerFactory ?? throw new InvalidOperationException("翻译服务尚未初始化。")).GetProvider(config.Pipeline.ActiveProfile, request);
            await foreach (var delta in provider.StreamAsync(request, cancellation.Token))
            {
                if (delta.Usage.HasValue)
                {
                    reportedUsage = delta.Usage.Value;
                }

                if (!string.IsNullOrEmpty(delta.Text))
                {
                    if (!ttftMs.HasValue)
                    {
                        ttftMs = stopwatch.ElapsedMilliseconds;
                        await Dispatcher.InvokeAsync(() => panel.SetStatus($"首字响应 TTFT: {ttftMs.Value}ms · 正在流式接收…"));
                    }

                    receivedOutput = true;
                }

                await Dispatcher.InvokeAsync(() => panel.Append(delta));
            }

            stopwatch.Stop();
            var totalDurationMs = stopwatch.ElapsedMilliseconds;

            if (!cancellation.IsCancellationRequested)
            {
                // 服务正常结束但没有任何可识别文本时不能伪装成成功，便于定位 provider 响应格式问题。
                await Dispatcher.InvokeAsync(() =>
                {
                    panel.SetStatus(
                        receivedOutput ? "翻译完成。" : "翻译服务返回空内容。",
                        isError: !receivedOutput);

                    if (receivedOutput)
                    {
                        session.FinalizeStage1Output();
                        panel.SetExplanationEnabled(true);
                        // 译文落地后单次异步叠加本地注音（issue 008 Phase 3）：视觉链路的转录文本在此刻获得注音视图
                        _ = panel.AnnotateSourceAsync();
                        if (_configStore.Current.General.AutoExplain)
                        {
                            panel.ExpandExplanation();
                        }

                        // 降级兜底估算：输入包含提示词与原句/图片，输出包含注音与译文
                        panel.SetMetrics(BuildMetrics(
                            reportedUsage,
                            ttftMs,
                            totalDurationMs,
                            () => TokenEstimator.Estimate(sysPrompt + "\n" + source, imagePng is { Length: > 0 }),
                            () => TokenEstimator.Estimate(panel.SourceText + "\n" + panel.TranslationText)));
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 新选区、重译或面板关闭会主动抢占旧请求，不应显示失败提示。
        }
        catch (Exception exception)
        {
            var profile = _configStore.GetActiveProfile();
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Id, profile.Model, profile.BaseUrl);
            var details = exception is ProviderHttpException { ServerMessage: { } serverMessage }
                ? serverMessage
                : exception.Message;
            await Dispatcher.InvokeAsync(() => panel.SetError(summary, details));
        }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation))
            {
                await Dispatcher.InvokeAsync(() => panel.SetTranslationInProgress(false));
                _translationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task StartExplanationAsync()
    {
        var session = _currentSession;
        if (session is null || session.IsExplainInProgress || session.HasExplained)
        {
            return;
        }

        if (_explanationCancellation is { } prevCancel)
        {
            _explanationCancellation = null;
            prevCancel.Cancel();
        }

        var cancellation = new CancellationTokenSource();
        _explanationCancellation = cancellation;
        var panel = EnsureResultPanel();
        panel.BeginExplanation();
        panel.SetStatus("正在生成精讲与语法解析…");
        session.IsExplainInProgress = true;
        var receivedOutput = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long? ttftMs = null;
        TokenUsage? reportedUsage = null;

        try
        {
            var config = _configStore.Current;
            var explainPrompt = !string.IsNullOrWhiteSpace(config.Prompts.Explain)
                ? config.Prompts.Explain.Trim()
                : PromptTemplates.DefaultExplainUserPrompt;

            session.PrepareStage2Request(explainPrompt);

            var request = new UnderstandRequest(
                UnderstandKind.Explain,
                session.InitialSourceText,
                session.ImagePng,
                config.General.TargetLanguage,
                session);

            var provider = (_providerFactory ?? throw new InvalidOperationException("翻译服务尚未初始化。"))
                .GetProvider(config.Pipeline.ActiveProfile, request);

            var fullExplanation = new System.Text.StringBuilder();

            await foreach (var delta in provider.StreamAsync(request, cancellation.Token))
            {
                if (delta.Usage.HasValue)
                {
                    reportedUsage = delta.Usage.Value;
                }

                if (!string.IsNullOrEmpty(delta.Text))
                {
                    if (!ttftMs.HasValue)
                    {
                        ttftMs = stopwatch.ElapsedMilliseconds;
                        await Dispatcher.InvokeAsync(() => panel.SetStatus($"精讲首字响应 TTFT: {ttftMs.Value}ms · 正在流式接收…"));
                    }

                    receivedOutput = true;
                    fullExplanation.Append(delta.Text);
                }

                await Dispatcher.InvokeAsync(() => panel.Append(delta));
            }

            stopwatch.Stop();
            var totalDurationMs = stopwatch.ElapsedMilliseconds;

            if (!cancellation.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    panel.CompleteExplanation(receivedOutput);
                    panel.SetStatus(
                        receivedOutput ? "精讲解析完成。" : "精讲服务返回空内容。",
                        isError: !receivedOutput);

                    if (receivedOutput)
                    {
                        session.HasExplained = true;
                        session.Messages.Add(new ChatMessage(ChatRole.Assistant, fullExplanation.ToString()));

                        panel.SetMetrics(BuildMetrics(
                            reportedUsage,
                            ttftMs,
                            totalDurationMs,
                            () => TokenEstimator.Estimate(
                                session.SystemPrompt + "\n" + session.GetStage1Output() + "\n" + explainPrompt,
                                session.ImagePng is { Length: > 0 }),
                            () => TokenEstimator.Estimate(fullExplanation.ToString())));
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 正常取消
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => panel.CompleteExplanation(false));
            var profile = _configStore.GetActiveProfile();
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Id, profile.Model, profile.BaseUrl);
            var details = exception is ProviderHttpException { ServerMessage: { } serverMessage }
                ? serverMessage
                : exception.Message;
            await Dispatcher.InvokeAsync(() => panel.SetError(summary, details));
        }
        finally
        {
            session.IsExplainInProgress = false;
            if (ReferenceEquals(_explanationCancellation, cancellation))
            {
                _explanationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    /// <summary>
    /// 汇总一轮调用的首字延迟、总耗时与 Token 产出：服务端未上报 usage 时按估算输入/输出兜底，
    /// 估算只在确实需要时才计算。翻译与精讲两条流共用，杜绝两份各自漂移的指标公式。
    /// </summary>
    private static TranslationMetrics BuildMetrics(
        TokenUsage? reportedUsage,
        long? ttftMs,
        long totalDurationMs,
        Func<int> estimateInput,
        Func<int> estimateOutput)
    {
        var timeToFirstTokenMs = ttftMs ?? totalDurationMs;
        var usage = reportedUsage ?? new TokenUsage(estimateInput(), Math.Max(1, estimateOutput()), IsEstimated: true);
        var generationTimeMs = Math.Max(1, totalDurationMs - timeToFirstTokenMs);
        return new TranslationMetrics(
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            TimeToFirstTokenMs: timeToFirstTokenMs,
            TotalDurationMs: totalDurationMs,
            TokensPerSecond: usage.OutputTokens * 1000.0 / generationTimeMs,
            IsEstimated: usage.IsEstimated);
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

            _resultPanel.BringToTopmostBand();

            return _resultPanel;
        }

        var panel = new ResultPanelWindow();
        if (_lastPanelWidth is > 0 && _lastPanelHeight is > 0)
        {
            panel.Width = _lastPanelWidth.Value;
            panel.Height = _lastPanelHeight.Value;
        }

        panel.SizeChanged += (_, _) =>
        {
            if (panel.WindowState == WindowState.Normal)
            {
                _lastPanelWidth = panel.ActualWidth;
                _lastPanelHeight = panel.ActualHeight;
            }
        };

        panel.TranslateRequested += (_, _) => _ = StartTranslationAsync(panel.SourceText, _lastImagePng);
        panel.ExplanationRequested += (_, _) => _ = StartExplanationAsync();
        panel.SetFavoriteService(_favoriteService);
        // 收藏语言的探测兜底口径与提示词展开共用同一份目标语言配置。
        panel.SetTargetLanguage(_configStore.Current.General.TargetLanguage);
        // 原文注音开关与源语言下拉共用设置页配置（issue 008 Phase 3）。
        panel.SetPhoneticsOptions(_configStore.Current.Phonetics);
        panel.AutoExplainChanged += (_, autoExplain) =>
        {
            var config = _configStore.Current;
            if (config.General.AutoExplain != autoExplain)
            {
                config.General.AutoExplain = autoExplain;
                _configStore.AtomicWrite(config);
            }
        };
        panel.SettingsRequested += (_, _) => ShowSettings(topmostFromResult: true);
        panel.Closed += (_, _) =>
        {
            _resultPanel = null;
            if (_isReplacingResultPanel)
            {
                _isReplacingResultPanel = false;
            }
            else
            {
                CancelTranslation();
            }

            if (_settingsWindow is { } settings)
            {
                // 结果卡关闭后，其带来的置顶来源也消失；设置恢复普通层级。
                settings.Topmost = false;
            }
        };
        _resultPanel = panel;
        panel.Show();
        panel.BringToTopmostBand();
        if (selectionOnDesktop is { } newPanelBounds)
        {
            panel.PositionNearSelection(newPanelBounds);
        }

        return panel;
    }

    private void ShowSettings(bool topmostFromResult = false)
    {
        if (_settingsWindow is { } existing)
        {
            // 单例设置窗：重复触发时只把已有窗口提到前台，不再叠开第二份编辑副本。
            existing.Topmost = topmostFromResult;
            existing.Show();
            existing.Activate();
            return;
        }

        // 传入实时提供者而不是打开时的快照：设置窗未保存就关闭时的尺寸回写必须以
        // 当前最新配置为基础，否则会覆盖结果卡 AutoExplainChanged 等入口期间的写盘。
        var window = new SettingsWindow(() => _configStore.Current, ApplySettings, TestConnectionAsync, SuspendCaptureHotkey, DownloadOcrDependenciesAsync, DownloadPhoneticDictionaryAsync, _favoriteService);
        // 只有结果卡“设置”按钮打开时才置顶；托盘等入口保持普通窗口，允许被遮挡。
        window.Topmost = topmostFromResult;
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
        // 目标语言可能已变：同步刷新结果卡上的收藏语言探测口径（不打断当前展示）。
        _resultPanel?.SetTargetLanguage(next.General.TargetLanguage);
        // 注音开关可能已变：结果卡按新口径决定是否叠加注音（原文区不强刷，下一轮原文自然生效）。
        _resultPanel?.SetPhoneticsOptions(next.Phonetics);
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
            _hotkeyWindow = new GlobalHotkeyWindow(_configStore.Current.Hotkeys.Capture, OnCaptureHotkeyPressed);
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
            _hotkeyWindow = new GlobalHotkeyWindow(nextGesture, OnCaptureHotkeyPressed);
        }
        catch
        {
            _hotkeyWindow = new GlobalHotkeyWindow(previousGesture, OnCaptureHotkeyPressed);
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

    private static async Task DownloadOcrDependenciesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken) =>
        await new OcrDependencyDownloader().DownloadAsync(progress, cancellationToken);

    /// <summary>
    /// 按语言下载注音词典（issue 008 Phase 2）：入口只负责把语言码转交下载器；
    /// 标注器的不可用闩锁由全局注册表在下载成功后统一重置，实现无需重启的热生效。
    /// </summary>
    private static async Task DownloadPhoneticDictionaryAsync(
        string languageCode,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await new PhoneticDependencyDownloader().DownloadAsync(languageCode, progress, cancellationToken);
        PhoneticAnnotatorRegistryHolder.ResetAvailability(languageCode);
    }

    private OcrEngine GetOcrEngine() => _ocrEngine ??= OcrEngine.CreateDefault();

    private void CancelTranslation()
    {
        if (_translationCancellation is { } cancellation)
        {
            _translationCancellation = null;
            cancellation.Cancel();
        }

        if (_explanationCancellation is { } explainCancel)
        {
            _explanationCancellation = null;
            explainCancel.Cancel();
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
