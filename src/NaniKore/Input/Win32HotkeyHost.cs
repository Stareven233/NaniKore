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
    private readonly ConcurrentQueue<Action> _actionQueue = new();

    private nint _hWnd;
    private nint _hInstance;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);

        InvokeOnWindowThread(() =>
        {
            // MOD_NOREPEAT (0x4000) 避免长按热键震荡连发
            var success = NativeMethods.RegisterHotKey(_hWnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
            if (!success)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"无法注册热键 (id={id}, vk=0x{vk:X}, mod=0x{modifiers:X})。错误码 {error}，可能正被其他程序占用。");
            }

            _callbacks[id] = callback;
        });
    }

    /// <summary>
    /// 注销热键（录制期间临时注销，或退出时注销）。
    /// </summary>
    public bool TryUnregisterHotKey(int id)
    {
        if (_disposed || _hWnd == 0)
        {
            return false;
        }

        return InvokeOnWindowThread(() =>
        {
            _callbacks.TryRemove(id, out _);
            return NativeMethods.UnregisterHotKey(_hWnd, id);
        });
    }

    private void InvokeOnWindowThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actionQueue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        NativeMethods.PostMessageW(_hWnd, WM_USER_EXECUTE, 0, 0);
        tcs.Task.GetAwaiter().GetResult();
    }

    private T InvokeOnWindowThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actionQueue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        NativeMethods.PostMessageW(_hWnd, WM_USER_EXECUTE, 0, 0);
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void ThreadProc()
    {
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
            if (!_readySource.Task.IsCompleted)
            {
                _readySource.SetException(ex);
            }
        }
        finally
        {
            if (_hWnd != 0)
            {
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
                while (_actionQueue.TryDequeue(out var action))
                {
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
                        catch
                        {
                            // 保护消息泵不受外部回调异常影响
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hWnd != 0)
        {
            InvokeOnWindowThread(() =>
            {
                foreach (var id in _callbacks.Keys)
                {
                    NativeMethods.UnregisterHotKey(_hWnd, id);
                }
                _callbacks.Clear();
            });

            NativeMethods.PostMessageW(_hWnd, NativeMethods.WM_CLOSE, 0, 0);
            _thread.Join(1000);
        }
    }
}
