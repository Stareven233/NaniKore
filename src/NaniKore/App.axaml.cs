using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.App.Theme;
using NaniKore.Capture;
using NaniKore.Config;
using NaniKore.Favorites;
using NaniKore.Imaging;
using NaniKore.Input;
using NaniKore.Interop;
using NaniKore.Ocr;
using NaniKore.Ocr.Models;
using NaniKore.Runtime;
using NaniKore.Overlay;
using NaniKore.Phonetics;
using NaniKore.Translation;
using SkiaSharp;

namespace NaniKore;

/// <summary>
/// Avalonia 12 应用宿主：编排全局热键、托盘入口、屏幕截取浮层、流式翻译与精讲、设置窗口与尺寸写回。
/// 常驻托盘运行，无主窗口，显式退出。彻底摆脱 WPF/WinForms 双引擎。
/// </summary>
public partial class NaniKoreApp : Application
{
    private readonly SecretStore _secretStore = new();
    private readonly IFavoriteService _favoriteService = new SqliteFavoriteService();
    private readonly IdleResource<OcrEngine> _ocrEngine = new(OcrEngine.CreateDefault);

    private ConfigStore _configStore = null!;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private GlobalHotkeyWindow? _captureHotkey;
    private GlobalHotkeyWindow? _pasteHotkey;
    private readonly HashSet<HotkeySlot> _recordingHotkeySlots = [];
    private OcrHttpServer? _ocrHttpServer;
    private ProviderFactory? _providerFactory;
    private TrayIconHost? _trayIcon;
    private ResultPanelWindow? _resultPanel;
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _explanationCancellation;
    private TranslationSession? _currentSession;
    private byte[]? _lastImagePng;
    private bool _selecting;
    private bool _acquiringPasteInput;
    private long _translationGeneration;
    private double? _lastPanelWidth;
    private double? _lastPanelHeight;

    private const int CaptureHotkeyId = 1;
    private const int PasteHotkeyId = 2;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ThemeManager.Initialize(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;
            InitializeApplication(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 数据目录（.data）不可创建或不可写时明确拒绝启动
        if (!TryEnsureDataDirectory())
        {
            Environment.ExitCode = 1;
            desktop.Shutdown(1);
            return;
        }

        _configStore = new ConfigStore(AppPaths.DataDirectory);
        ConfigureLocalResourceCache(_configStore.Current);
        // 字体族必须在任何窗口创建之前写入应用资源（issue 033）：DynamicResource 在控件构造期允许暂缺，
        // 但首帧就在此写入才能避免闪烁，且内置样式不再持有任何字体常量。
        FontResources.Apply(_configStore.Current.Fonts);
        WindowBackgroundResources.Apply(_configStore.Current.Windows);
        _providerFactory = new ProviderFactory(_configStore.Current, _secretStore);

        // 托盘宿主（点击与右键原生菜单事件联动）
        _trayIcon = new TrayIconHost(
            () => _ = CaptureSelectAndTranslateAsync(),
            () => _ = PasteTranslateAsync(),
            () => ShowSettings(),
            ExitApplication);

        if (_configStore.ReadFailed)
        {
            // 配置读取失败要显著警示并点名出错项（R09）：用对话框而非一闪而过的托盘提示。
            MessageDialog.Show(null, "配置文件读取失败", _configStore.LoadWarning, InfoSeverity.Error);
        }
        else if (!string.IsNullOrWhiteSpace(_configStore.LoadWarning))
        {
            _trayIcon.ShowWarning(_configStore.LoadWarning);
        }

        // 密钥文件无法解密（换用户/换机器/损坏）时不再静默当作未配置（R09）。
        if (_secretStore.CheckReadable())
        {
            _trayIcon.ShowWarning(_secretStore.LoadWarning ?? "密钥文件无法读取，已拒绝覆盖原始密文。");
        }

        // 读取失败时已在上面给出显著警示；不再叠加版本差提示，避免默认回退后的噪音。
        if (!_configStore.ReadFailed && IsConfigVersionBehindTemplate())
        {
            _trayIcon.ShowWarning("检测到配置版本与模板版本不一致，详见 设置-关于");
        }

        // 两个热键各自独立注册
        TryRegisterHotkey(HotkeySlot.Capture);
        TryRegisterHotkey(HotkeySlot.Paste);
        ConfigureOcrHttpServer(_configStore.Current.General.OcrListen);

        Console.WriteLine("NaniKore translation pipeline is ready.");
        Console.WriteLine($"Press {_configStore.Current.Hotkeys.Capture} to capture and translate a region.");
        if (HotkeyGesture.IsEnabled(_configStore.Current.Hotkeys.Paste))
        {
            Console.WriteLine($"Press {_configStore.Current.Hotkeys.Paste} to translate the selection or clipboard content.");
        }
    }

    private void ExitApplication()
    {
        _desktop?.Shutdown(0);
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        CancelTranslation();
        _settingsWindow?.Close();
        _resultPanel?.Close();
        _trayIcon?.Dispose();
        _ocrHttpServer?.Dispose();
        _providerFactory?.Dispose();
        _ocrEngine.Dispose();
        PhoneticAnnotatorRegistryHolder.Default.Dispose();
        _captureHotkey?.Dispose();
        _pasteHotkey?.Dispose();
        ThemeManager.Shutdown();
    }

    private bool TryEnsureDataDirectory()
    {
        try
        {
            AppPaths.EnsureWritableDataDirectory();
            return true;
        }
        catch (DataDirectoryUnavailableException exception)
        {
            MessageDialog.Show(null, "无法使用数据目录", exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    private bool IsConfigVersionBehindTemplate() => ConfigVersionComparator.IsBehind(
        ConfigStore.ReadTemplateVersion(),
        _configStore.Current.Version);

    private void OnCaptureHotkeyPressed()
    {
        _ = CaptureSelectAndTranslateAsync();
    }

    private void OnPasteHotkeyPressed()
    {
        if (_selecting)
        {
            return;
        }

        _ = PasteTranslateAsync();
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
            var imageRoute = ClipboardImageRouteResolver.Resolve(
                _configStore.Current.Pipeline.Mode,
                _configStore.GetActiveProfile().SupportsVision);
            if (imageRoute == ClipboardImageRoute.UnsupportedVision)
            {
                ShowVisionUnsupportedReminder();
                return;
            }

            using var frame = DesktopCapturer.CaptureVirtualScreen();
            var overlay = new SelectionOverlayWindow(frame.Image, frame.VirtualBounds);

            var tcs = new TaskCompletionSource<PixelRect?>();
            overlay.Closed += (_, _) => tcs.TrySetResult(overlay.SelectionResult);
            overlay.Show();
            var selection = await tcs.Task;

            if (selection is not { } sel || sel.Width < 4 || sel.Height < 4)
            {
                return;
            }

            using var cropped = CropImage(frame.Image, sel);
            var selectionOnDesktop = new PixelRect(
                frame.VirtualBounds.X + sel.X,
                frame.VirtualBounds.Y + sel.Y,
                sel.Width,
                sel.Height);

            if (imageRoute == ClipboardImageRoute.DirectFeed)
            {
                var image = PngEncoder.Encode(cropped);
                // 网络请求序列化之前归还截图像素，避免与 base64/JSON 缓冲同时驻留。
                cropped.Dispose();
                frame.Dispose();
                AcceptNewInput(image);
                var visionPanel = EnsureResultPanel(selectionOnDesktop);
                visionPanel.Reset(string.Empty, "正在识别并翻译图像…", autoTranslate: true, autoExplain: _configStore.Current.General.AutoExplain, autoAnnotate: _configStore.Current.General.AutoAnnotate);
                _ = StartTranslationAsync(string.Empty, image);
                return;
            }

            OcrResult result;
            try
            {
                result = await Task.Run(() => RecognizeWithCachedEngine(cropped));
            }
            catch (Exception exception)
            {
                ShowFailure("本地 OCR 识别失败。", exception);
                return;
            }

            if (result.Lines.Count == 0)
            {
                MessageDialog.Show(_resultPanel, "没有检测到可识别文本。", "可尝试放大选区或换用 vision_oneshot 流程。", InfoSeverity.Neutral);
                return;
            }

            AcceptNewInput(null);
            var autoTranslate = _configStore.Current.General.AutoTranslate;
            var panel = EnsureResultPanel(selectionOnDesktop);
            panel.Reset(result.Text, $"OCR 完成：{result.Lines.Count} 行 · {result.Elapsed.TotalMilliseconds:N0} ms", autoTranslate, autoExplain: _configStore.Current.General.AutoExplain, autoAnnotate: _configStore.Current.General.AutoAnnotate);
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

    private async Task PasteTranslateAsync()
    {
        if (_acquiringPasteInput || _selecting)
        {
            return;
        }

        _acquiringPasteInput = true;
        try
        {
            var config = _configStore.Current;
            var imageRoute = ClipboardImageRouteResolver.Resolve(
                config.Pipeline.Mode,
                _configStore.GetActiveProfile().SupportsVision);
            var anchor = CursorAnchor();

            var outcome = await Task.Run(() => ClipboardInputSource.Acquire(
                imageRoute,
                RecognizeWithCachedEngine));

            if (outcome.Kind == PasteInputKind.Empty)
            {
                _trayIcon?.ShowWarning(outcome.Notice ?? "没有读取到选区文本或可用的剪贴板内容。");
                return;
            }

            if (outcome.Kind == PasteInputKind.VisionUnsupported)
            {
                ShowVisionUnsupportedReminder(outcome.Notice);
                return;
            }

            var autoExplain = config.General.AutoExplain;
            if (outcome.ImagePng is { Length: > 0 } imagePng)
            {
                AcceptNewInput(imagePng);
                var visionPanel = EnsureResultPanel(anchor);
                visionPanel.Reset(
                    string.Empty,
                    outcome.Kind == PasteInputKind.SelectionImage ? "正在识别并翻译选区图像…" : "正在识别并翻译剪贴板图像…",
                    autoTranslate: true,
                    autoExplain: autoExplain,
                    autoAnnotate: config.General.AutoAnnotate);
                _ = StartTranslationAsync(string.Empty, imagePng, outcome.Notice);
                return;
            }

            AcceptNewInput(null);
            var status = outcome.Kind switch
            {
                PasteInputKind.ClipboardImage or PasteInputKind.SelectionImage => $"OCR 完成：{outcome.OcrLineCount} 行 · {outcome.OcrElapsed.TotalMilliseconds:N0} ms",
                PasteInputKind.SelectionText => "已读取选区文本。",
                PasteInputKind.ClipboardText => "已读取剪贴板文本。",
                _ => "已读取剪贴板文本。",
            };

            var autoTranslate = config.General.PasteAutoTranslate;
            var panel = EnsureResultPanel(anchor);
            panel.Reset(outcome.Text, status, autoTranslate, autoExplain: autoExplain, autoAnnotate: config.General.AutoAnnotate);
            if (autoTranslate)
            {
                _ = StartTranslationAsync(outcome.Text, null, outcome.Notice);
            }
        }
        catch (DirectoryNotFoundException exception)
        {
            ShowFailure("本地 OCR 依赖缺失，请打开设置-通用-本地ocr依赖进行下载", exception);
        }
        catch (Win32Exception exception)
        {
            ShowFailure("读取选区或剪贴板失败。", exception);
        }
        catch (ExternalException exception)
        {
            ShowFailure("图像或剪贴板操作失败。", exception);
        }
        catch (Exception exception)
        {
            ShowFailure("读取选区或剪贴板失败。", exception);
        }
        finally
        {
            _acquiringPasteInput = false;
        }
    }

    private void ShowVisionUnsupportedReminder(string? extraNotice = null)
    {
        var profile = _configStore.GetActiveProfile();
        var body = $"配置档「{profile.Name}」未开启 supports_vision，无法把图像直接交给 {profile.Model}。" +
            "请切换到支持视觉的配置档，或在 设置-翻译 把输入管线改为 local_ocr（改用本地 OCR 识别图像）。";
        if (extraNotice is { Length: > 0 } notice)
        {
            body += $"{Environment.NewLine}{Environment.NewLine}{notice}";
        }

        MessageDialog.Show(_resultPanel, "当前配置档不支持视觉输入", body, InfoSeverity.Neutral);
    }

    private static PixelRect CursorAnchor()
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            return new PixelRect(pt.X, pt.Y, 1, 1);
        }
        return new PixelRect(0, 0, 1, 1);
    }

    private static SKBitmap CropImage(SKBitmap source, PixelRect selection)
    {
        var rect = new SKRectI(selection.X, selection.Y, selection.Right, selection.Bottom);
        var subset = new SKBitmap();
        if (source.ExtractSubset(subset, rect))
        {
            return subset;
        }

        var width = Math.Max(1, selection.Width);
        var height = Math.Max(1, selection.Height);
        var copy = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(source, new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), new SKRect(0, 0, width, height));
        return copy;
    }

    private async Task StartTranslationAsync(string source, ReadOnlyMemory<byte>? imagePng, string? notice = null)
    {
        CancelTranslation();
        var generation = Volatile.Read(ref _translationGeneration);
        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        var panel = EnsureResultPanel();
        panel.BeginTranslation();
        panel.SetStatus(notice is { Length: > 0 } pendingNotice ? pendingNotice : "正在连接翻译服务…");
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
                if (cancellation.IsCancellationRequested || Volatile.Read(ref _translationGeneration) != generation)
                {
                    break;
                }

                if (delta.Usage.HasValue)
                {
                    reportedUsage = delta.Usage.Value;
                }

                if (!string.IsNullOrEmpty(delta.Text))
                {
                    if (!ttftMs.HasValue)
                    {
                        ttftMs = stopwatch.ElapsedMilliseconds;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (Volatile.Read(ref _translationGeneration) == generation)
                            {
                                panel.SetStatus($"首字响应 TTFT: {ttftMs.Value}ms · 正在流式接收…");
                            }
                        });
                    }

                    receivedOutput = true;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _translationGeneration) == generation)
                    {
                        panel.Append(delta);
                    }
                });
            }

            stopwatch.Stop();
            var totalDurationMs = stopwatch.ElapsedMilliseconds;

            if (!cancellation.IsCancellationRequested && Volatile.Read(ref _translationGeneration) == generation)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCurrentRequest(panel, generation, cancellation))
                    {
                        return;
                    }
                    panel.SetStatus(
                        receivedOutput ? "翻译完成。" : "翻译服务返回空内容。",
                        isError: !receivedOutput);

                    if (receivedOutput)
                    {
                        session.FinalizeStage1Output();
                        panel.SetExplanationEnabled(true);
                        _ = panel.AnnotateSourceAsync();
                        if (_configStore.Current.General.AutoExplain)
                        {
                            panel.ExpandExplanation();
                        }

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
        }
        catch (Exception exception)
        {
            var profile = _configStore.GetActiveProfile();
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Name, profile.Model, profile.BaseUrl);
            var details = exception is ProviderHttpException { ServerMessage: { } serverMessage }
                ? serverMessage
                : exception.Message;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrentRequest(panel, generation, cancellation))
                {
                    panel.SetError(summary, details);
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 身份检查与清理在同一次 UI 回调中完成，旧任务不能清掉新请求状态。
                if (ReferenceEquals(_translationCancellation, cancellation))
                {
                    panel.SetTranslationInProgress(false);
                    _translationCancellation = null;
                }
            });

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
        var generation = Volatile.Read(ref _translationGeneration);
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
                if (cancellation.IsCancellationRequested || Volatile.Read(ref _translationGeneration) != generation)
                {
                    break;
                }

                if (delta.Usage.HasValue)
                {
                    reportedUsage = delta.Usage.Value;
                }

                if (!string.IsNullOrEmpty(delta.Text))
                {
                    if (!ttftMs.HasValue)
                    {
                        ttftMs = stopwatch.ElapsedMilliseconds;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (Volatile.Read(ref _translationGeneration) == generation)
                            {
                                panel.SetStatus($"精讲首字响应 TTFT: {ttftMs.Value}ms · 正在流式接收…");
                            }
                        });
                    }

                    receivedOutput = true;
                    fullExplanation.Append(delta.Text);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _translationGeneration) == generation)
                    {
                        panel.Append(delta);
                    }
                });
            }

            stopwatch.Stop();
            var totalDurationMs = stopwatch.ElapsedMilliseconds;

            if (!cancellation.IsCancellationRequested && Volatile.Read(ref _translationGeneration) == generation)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCurrentRequest(panel, generation, cancellation))
                    {
                        return;
                    }
                    panel.CompleteExplanation(receivedOutput);
                    panel.SetStatus(
                        receivedOutput ? "精讲解析完成" : "精讲服务返回空内容",
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
        }
        catch (Exception exception)
        {
            var profile = _configStore.GetActiveProfile();
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Name, profile.Model, profile.BaseUrl);
            var details = exception is ProviderHttpException { ServerMessage: { } serverMessage }
                ? serverMessage
                : exception.Message;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrentRequest(panel, generation, cancellation))
                {
                    panel.CompleteExplanation(false);
                    panel.SetError(summary, details);
                }
            });
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

    private ResultPanelWindow EnsureResultPanel(PixelRect? anchorOnDesktop = null)
    {
        if (_resultPanel is not null)
        {
            if (!_resultPanel.IsVisible)
            {
                _resultPanel.Show();
            }

            if (anchorOnDesktop is { } selectedBounds)
            {
                _resultPanel.PositionNearSelection(selectedBounds);
            }

            _resultPanel.BringToTopmostBand();
            return _resultPanel;
        }

        var panel = new ResultPanelWindow();
        _lastPanelWidth ??= _configStore.Current.Windows.ResultPanelWidth;
        _lastPanelHeight ??= _configStore.Current.Windows.ResultPanelHeight;
        if (_lastPanelWidth is > 0 && _lastPanelHeight is > 0)
        {
            panel.Width = _lastPanelWidth.Value;
            panel.Height = _lastPanelHeight.Value;
        }

        panel.SizeChanged += (_, _) =>
        {
            if (panel.WindowState == WindowState.Normal)
            {
                _lastPanelWidth = panel.Bounds.Width;
                _lastPanelHeight = panel.Bounds.Height;
            }
        };

        panel.Deactivated += (_, _) => PersistResultPanelSize(panel);
        panel.TranslateRequested += (_, _) => _ = StartTranslationAsync(panel.SourceText, _lastImagePng);
        panel.ExplanationRequested += (_, _) => _ = StartExplanationAsync();
        panel.SetFavoriteService(_favoriteService);
        panel.SetTargetLanguage(_configStore.Current.General.TargetLanguage);
        panel.SetPhoneticsOptions(_configStore.Current.Phonetics);
        panel.SetPinned(_configStore.Current.Windows.ResultPanelPinned);
        panel.PinChanged += (_, pinned) =>
        {
            var config = _configStore.Current;
            if (config.Windows.ResultPanelPinned != pinned)
            {
                config.Windows.ResultPanelPinned = pinned;
                TryWriteConfig(config);
            }
        };
        panel.AutoExplainChanged += (_, autoExplain) =>
        {
            var config = _configStore.Current;
            if (config.General.AutoExplain != autoExplain)
            {
                config.General.AutoExplain = autoExplain;
                TryWriteConfig(config);
            }
        };
        panel.SettingsRequested += (_, tab) => ShowSettings(tab);
        panel.Closed += (_, _) =>
        {
            PersistResultPanelSize(panel);
            _resultPanel = null;
            CancelTranslation();
            // 关闭即结束本次图像与精讲上下文；窗口打开期间仍保留，供重译和精讲使用。
            _currentSession = null;
            _lastImagePng = null;
        };

        _resultPanel = panel;
        panel.Show();
        panel.BringToTopmostBand();
        if (anchorOnDesktop is { } newPanelBounds)
        {
            panel.PositionNearSelection(newPanelBounds);
        }

        return panel;
    }

    private void PersistResultPanelSize(ResultPanelWindow panel)
    {
        if (panel.WindowState != WindowState.Normal)
        {
            return;
        }

        var width = panel.Bounds.Width;
        var height = panel.Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        width = Math.Max(panel.MinWidth, width);
        height = Math.Max(panel.MinHeight, height);

        var windows = _configStore.Current.Windows;
        if (Math.Abs(windows.ResultPanelWidth - width) < 0.5 &&
            Math.Abs(windows.ResultPanelHeight - height) < 0.5)
        {
            return;
        }

        var next = AppConfigCloner.Clone(_configStore.Current);
        next.Windows.ResultPanelWidth = width;
        next.Windows.ResultPanelHeight = height;
        try
        {
            _configStore.AtomicWrite(next);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 后台开关写配置的守卫（R09）：读取失败锁定写盘或写盘异常时，不让事件处理崩溃，
    /// 只把可行动的中文错误推到托盘；内存开关仍即时生效供本次会话使用。
    /// </summary>
    private void TryWriteConfig(AppConfig config)
    {
        try
        {
            _configStore.AtomicWrite(config);
        }
        catch (Exception exception)
        {
            _trayIcon?.ShowWarning(exception.Message);
        }
    }

    private void ShowSettings(SettingsTab tab = SettingsTab.General)
    {
        if (_settingsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.SelectTab(tab);
            existing.Show();
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(
            () => _configStore.Current,
            ApplySettings,
            TestConnectionAsync,
            SuspendHotkey,
            DownloadOcrDependenciesAsync,
            DownloadPhoneticDictionaryAsync,
            _favoriteService,
            () => ConfigStore.ReadTemplateVersion(),
            tab);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    private void ApplySettings(AppConfig next)
    {
        var oldConfig = _configStore.Current;
        _configStore.AtomicWrite(next);
        ConfigureLocalResourceCache(next);
        // 字体热刷新：DynamicResource 自动重绘已存在的控件，无需重启。
        FontResources.Apply(next.Fonts);
        WindowBackgroundResources.Apply(next.Windows);
        _providerFactory?.Dispose();
        _providerFactory = new ProviderFactory(next, _secretStore);

        var captureChanged = !string.Equals(oldConfig.Hotkeys.Capture, next.Hotkeys.Capture, StringComparison.OrdinalIgnoreCase);
        var pasteChanged = !string.Equals(oldConfig.Hotkeys.Paste, next.Hotkeys.Paste, StringComparison.OrdinalIgnoreCase);
        // 先释放所有变更槽位，再独立注册，允许两个热键交换或移到刚腾出的组合。
        if (captureChanged)
        {
            ReleaseHotkey(HotkeySlot.Capture);
        }
        if (pasteChanged)
        {
            ReleaseHotkey(HotkeySlot.Paste);
        }

        if (captureChanged)
        {
            TryRegisterHotkey(HotkeySlot.Capture);
        }
        if (pasteChanged)
        {
            TryRegisterHotkey(HotkeySlot.Paste);
        }

        if (!string.Equals(oldConfig.General.OcrListen, next.General.OcrListen, StringComparison.OrdinalIgnoreCase))
        {
            ConfigureOcrHttpServer(next.General.OcrListen);
        }

        _resultPanel?.SetTargetLanguage(next.General.TargetLanguage);
        _resultPanel?.SetPhoneticsOptions(next.Phonetics);
    }

    private string CurrentGesture(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Paste => _configStore.Current.Hotkeys.Paste,
        _ => _configStore.Current.Hotkeys.Capture,
    };

    private GlobalHotkeyWindow? HotkeyWindow(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? _pasteHotkey : _captureHotkey;

    private void SetHotkeyWindow(HotkeySlot slot, GlobalHotkeyWindow? window)
    {
        if (slot == HotkeySlot.Paste)
        {
            _pasteHotkey = window;
        }
        else
        {
            _captureHotkey = window;
        }
    }

    private Action HotkeyHandler(HotkeySlot slot) => () =>
    {
        // 关键调度：全局热键底层消息泵在后台工作线程触发，必须调度到 Avalonia UI 线程执行窗口创建与交互
        Dispatcher.UIThread.Post(() =>
        {
            // 注销前已排队的热键消息也不能打断录制。
            if (_recordingHotkeySlots.Count > 0)
            {
                return;
            }

            if (slot == HotkeySlot.Paste)
            {
                OnPasteHotkeyPressed();
            }
            else
            {
                OnCaptureHotkeyPressed();
            }
        });
    };

    private static int HotkeyId(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? PasteHotkeyId : CaptureHotkeyId;

    private static string DescribeHotkey(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? "选取翻译" : "截图翻译";

    private void RegisterHotkey(HotkeySlot slot)
    {
        var gesture = CurrentGesture(slot);
        ReleaseHotkey(slot);
        if (_recordingHotkeySlots.Count > 0 || !HotkeyGesture.IsEnabled(gesture))
        {
            return;
        }

        SetHotkeyWindow(slot, new GlobalHotkeyWindow(gesture, HotkeyHandler(slot), HotkeyId(slot)));
    }

    private void ReleaseHotkey(HotkeySlot slot)
    {
        HotkeyWindow(slot)?.Dispose();
        SetHotkeyWindow(slot, null);
    }

    private void TryRegisterHotkey(HotkeySlot slot)
    {
        try
        {
            RegisterHotkey(slot);
        }
        catch (Exception exception)
        {
            _trayIcon?.ShowWarning($"注册{DescribeHotkey(slot)}热键失败：{exception.Message}");
        }
    }

    private void SuspendHotkey(HotkeyRecordingState state)
    {
        if (state.Recording)
        {
            _recordingHotkeySlots.Add(state.Slot);
            // 任一录制器活动时都释放两个槽位，避免另一槽位吞掉待录制的组合。
            ReleaseHotkey(HotkeySlot.Capture);
            ReleaseHotkey(HotkeySlot.Paste);
            return;
        }

        if (!_recordingHotkeySlots.Remove(state.Slot) || _recordingHotkeySlots.Count > 0)
        {
            return;
        }

        // 最后一个录制器结束后只恢复已提交配置；每个槽位独立报错，不回退旧绑定。
        foreach (var slot in new[] { HotkeySlot.Capture, HotkeySlot.Paste })
        {
            try
            {
                RegisterHotkey(slot);
            }
            catch (Exception exception)
            {
                _trayIcon?.ShowWarning($"恢复{DescribeHotkey(slot)}热键失败：{exception.Message}");
            }
        }
    }

    private async Task<string> TestConnectionAsync(ProviderProfile profile, string? pendingApiKey)
    {
        try
        {
            var apiKey = string.IsNullOrWhiteSpace(pendingApiKey) ? _secretStore.Load(profile.Name) : pendingApiKey;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            await ProviderFactory.TestConnectionAsync(profile, apiKey ?? string.Empty, cancellation.Token);
            return "连接成功：服务已返回流式内容。";
        }
        catch (Exception exception)
        {
            return ProviderErrorMapper.ToUserMessage(exception, profile.Name, profile.Model, profile.BaseUrl);
        }
    }

    private static async Task DownloadOcrDependenciesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken) =>
        await new OcrDependencyDownloader().DownloadAsync(progress, cancellationToken);

    private static async Task DownloadPhoneticDictionaryAsync(
        string languageCode,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await new PhoneticDependencyDownloader().DownloadAsync(languageCode, progress, cancellationToken);
        PhoneticAnnotatorRegistryHolder.ResetAvailability(languageCode);
    }

    private OcrResult RecognizeWithCachedEngine(SKBitmap bitmap) =>
        _ocrEngine.Use(engine => engine.Recognize(bitmap));

    private void ConfigureLocalResourceCache(AppConfig config)
    {
        // ConfigStore 已校验配置；零时长保留资源，其他值按资源各自最后使用结束时间回收。
        if (!ResourceIdleDuration.TryParse(config.General.LocalResourceIdleTimeout, out var timeout))
        {
            throw new InvalidOperationException("本地资源空闲时长尚未通过配置校验。");
        }
        _ocrEngine.Configure(timeout);
        PhoneticAnnotatorRegistryHolder.Default.ConfigureIdleTimeout(timeout);
    }

    private void AcceptNewInput(byte[]? imagePng)
    {
        // 即使本次关闭自动翻译，也必须让旧流失效，防止旧会话回写或继续保留上一张图。
        CancelTranslation();
        _currentSession = null;
        _lastImagePng = imagePng;
    }

    private bool IsCurrentRequest(ResultPanelWindow panel, long generation, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested &&
        Volatile.Read(ref _translationGeneration) == generation && ReferenceEquals(_resultPanel, panel);

    private void CancelTranslation()
    {
        _ = Interlocked.Increment(ref _translationGeneration);
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

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            new Win32ClipboardGateway().SetText(text);
        }
        catch
        {
        }
    }

    private void ConfigureOcrHttpServer(string? listenAddress)
    {
        _ocrHttpServer?.Dispose();
        _ocrHttpServer = null;

        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            return;
        }

        try
        {
            _ocrHttpServer = new OcrHttpServer(listenAddress, RecognizeWithCachedEngine);
            Console.WriteLine($"OCR HTTP service listening on {_ocrHttpServer.EndpointDescription}");
        }
        catch (Exception ex)
        {
            _trayIcon?.ShowWarning($"OCR HTTP 服务启动失败：{ex.Message}");
            Console.Error.WriteLine($"[NaniKore] Failed to start OCR HTTP server on {listenAddress}: {ex.Message}");
        }
    }

    private void ShowFailure(string message, Exception exception)
    {
        Console.Error.WriteLine($"{message} {exception}");
        MessageDialog.Show(_resultPanel, message, exception.Message, InfoSeverity.Error);
    }
}
