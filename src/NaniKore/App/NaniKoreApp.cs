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
using NaniKore.Imaging;
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
    /// <summary>密钥单文件（.data/keys.bin）与应用同生命周期；目录不可写时启动已被拒绝，不会走到这里。</summary>
    private readonly SecretStore _secretStore = new();
    private readonly IFavoriteService _favoriteService = new SqliteFavoriteService();
    /// <summary>本地 OCR 引擎惰性创建的互斥闩：截图链路与剪贴板图像链路可能并发触发识别。</summary>
    private readonly object _ocrEngineGate = new();
    private ConfigStore _configStore = null!;
    private GlobalHotkeyWindow? _captureHotkey;
    private GlobalHotkeyWindow? _pasteHotkey;
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
    private bool _acquiringPasteInput;
    /// <summary>
    /// 翻译/精讲流的代际号：每放弃一轮（抢占、关闭面板）就递增。旧流在派发回调和
    /// 完成块里比对代数，代数不符即静默舍弃——覆盖「Reset 早于 CancelTranslation」
    /// 调用顺序下，已过取消检查却尚未派发的缓冲片段，杜绝其混进复用面板的新内容。
    /// </summary>
    private long _translationGeneration;
    private double? _lastPanelWidth;
    private double? _lastPanelHeight;

    /// <summary>WM_HOTKEY 的实例 id：同进程两个全局热键必须用不同 id，否则系统回调无法区分来源。</summary>
    private const int CaptureHotkeyId = 1;
    private const int PasteHotkeyId = 2;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 主题资源必须先于任何窗口创建就位，否则首个窗口会以系统默认样式闪现一帧
        // （拒绝启动的报错对话框同样要走主题）。
        ThemeManager.Initialize(this);
        // 显式关闭语义必须先设好：拒绝启动路径下关闭对话框不能顺带结束应用，
        // 否则拿不到退出码 1（OnLastWindowClose 会在对话框关闭时直接收摊）。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 数据目录（.data）不可创建或不可写时明确拒绝启动：宁可失败，也不要两份真相（issue 017 决策 8）。
        if (!TryEnsureDataDirectory())
        {
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        _configStore = new ConfigStore(AppPaths.DataDirectory);
        _providerFactory = new ProviderFactory(_configStore.Current, _secretStore);
        // 托盘必须先于热键注册就位：任一热键注册失败都要靠托盘气泡降级提示，不能改成弹窗或静默。
        _trayIcon = new TrayIconHost(
            () => _ = CaptureSelectAndTranslateAsync(),
            () => _ = PasteTranslateAsync(),
            () => ShowSettings(),
            Shutdown);

        if (!string.IsNullOrWhiteSpace(_configStore.LoadWarning))
        {
            _trayIcon.ShowWarning(_configStore.LoadWarning);
        }

        // 版本差只提示不动作（issue 017 C 期）：托盘气泡一次，设置-关于页给常驻一行。
        if (IsConfigVersionBehindTemplate())
        {
            _trayIcon.ShowWarning("检测到配置版本与模板版本不一致，详见 设置-关于");
        }

        // 两个热键各自独立注册：一个失败只气泡提示，另一个与托盘菜单照常可用。
        TryRegisterHotkey(HotkeySlot.Capture);
        TryRegisterHotkey(HotkeySlot.Paste);

        Console.WriteLine("NaniKore translation pipeline is ready.");
        Console.WriteLine($"Press {_configStore.Current.Hotkeys.Capture} to capture and translate a region.");
        if (HotkeyGesture.IsEnabled(_configStore.Current.Hotkeys.Paste))
        {
            Console.WriteLine($"Press {_configStore.Current.Hotkeys.Paste} to translate the selection or clipboard content.");
        }
    }

    /// <summary>可写性前置检查失败时弹错并回报 false；调用方据此拒绝启动。</summary>
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

    /// <summary>模板与配置的 major/minor 不一致即视为配置落后；任一侧取不到版本号则不判定。</summary>
    private bool IsConfigVersionBehindTemplate() => ConfigVersionComparator.IsBehind(
        ConfigStore.ReadTemplateVersion(),
        _configStore.Current.Version);

    /// <summary>截图热键：正在框选时保持忽略新触发；旧结果面板直接 Reset 复用，不关闭。</summary>
    private void OnCaptureHotkeyPressed()
    {
        _ = CaptureSelectAndTranslateAsync();
    }

    /// <summary>选取/剪贴板翻译热键：输入获取期间（含 600ms 轮询窗口）连按只跑一轮；框选模态期间直接丢弃。</summary>
    private void OnPasteHotkeyPressed()
    {
        if (_selecting)
        {
            // 框选浮层是模态的：此刻按 paste 说明用户仍在截图表链路里，本次触发丢弃。
            return;
        }

        _ = PasteTranslateAsync();
    }

    /// <summary>
    /// 选取/剪贴板翻译（issue 018）：输入获取与截图链路完全同构——同一个 provider、同一套提示词、
    /// 同一个单实例结果面板。区别只在输入来源与锚点：没有选区矩形，面板以当前鼠标光标为锚点。
    /// 文本路径遵循 <c>general.paste_auto_translate</c>（默认开）：关闭时只呈现原文（含注音），由「翻译」按钮显式触发；视觉直喂不受此开关约束，恒定一次完成识别与翻译（与截图链路的 <c>auto_translate</c> 同口径）。<c>auto_explain</c> 跟随配置。
    /// </summary>
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
            // 图像去向按「管线偏好优先、能力不足明确停下」解析（issue 019 裁决）：与截图链路同一把尺。
            var imageRoute = ClipboardImageRouteResolver.Resolve(
                config.Pipeline.Mode,
                _configStore.GetActiveProfile().SupportsVision);
            // 锚点在发起输入获取的瞬间就定下：输入获取最长要等 600ms 轮询预算，
            // 面板位置应当反映「按热键时鼠标在哪」，而不是等待结束时的落点。
            var anchor = CursorAnchor();
            // 剪贴板读写与轮询全部落在专用 STA 线程；OCR 也在该线程跑完，面板更新回到主线程。
            var outcome = await Task.Run(() => ClipboardInputSource.Acquire(
                imageRoute,
                bitmap => GetOcrEngine().Recognize(bitmap)));

            if (outcome.Kind == PasteInputKind.Empty)
            {
                // 选区、剪贴板文本、剪贴板图像都没有：只提示，不弹出空面板。
                _trayIcon?.ShowWarning(outcome.Notice ?? "没有读取到选区文本或可用的剪贴板内容。");
                return;
            }

            if (outcome.Kind == PasteInputKind.VisionUnsupported)
            {
                // 能力不符时明确停下并说清两条出路，绝不静默改道成 OCR（issue 019 裁决）。
                ShowVisionUnsupportedReminder(outcome.Notice);
                return;
            }

            var autoExplain = config.General.AutoExplain;
            if (outcome.ImagePng is { Length: > 0 } imagePng)
            {
                // 视觉直喂：原文区留空，PNG 原图进 provider；剪贴板保持不动。
                // 恒定自动翻译（autoTranslate: true）：vision_oneshot 将识别与翻译合成一次调用，没有「只呈现原文」的中间态，
                // 与截图链路视觉直喂同口径，不受 paste_auto_translate 开关约束。
                _lastImagePng = imagePng;
                var visionPanel = EnsureResultPanel(anchor);
                visionPanel.Reset(
                    string.Empty,
                    outcome.Kind == PasteInputKind.SelectionImage ? "正在识别并翻译选区图像…" : "正在识别并翻译剪贴板图像…",
                    autoTranslate: true,
                    autoExplain: autoExplain);
                _ = StartTranslationAsync(string.Empty, imagePng, outcome.Notice);
                return;
            }

            // 文本路径（选区文本 / 剪贴板文本 / 图像 OCR）：原文已落地，面板「翻译」按钮可用。
            _lastImagePng = null;
            var status = outcome.Kind switch
            {
                PasteInputKind.ClipboardImage or PasteInputKind.SelectionImage => $"OCR 完成：{outcome.OcrLineCount} 行 · {outcome.OcrElapsed.TotalMilliseconds:N0} ms",
                PasteInputKind.SelectionText => "已读取选区文本。",
                PasteInputKind.ClipboardText => "已读取剪贴板文本。",
                _ => "已读取剪贴板文本。",
            };
            // 选取翻译的自动翻译开关（默认开）：关闭时只呈现原文（含注音），由面板「翻译」按钮显式触发，
            // 与截图链路 local_ocr 文本路径的 auto_translate 完全同口径。
            var autoTranslate = config.General.PasteAutoTranslate;
            var panel = EnsureResultPanel(anchor);
            panel.Reset(outcome.Text, status, autoTranslate, autoExplain: autoExplain);
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

    /// <summary>
    /// 视觉一条龙遇不支持视觉的配置档的统一提醒（issue 019 裁决）：说清现状与两条出路，
    /// 截图与选取两把热键共用同一句人话，绝不静默改道。<paramref name="extraNotice"/>
    /// 承载选取链路可能叠加的剪贴板还原失败异状（原图可能没保住，必须一并告知）。
    /// </summary>
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

    /// <summary>无选区路径的锚点：以当前鼠标光标位置为准，交由面板沿用「下方优先、不够翻上方」策略。</summary>
    private static System.Drawing.Rectangle CursorAnchor()
    {
        var position = WinForms.Cursor.Position;
        return new System.Drawing.Rectangle(position.X, position.Y, 1, 1);
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
            // 图像去向按「管线优先 + 能力不符明确停下」解析（issue 019 裁决）：与选取链路同一把尺。
            var imageRoute = ClipboardImageRouteResolver.Resolve(
                _configStore.Current.Pipeline.Mode,
                _configStore.GetActiveProfile().SupportsVision);
            if (imageRoute == ClipboardImageRoute.UnsupportedVision)
            {
                // 视觉一条龙遇不吃视觉的配置档：在动手截图之前就说清，不让主人白选一场。
                ShowVisionUnsupportedReminder();
                return;
            }

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
            if (imageRoute == ClipboardImageRoute.DirectFeed)
            {
                var image = PngEncoder.Encode(cropped);
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

    private async Task StartTranslationAsync(string source, ReadOnlyMemory<byte>? imagePng, string? notice = null)
    {
        CancelTranslation();
        // 代际在取消之后捕获：此后任何新一轮都会推高代数，使本流的迟到派发自行作废。
        var generation = Volatile.Read(ref _translationGeneration);
        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        var panel = EnsureResultPanel();
        panel.BeginTranslation();
        // 剪贴板还原失败等异状在状态栏告知：与连接提示二选一，直到首个片段到达才被替换。
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
                // 面板被新一轮翻译复用（代际推高）或本轮已取消：取消前已缓冲的片段绝不插进新内容的开头。
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
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (Volatile.Read(ref _translationGeneration) == generation)
                            {
                                panel.SetStatus($"首字响应 TTFT: {ttftMs.Value}ms · 正在流式接收…");
                            }
                        });
                    }

                    receivedOutput = true;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    // 迟到派发自我否决：面板此刻展示的可能是新一轮内容，旧片段写进去就是污染。
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
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Name, profile.Model, profile.BaseUrl);
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
        // 与翻译流同一把代际尺：新一轮翻译开启时，旧精讲流的迟到派发同样自行作废。
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
                    // 同上：复用面板开启新一轮时，旧精讲流的缓冲片段不能混进新内容。
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
                        await Dispatcher.InvokeAsync(() =>
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

                await Dispatcher.InvokeAsync(() =>
                {
                    // 迟到派发自我否决：面板此刻展示的可能是新一轮内容。
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
            var summary = ProviderErrorMapper.ToUserMessage(exception, profile.Name, profile.Model, profile.BaseUrl);
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

    private ResultPanelWindow EnsureResultPanel(System.Drawing.Rectangle? anchorOnDesktop = null)
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
        panel.SettingsRequested += (_, tab) => ShowSettings(tab);
        panel.Closed += (_, _) =>
        {
            _resultPanel = null;
            // 关闭即放弃本轮结果：翻译与精讲两条流都要取消，避免面板关闭后继续更新旧内容。
            CancelTranslation();
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

    private void ShowSettings(SettingsTab tab = SettingsTab.General)
    {
        if (_settingsWindow is { } existing)
        {
            // 单例设置窗：若已最小化先恢复正常窗口状态，再提到前台激活，不重复开窗。
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.SelectTab(tab);
            existing.Show();
            existing.Activate();
            return;
        }

        // 传入实时提供者而不是打开时的快照：设置窗未保存就关闭时的尺寸回写必须以
        // 当前最新配置为基础，否则会覆盖结果卡 AutoExplainChanged 等入口期间的写盘。
        var window = new SettingsWindow(
            () => _configStore.Current,
            ApplySettings,
            TestConnectionAsync,
            SuspendHotkey,
            DownloadOcrDependenciesAsync,
            DownloadPhoneticDictionaryAsync,
            _favoriteService,
            // 「关于」页版本号的 A 段取程序目录模板版本（最新）；缺注入时只显示配置版本（自测/预览走这条）。
            () => ConfigStore.ReadTemplateVersion(),
            tab);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    private void ApplySettings(AppConfig next)
    {
        var oldConfig = _configStore.Current;
        // 业务配置先落盘，再按新手势重注册：注册读的是 Current，反过来的话注册到的是旧组合。
        _configStore.AtomicWrite(next);
        _providerFactory?.Dispose();
        _providerFactory = new ProviderFactory(next, _secretStore);

        // 两个热键各自比对、各自重注册：单个失败只气泡提示，不回退旧键、不 rethrow，其余设置照常生效。
        if (!string.Equals(oldConfig.Hotkeys.Capture, next.Hotkeys.Capture, StringComparison.OrdinalIgnoreCase))
        {
            TryRegisterHotkey(HotkeySlot.Capture);
        }

        if (!string.Equals(oldConfig.Hotkeys.Paste, next.Hotkeys.Paste, StringComparison.OrdinalIgnoreCase))
        {
            TryRegisterHotkey(HotkeySlot.Paste);
        }

        // 目标语言可能已变：同步刷新结果卡上的收藏语言探测口径（不打断当前展示）。
        _resultPanel?.SetTargetLanguage(next.General.TargetLanguage);
        // 注音开关可能已变：结果卡按新口径决定是否叠加注音（原文区不强刷，下一轮原文自然生效）。
        _resultPanel?.SetPhoneticsOptions(next.Phonetics);
    }

    /// <summary>按槽位取当前配置里的手势文本。</summary>
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

    private Action HotkeyHandler(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? OnPasteHotkeyPressed : OnCaptureHotkeyPressed;

    private static int HotkeyId(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? PasteHotkeyId : CaptureHotkeyId;

    /// <summary>报错与气泡里区分两个热键的展示名。</summary>
    private static string DescribeHotkey(HotkeySlot slot) =>
        slot == HotkeySlot.Paste ? "选取翻译" : "截图翻译";

    /// <summary>注册单个热键；空串＝关闭该热键（顺手摘掉旧注册，不留下幽灵热键）。</summary>
    private void RegisterHotkey(HotkeySlot slot)
    {
        var gesture = CurrentGesture(slot);
        ReleaseHotkey(slot);
        if (!HotkeyGesture.IsEnabled(gesture))
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

    /// <summary>启动与设置保存路径：单个热键注册失败只降级为托盘气泡，互不牵连。</summary>
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

    /// <summary>热键录制期间临时注销「正在录制的那个」实例，避免录制正在生效的组合时反而触发翻译。</summary>
    private void SuspendHotkey(HotkeyRecordingState state)
    {
        if (state.Recording)
        {
            ReleaseHotkey(state.Slot);
            return;
        }

        if (HotkeyWindow(state.Slot) is not null)
        {
            return;
        }

        try
        {
            RegisterHotkey(state.Slot);
        }
        catch (Exception exception)
        {
            // 恢复失败不能打断设置窗；托盘提示后仍可用托盘菜单触发同一条链路。
            _trayIcon?.ShowWarning($"恢复{DescribeHotkey(state.Slot)}热键失败：{exception.Message}");
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

    private OcrEngine GetOcrEngine()
    {
        // 截图链路与剪贴板图像链路可能并发识别：惰性创建走互斥闩，绝不重复建会话。
        lock (_ocrEngineGate)
        {
            return _ocrEngine ??= OcrEngine.CreateDefault();
        }
    }

    private void CancelTranslation()
    {
        // 先推代际再取消：旧流可能还有已读出的片段排在派发队列里，代际不符即自行作废。
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
        _captureHotkey?.Dispose();
        _pasteHotkey?.Dispose();
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}
