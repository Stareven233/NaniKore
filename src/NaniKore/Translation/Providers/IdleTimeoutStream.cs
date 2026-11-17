namespace NaniKore.Translation.Providers;

/// <summary>
/// 流式响应的“空闲超时”包装（R08）：任意两次成功读取之间若超过 idleTimeout 仍无新字节，
/// 就取消本次读取并抛出 <see cref="ProviderTimeoutException"/>。
/// <para>
/// 之所以按“空闲间隔”而非“总时长”计时：持续有数据到达会不断重置计时，正常输出的长回复不会被总时长截断；
/// 只有真正卡住的流（例如服务端发出 200 响应头后不再发送正文）才会结束等待。
/// 它只约束“读不到数据”的空闲间隔；连接与等待响应头那一段由 HttpClient.Timeout 负责。
/// </para>
/// </summary>
internal sealed class IdleTimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idleTimeout;

    public IdleTimeoutStream(Stream inner, TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        _inner = inner;
        _idleTimeout = idleTimeout;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 每次读取重置为空闲窗口：只要在 idleTimeout 内拿到数据就重新计时。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        try
        {
            return await _inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 调用方没取消却读到取消，只能是空闲计时触发：连接仍在但服务端长时间不发送数据。
            throw new ProviderTimeoutException(
                $"翻译服务已连接，但超过 {_idleTimeout.TotalSeconds:0.#} 秒未收到任何数据，已中止本次等待" +
                "若上游模型或网络较慢，可在设置中调大该配置档的 timeout_seconds");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // 同步读取不参与流式主路径（SseReader 全为异步读取），仅按约定转发以保持 Stream 契约完整。
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
