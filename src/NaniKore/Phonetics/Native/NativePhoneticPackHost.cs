using NaniKore.Runtime;
using NaniKore.Translation;

namespace NaniKore.Phonetics;

/// <summary>
/// 每种语言的原生包状态（设置页卡片与软降级口径共用）。
/// <see cref="Downloading"/>、<see cref="Failed"/> 与 <see cref="RestartPending"/> 只在内存里存在，
/// 进程重启后重新由磁盘判定——不留任何「上次下载到一半」的持久状态。
/// </summary>
internal enum NativePhoneticPackState
{
    /// <summary>该语言没有原生包（中文走拼音数据文件）。</summary>
    Unsupported,

    /// <summary>包目录或文件缺失，需要首次下载。</summary>
    Missing,

    /// <summary>文件在但哈希不符（旧版本或损坏），需要更新。</summary>
    NeedsUpdate,

    /// <summary>正在下载。</summary>
    Downloading,

    /// <summary>校验或加载失败（哈希、架构、清单、ABI、缺导出、DLL 无法加载）。</summary>
    Failed,

    /// <summary>全部文件校验通过，可直接使用。</summary>
    Ready,

    /// <summary>新版本已下载到暂存目录，因旧 DLL 正在使用，需重启后替换生效。</summary>
    RestartPending,
}

/// <summary>状态 → 界面文案：设置页按钮与自测共用同一映射，避免两处各写一份 switch。</summary>
internal static class NativePhoneticPackStateText
{
    /// <summary>卡片按钮文案。</summary>
    public static string ButtonLabel(NativePhoneticPackState state) => state switch
    {
        NativePhoneticPackState.Ready => "已就绪",
        NativePhoneticPackState.Missing => "下载",
        NativePhoneticPackState.NeedsUpdate => "更新",
        NativePhoneticPackState.Downloading => "下载中…",
        NativePhoneticPackState.Failed => "重试",
        NativePhoneticPackState.RestartPending => "重启生效",
        _ => "不可用",
    };

    /// <summary>行内说明文案。</summary>
    public static string Description(NativePhoneticPackState state) => state switch
    {
        NativePhoneticPackState.Ready => "语言包已就绪，注音即时可用。",
        NativePhoneticPackState.Missing => "尚未安装，点击下载后即可使用。",
        NativePhoneticPackState.NeedsUpdate => "本地文件版本不符，点击更新后即可使用。",
        NativePhoneticPackState.Downloading => "正在下载，请稍候。",
        NativePhoneticPackState.Failed => "加载失败，当前只显示纯文本。",
        NativePhoneticPackState.RestartPending => "新版本已下载，重启程序后生效（不会覆盖使用中的 DLL）。",
        _ => "该语言没有可用的原生注音包。",
    };
}

/// <summary>
/// 单语言槽位：惰性资源（<see cref="IdleResource{T}"/>）承载一个原生标注器，并持有该语言的包状态。
/// 状态的唯一真相来自磁盘校验（文件哈希 + pack.json + PE 架构）与真实加载结果——两者不一致时以加载结果为准，
/// 所以「界面说就绪、实际却加载失败」这种自相矛盾不会出现。
/// </summary>
internal sealed class NativePhoneticPackSlot : ITextAnnotator, IIdleTextAnnotator, IDisposable
{
    private readonly IdleResource<NativeTextAnnotator> _resource;
    private readonly Func<string, string> _packDirectory;
    private readonly NativePhoneticPackInstaller _installer;
    private readonly Lock _gate = new();
    private NativePhoneticPackState? _state;
    private string? _detail;
    private bool _disposed;

    public NativePhoneticPackSlot(
        NativePhoneticPack pack,
        TimeSpan idleTimeout,
        TimeProvider? clock = null,
        Func<string, string>? packDirectory = null,
        NativePhoneticPackInstaller? installer = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        _packDirectory = packDirectory ?? NativePhoneticManifest.GetPackDirectory;
        _installer = installer ?? NativePhoneticPackInstaller.Default;
        _resource = new IdleResource<NativeTextAnnotator>(() => new NativeTextAnnotator(pack.Language, _packDirectory), clock);
        _resource.Configure(idleTimeout);
    }

    public NativePhoneticPack Pack { get; }

    public string LanguageCode => Pack.Language;

    /// <summary>当前状态；首次询问时才做磁盘校验，构造时不碰磁盘。</summary>
    public NativePhoneticPackState State
    {
        get
        {
            lock (_gate)
            {
                return EnsureStateLocked();
            }
        }
    }

    /// <summary>失败或下载进度的说明；正常状态下为 null。</summary>
    public string? Detail
    {
        get
        {
            lock (_gate)
            {
                _ = EnsureStateLocked();
                return _detail;
            }
        }
    }

    public bool IsAvailable => SyncAfterUse(static annotator => annotator.IsAvailable, static annotator => annotator.Failure, fallback: false);

    public int CountWords(string plainText) =>
        SyncAfterUse(annotator => annotator.CountWords(plainText), static annotator => annotator.Failure, fallback: 0);

    public IReadOnlyList<AnnotationSpan> Annotate(string plainText, AnnotationContext context) =>
        SyncAfterUse(annotator => annotator.Annotate(plainText, context), static annotator => annotator.Failure, fallback: []);

    /// <summary>下载完成后清掉旧实例与旧状态：下一次访问重新校验、重建（热生效）。</summary>
    public void ResetAvailability()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _resource.Reset();
            _state = null;
            _detail = null;
        }
    }

    public void ConfigureIdleTimeout(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _resource.Configure(timeout);
        }
    }

    /// <summary>闲置回收：只释放词表/模型引用（DLL 常驻），返回是否真的释放了。</summary>
    internal bool ReleaseIfIdle() => _resource.ReleaseIfIdle();

    internal void MarkDownloading(string message)
    {
        lock (_gate)
        {
            _state = NativePhoneticPackState.Downloading;
            _detail = message;
        }
    }

    internal void MarkDownloadFailed(string message)
    {
        lock (_gate)
        {
            _state = NativePhoneticPackState.Failed;
            _detail = message;
        }
    }

    internal void MarkRestartPending(string message)
    {
        lock (_gate)
        {
            _state = NativePhoneticPackState.RestartPending;
            _detail = message;
        }
    }

    /// <summary>强制按磁盘重新判定（下载落地、暂存包提升、自测注入后调用）。</summary>
    internal void InvalidateState()
    {
        lock (_gate)
        {
            _state = null;
            _detail = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _resource.Dispose();
        }
    }

    /// <summary>
    /// 一次「使用 + 状态同步」：所有注音访问都走这里，加载失败的原因由此回流到设置页状态。
    /// </summary>
    private T SyncAfterUse<T>(Func<NativeTextAnnotator, T> operation, Func<NativeTextAnnotator, NativePhoneticPackageFailure> failure, T fallback)
    {
        NativeTextAnnotator? annotator = null;
        T result;
        try
        {
            result = _resource.Use(instance =>
            {
                annotator = instance;
                return operation(instance);
            });
        }
        catch (ObjectDisposedException)
        {
            return fallback;
        }
        catch (InvalidOperationException)
        {
            // 原生边界之外不该出现异常（DLL 内部一律转错误码）；真出现了也退化成纯文本，不打断主流程。
            return fallback;
        }

        if (annotator is null)
        {
            return result;
        }

        var code = failure(annotator);
        lock (_gate)
        {
            if (code != NativePhoneticPackageFailure.None)
            {
                _state = NativePhoneticPackState.Failed;
                _detail = NativePhoneticPackageFailureText.Describe(code, Pack, annotator.FailureDetail);
            }
            else if (_state is NativePhoneticPackState.Failed)
            {
                // 曾经加载失败、现在成功了（例如用户补下载完成）：回到磁盘判定。
                _state = null;
                _detail = null;
            }
        }

        return result;
    }

    /// <summary>磁盘判定（调用方已持有 <see cref="_gate"/>）。</summary>
    private NativePhoneticPackState EnsureStateLocked()
    {
        if (_state is not null)
        {
            return _state.Value;
        }

        var failure = NativePhoneticPackVerifier.Verify(Pack, _packDirectory(Pack.Language), out var detail);
        switch (failure)
        {
            case NativePhoneticPackageFailure.None:
                _state = NativePhoneticPackState.Ready;
                _detail = null;
                break;
            case NativePhoneticPackageFailure.Missing:
                _state = NativePhoneticPackState.Missing;
                _detail = null;
                break;
            case NativePhoneticPackageFailure.HashMismatch:
                _state = NativePhoneticPackState.NeedsUpdate;
                _detail = detail;
                break;
            default:
                _state = NativePhoneticPackState.Failed;
                _detail = NativePhoneticPackageFailureText.Describe(failure, Pack, detail);
                break;
        }

        // 暂存目录里有已下载的新版本且校验通过时，明确告知「重启生效」而不是「已就绪」。
        if (_state == NativePhoneticPackState.Ready &&
            _installer.HasStagedPack(Pack) &&
            _installer.VerifyStaged(Pack) == NativePhoneticPackageFailure.None)
        {
            _state = NativePhoneticPackState.RestartPending;
            _detail = NativePhoneticPackStateText.Description(NativePhoneticPackState.RestartPending);
        }

        return _state.Value;
    }
}

/// <summary>
/// 原生注音语言包的宿主：按语种惰性加载、状态机、闲置释放与「重启生效」的暂存更新。
/// 结果面板与设置页共用进程单例（<see cref="NativePhoneticPackHostHolder"/>），
/// 使下载完成与状态展示永远指向同一份状态。
/// </summary>
internal sealed class NativePhoneticPackHost : IDisposable
{
    private readonly Dictionary<string, NativePhoneticPackSlot> _slots;
    private readonly NativePhoneticPackInstaller _installer;
    private readonly Lock _gate = new();
    private TimeSpan _idleTimeout;
    private bool _disposed;

    public NativePhoneticPackHost(
        TimeSpan idleTimeout = default,
        TimeProvider? clock = null,
        NativePhoneticPackInstaller? installer = null,
        Func<string, string>? packDirectory = null)
    {
        _idleTimeout = idleTimeout;
        _installer = installer ?? NativePhoneticPackInstaller.Default;
        _slots = [];
        foreach (var pack in NativePhoneticManifest.Packs)
        {
            _slots[pack.Language] = new NativePhoneticPackSlot(pack, idleTimeout, clock, packDirectory ?? NativePhoneticManifest.GetPackDirectory, _installer);
        }
    }

    /// <summary>按语言取标注器；该语言没有原生包时返回 null。</summary>
    public ITextAnnotator? GetAnnotator(string? languageCode) =>
        NormalizeLanguageCode(languageCode) is { } code && _slots.TryGetValue(code, out var slot) ? slot : null;

    public NativePhoneticPackState GetState(string? languageCode) =>
        NormalizeLanguageCode(languageCode) is { } code && _slots.TryGetValue(code, out var slot)
            ? slot.State
            : NativePhoneticPackState.Unsupported;

    /// <summary>失败/进度说明；正常状态返回 null。</summary>
    public string? GetDetail(string? languageCode) =>
        NormalizeLanguageCode(languageCode) is { } code && _slots.TryGetValue(code, out var slot) ? slot.Detail : null;

    /// <summary>全部已装配语言的状态快照（设置页刷新用）。</summary>
    public IReadOnlyDictionary<string, NativePhoneticPackState> GetStates()
    {
        var states = new Dictionary<string, NativePhoneticPackState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, slot) in _slots)
        {
            states[code] = slot.State;
        }

        return states;
    }

    /// <summary>与 OCR/词典共用同一闲置策略：只改策略，不等待正在进行的注音。</summary>
    public void ConfigureIdleTimeout(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _idleTimeout = timeout;
        }

        foreach (var slot in _slots.Values)
        {
            slot.ConfigureIdleTimeout(timeout);
        }
    }

    /// <summary>闲置回收一次（自测与设置页保存后调用）；返回实际释放的语言数。</summary>
    internal int ReleaseIdleResources()
    {
        var released = 0;
        foreach (var slot in _slots.Values)
        {
            if (slot.ReleaseIfIdle())
            {
                released++;
            }
        }

        return released;
    }

    /// <summary>下载/更新某语言的语言包：落暂存目录校验通过后，未被加载则立即替换，已加载则等重启。</summary>
    public async Task<bool> DownloadAsync(string languageCode, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var pack = NativePhoneticManifest.GetPack(languageCode)
            ?? throw new InvalidOperationException($"未知的原生注音语言代码：{languageCode}");
        var slot = _slots[pack.Language];

        // 已经是最新：不重复下载，也不改变现有实例。
        if (_installer.IsUpToDate(pack))
        {
            progress?.Report($"{pack.DisplayName}语言包已是最新版本。");
            slot.InvalidateState();
            return true;
        }

        slot.MarkDownloading("正在准备下载…");
        try
        {
            var outcome = await _installer.InstallAsync(pack, progress, cancellationToken).ConfigureAwait(true);
            if (outcome.RequiresRestart)
            {
                slot.MarkRestartPending(NativePhoneticPackStateText.Description(NativePhoneticPackState.RestartPending));
                progress?.Report($"{pack.DisplayName}语言包新版本已下载，重启程序后生效。");
            }
            else
            {
                slot.ResetAvailability();
                progress?.Report($"{pack.DisplayName}语言包已就绪，即时生效。");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            slot.MarkDownloadFailed("下载已取消。");
            throw;
        }
        catch (Exception exception)
        {
            slot.MarkDownloadFailed(exception.Message);
            throw;
        }
    }

    /// <summary>
    /// 启动时把暂存目录里的新版本提升为正式包：这就是「重启生效」的落地点。
    /// 暂存内容校验不过就整份丢弃，绝不用来覆盖当前可用的包。
    /// </summary>
    public int ApplyStagedPacks()
    {
        var applied = 0;
        foreach (var pack in NativePhoneticManifest.Packs)
        {
            if (!_installer.HasStagedPack(pack))
            {
                continue;
            }

            if (_installer.VerifyStaged(pack) == NativePhoneticPackageFailure.None)
            {
                _installer.PromoteStaged(pack);
                applied++;
            }
            else
            {
                _installer.DiscardStaged(pack);
            }

            _slots[pack.Language].InvalidateState();
        }

        return applied;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var slot in _slots.Values)
        {
            slot.Dispose();
        }
    }

    /// <summary>语言代码归一化：与源语言下拉、词典清单同一口径（容忍展示名与大小写）。</summary>
    internal static string? NormalizeLanguageCode(string? value) => TextAnnotatorRegistry.NormalizeLanguageCode(value);
}

/// <summary>
/// 应用级原生语言包宿主（进程内单例）：设置页状态、下载入口与结果面板共用同一实例，
/// 避免两处各建一份状态机（下载完成一处生效、另一处还是旧状态）。
/// </summary>
internal static class NativePhoneticPackHostHolder
{
    public static NativePhoneticPackHost Default { get; } = new();
}
