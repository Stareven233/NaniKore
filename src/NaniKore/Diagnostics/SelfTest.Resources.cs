using NaniKore.Runtime;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    private static void RunIdleResourceTests()
    {
        var clock = new ResourceTestClock();
        var created = new List<ResourceTestHandle>();
        using var cache = new IdleResource<ResourceTestHandle>(() =>
        {
            var handle = new ResourceTestHandle();
            created.Add(handle);
            return handle;
        }, clock);

        cache.Configure(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromHours(1));
        Check("idle policy does not eagerly create a resource", !cache.ReleaseIfIdle() && created.Count == 0);
        var first = cache.Use(handle => handle);
        clock.Advance(TimeSpan.FromSeconds(29));
        Check("resource is kept before idle timeout", !cache.ReleaseIfIdle() && !first.Disposed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Check("resource is disposed at idle timeout", cache.ReleaseIfIdle() && first.Disposed);
        var second = cache.Use(handle => handle);
        Check("expired resource is recreated lazily", created.Count == 2 && !ReferenceEquals(first, second));

        // 操作内经过很长时间也不能回收；空闲计时必须从操作完成后重新开始。
        cache.Use(handle =>
        {
            clock.Advance(TimeSpan.FromHours(1));
            Check("busy resource cannot be released even after timeout", !cache.ReleaseIfIdle() && !handle.Disposed);
            return 0;
        });
        clock.Advance(TimeSpan.FromSeconds(29));
        Check("idle timer starts at operation completion", !cache.ReleaseIfIdle());
        cache.Configure(TimeSpan.FromSeconds(10));
        Check("shortening policy releases already idle resource", second.Disposed);

        var third = cache.Use(handle => handle);
        cache.Configure(TimeSpan.Zero);
        clock.Advance(TimeSpan.FromDays(100));
        Check("zero policy keeps resources indefinitely", !cache.ReleaseIfIdle() && !third.Disposed);
        cache.Configure(TimeSpan.FromDays(1000));
        Check("long idle durations fit segmented timer scheduling", !third.Disposed);
        cache.Reset();
        Check("reset disposes existing resource without recreating it", third.Disposed && created.Count == 3);
        var fourth = cache.Use(handle => handle);
        Check("reset resource remains usable on next access", created.Count == 4 && !fourth.Disposed);

        cache.Configure(TimeSpan.FromSeconds(30));
        try
        {
            cache.Use<int>(_ => throw new InvalidOperationException("expected"));
        }
        catch (InvalidOperationException)
        {
        }
        clock.Advance(TimeSpan.FromSeconds(30));
        Check("failed operation also leaves resource eligible for idle cleanup", cache.ReleaseIfIdle() && fourth.Disposed);

        using var other = new IdleResource<ResourceTestHandle>(() => new(), clock);
        cache.Configure(TimeSpan.FromSeconds(30));
        other.Configure(TimeSpan.FromSeconds(30));
        var left = cache.Use(handle => handle);
        var right = other.Use(handle => handle);
        clock.Advance(TimeSpan.FromSeconds(20));
        other.Use(_ => 0);
        clock.Advance(TimeSpan.FromSeconds(10));
        Check("resources expire independently of another resource's activity",
            cache.ReleaseIfIdle() && left.Disposed && !other.ReleaseIfIdle() && !right.Disposed);

        // 配置保存和定时清理不能等待后台操作，也不能释放其持有的句柄。
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var running = Task.Run(() => other.Use(handle =>
        {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Resource test operation timed out.");
            }
            return !handle.Disposed;
        }));
        try
        {
            Check("background resource operation starts", entered.Wait(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromHours(1));
            other.Configure(TimeSpan.FromSeconds(1));
            Check("policy change does not dispose concurrent active resource", !other.ReleaseIfIdle() && !right.Disposed);
        }
        finally
        {
            finish.Set();
        }
        Check("active operation completes with a valid handle", running.GetAwaiter().GetResult());
        clock.Advance(TimeSpan.FromSeconds(1));
        Check("new idle policy applies after active operation completes", other.ReleaseIfIdle() && right.Disposed);

        // 用真实定时器验证无需下次访问或手工扫除也能释放。
        using var expired = new ManualResetEventSlim();
        using var automatic = new IdleResource<ResourceTestHandle>(() => new() { OnDispose = expired.Set });
        automatic.Configure(TimeSpan.FromMilliseconds(30));
        automatic.Use(_ => 0);
        Check("idle timer releases resource automatically", expired.Wait(TimeSpan.FromSeconds(5)));
    }

    private sealed class ResourceTestHandle : IDisposable
    {
        public bool Disposed { get; private set; }
        public Action? OnDispose { get; init; }
        public void Dispose()
        {
            Disposed = true;
            OnDispose?.Invoke();
        }
    }

    private sealed class ResourceTestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
