namespace NaniKore.Runtime;

/// <summary>按使用结束时间回收惰性资源；使用、重建和释放共用一把锁，原生句柄不会在操作途中失效。</summary>
internal sealed class IdleResource<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<T> _create;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private T? _resource;
    private long _lastUsed;
    private long _timeoutTicks;
    private int _active;
    private bool _disposed;

    public IdleResource(Func<T> create, TimeProvider? clock = null)
    {
        _create = create;
        _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(_ => ReleaseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TResult Use<TResult>(Func<T, TResult> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active++;
            try
            {
                return operation(_resource ??= _create());
            }
            finally
            {
                _lastUsed = _clock.GetTimestamp();
                _active--;
                Schedule();
            }
        }
    }

    /// <summary>零表示永不释放；保存新策略不等待正在运行的推理，也不重置已有空闲时长。</summary>
    public void Configure(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        Interlocked.Exchange(ref _timeoutTicks, timeout.Ticks);
        if (Monitor.TryEnter(_gate))
        {
            try
            {
                ReleaseIfIdle();
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
        else
        {
            // 与旧定时回调竞争时也要补一次检查，不能让旧的等待时间覆盖新策略。
            ThreadPool.QueueUserWorkItem(static owner =>
            {
                lock (owner._gate)
                {
                    owner.ReleaseIfIdle();
                }
            }, this, preferLocal: false);
        }
    }

    internal bool ReleaseIfIdle()
    {
        // 定时器与配置保存均不等待长时间推理；操作结束时会按最新策略重新安排回收。
        if (!Monitor.TryEnter(_gate))
        {
            return false;
        }
        try
        {
            var timeout = Interlocked.Read(ref _timeoutTicks);
            if (_disposed || _active != 0)
            {
                return false;
            }
            var expired = _resource is not null && timeout > 0 &&
                _clock.GetElapsedTime(_lastUsed).Ticks >= timeout;
            if (expired)
            {
                Release();
            }
            Schedule();
            return expired;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>词典更新后废弃旧实例；下一次使用仍可惰性重建。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Release();
            Schedule();
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
            _timer.Dispose();
            Release();
        }
    }

    private void Schedule()
    {
        var timeout = Interlocked.Read(ref _timeoutTicks);
        var due = Timeout.InfiniteTimeSpan;
        if (!_disposed && _active == 0 && _resource is not null && timeout > 0)
        {
            var remaining = timeout - _clock.GetElapsedTime(_lastUsed).Ticks;
            // 单次定时，不轮询后台；超长时长分段等待，避免超过系统定时器约 49 天的上限。
            due = TimeSpan.FromTicks(Math.Clamp(remaining, TimeSpan.TicksPerMillisecond,
                ((long)uint.MaxValue - 1) * TimeSpan.TicksPerMillisecond));
        }
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Release()
    {
        var previous = _resource;
        _resource = null;
        previous?.Dispose();
    }
}
