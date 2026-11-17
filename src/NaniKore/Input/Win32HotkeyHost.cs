using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NaniKore.Interop;

namespace NaniKore.Input;

/// <summary>
/// 纯 Win32 消息专用窗口承载全局热键，彻底脱离 WinForms NativeWindow。
/// 运行在专有后台 STA 线程，完全兼容 NativeAOT（使用 LibraryImport 与 UnmanagedCallersOnly）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Win32HotkeyHost : IDisposable
{
    private const uint WM_USER_EXECUTE = 0x0400 + 101;
    private static readonly ConcurrentDictionary<nint, Win32HotkeyHost> ActiveHosts = new();

    private static readonly Lazy<Win32HotkeyHost> LazyShared = new(() => new Win32HotkeyHost());

    /// <summary>进程级共享热键宿主单例。</summary>
    public static Win32HotkeyHost Shared => LazyShared.Value;

    private readonly string _className = $"NaniKoreHotkeyHost_{Guid.NewGuid():N}";
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Action> _callbacks = new();
    private readonly object _requestLock = new();
    private readonly Queue<(Action Execute, Action<Exception> Fail)> _actionQueue = new();

    private nint _hWnd;
    private nint _hInstance;
    private bool _disposed;
    private Exception? _postError;

    public Win32HotkeyHost()
    {
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "NaniKore-Win32HotkeyThread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 同步等待消息窗口创建就绪
        _readySource.Task.GetAwaiter().GetResult();
    }

    public nint Handle => _hWnd;

    /// <summary>
    /// 注册全局热键。
    /// 若已被其他进程注册，抛出带有 Win32 错误码（1409）的 Win32Exception。
    /// </summary>
    public void RegisterHotKey(int id, uint modifiers, uint vk, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        InvokeOnWindowThread(() =>
        {
            // 同一窗口的重复 ID 可能保留多个原生注册，必须在调用 Win32 前拒绝。
            if (_callbacks.ContainsKey(id))
            {
                throw new InvalidOperationException($"热键 ID {id} 已注册");
            }

            // MOD_NOREPEAT (0x4000) 避免长按热键震荡连发
            var success = NativeMethods.RegisterHotKey(_hWnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
            if (!success)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"无法注册热键 (id={id}, vk=0x{vk:X}, mod=0x{modifiers:X})。错误码 {error}，可能正被其他程序占用");
            }

            _callbacks[id] = callback;
        });
    }

    /// <summary>
    /// 注销热键（录制期间临时注销，或退出时注销）。
    /// </summary>
    public bool TryUnregisterHotKey(int id)
    {
        try
        {
            return InvokeOnWindowThread(() =>
            {
                _callbacks.TryRemove(id, out _);
                return NativeMethods.UnregisterHotKey(_hWnd, id);
            });
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void InvokeOnWindowThread(Action action) => InvokeOnWindowThread(() =>
    {
        action();
        return true;
    });

    private T InvokeOnWindowThread<T>(Func<T> func)
    {
        TaskCompletionSource<T>? source = null;
        lock (_requestLock)
        {
            // 接收、投递和停止共用同一边界，停止后不再留下无人处理的请求。
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Thread.CurrentThread != _thread)
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = source;
                _actionQueue.Enqueue((() =>
                {
                    try
                    {
                        completion.TrySetResult(func());
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, ex => { completion.TrySetException(ex); }));
                PostExecuteLocked();
            }
        }

        // 窗口线程不能排队后等待自身；已接收的调用可在停止边界之后完成。
        return source is null ? func() : source.Task.GetAwaiter().GetResult();
    }

    // 调用者必须持有 _requestLock；投递失败即关闭接收并终结全部待处理请求。
    private void PostExecuteLocked()
    {
        if (!NativeMethods.PostMessageW(_hWnd, WM_USER_EXECUTE, 0, 0))
        {
            _postError = new Win32Exception(Marshal.GetLastPInvokeError(), "投递热键窗口请求失败");
            _disposed = true;
            FailPendingLocked(_postError);
        }
    }

    private void FailPendingLocked(Exception error)
    {
        while (_actionQueue.TryDequeue(out var request))
        {
            request.Fail(error);
        }
    }

    private void ThreadProc()
    {
        Exception? exitError = null;
        try
        {
            _hInstance = NativeMethods.GetModuleHandle(null);

            fixed (char* pClassName = _className)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = &StaticWndProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = _hInstance,
                    hIcon = 0,
                    hCursor = 0,
                    hbrBackground = 0,
                    lpszMenuName = null,
                    lpszClassName = pClassName,
                    hIconSm = 0
                };

                var atom = NativeMethods.RegisterClassExW(in wcx);
                if (atom == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    _readySource.SetException(new Win32Exception(error, "注册热键消息窗口类失败"));
                    return;
                }
            }

            _hWnd = NativeMethods.CreateWindowExW(
                0,
                _className,
                "NaniKoreHotkeyHostWindow",
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HWND_MESSAGE,
                0,
                _hInstance,
                0);

            if (_hWnd == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                _readySource.SetException(new Win32Exception(error, "创建热键消息窗口失败"));
                return;
            }

            ActiveHosts[_hWnd] = this;
            _readySource.SetResult(true);

            // Win32 标准消息循环
            while (NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessageW(in msg);
            }
        }
        catch (Exception ex)
        {
            exitError = ex;
            _readySource.TrySetException(ex);
        }
        finally
        {
            lock (_requestLock)
            {
                _disposed = true;
                FailPendingLocked(exitError ?? new ObjectDisposedException(nameof(Win32HotkeyHost)));
            }

            if (_hWnd != 0)
            {
                foreach (var id in _callbacks.Keys)
                {
                    NativeMethods.UnregisterHotKey(_hWnd, id);
                }
                _callbacks.Clear();
                ActiveHosts.TryRemove(_hWnd, out _);
                NativeMethods.DestroyWindow(_hWnd);
                _hWnd = 0;
            }

            if (_hInstance != 0)
            {
                NativeMethods.UnregisterClassW(_className, _hInstance);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (ActiveHosts.TryGetValue(hWnd, out var host))
        {
            return host.InstanceWndProc(hWnd, msg, wParam, lParam);
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private nint InstanceWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_USER_EXECUTE:
                while (true)
                {
                    Action action;
                    lock (_requestLock)
                    {
                        if (!_actionQueue.TryDequeue(out var request))
                        {
                            if (_disposed)
                            {
                                NativeMethods.PostQuitMessage(0);
                            }
                            break;
                        }
                        action = request.Execute;
                    }
                    // 出队即取得执行权；关闭只能使尚未出队的请求失败。
                    action();
                }
                return 0;

            case NativeMethods.WM_HOTKEY:
                var id = (int)wParam;
                if (_callbacks.TryGetValue(id, out var callback))
                {
                    // 异步分发回调，避免阻塞消息线程
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            callback();
                        }
                        catch (Exception ex)
                        {
                            // 记录异常，绝不静默吞没，同时保护消息泵继续运行
                            Console.Error.WriteLine($"[Win32HotkeyHost] 全局热键回调执行异常 (id={id}): {ex}");
                        }
                    });
                }
                return 0;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return 0;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return 0;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        var onWindowThread = Thread.CurrentThread == _thread;
        lock (_requestLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (onWindowThread)
                {
                    // 窗口线程关闭时不能等待自身，队列中的调用必须立即收到失败。
                    FailPendingLocked(new ObjectDisposedException(nameof(Win32HotkeyHost)));
                    NativeMethods.PostQuitMessage(0);
                }
                else
                {
                    // 已接收的请求先执行完，再由消息处理器退出并统一清理。
                    PostExecuteLocked();
                }
            }

            // 无法唤醒线程时报告失败，不能继续等待一个可能永远不会发生的退出。
            if (_postError is not null)
            {
                throw new InvalidOperationException("无法唤醒热键窗口线程以完成关闭", _postError);
            }
        }

        if (!onWindowThread)
        {
            // 并发关闭者均在锁外等待清理完成，窗口线程自身绝不 Join。
            _thread.Join();
        }
    }
}
