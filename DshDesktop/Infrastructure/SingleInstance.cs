using System.Threading;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 单实例互锁:同一用户会话内只允许一个实例,名称固定、与目标端口无关。
/// 后续实例启动时通过同名事件通知首实例,由首实例把主窗口带到前台(含托盘隐藏状态)。
/// 原实例崩溃(互斥体 abandoned)时,新实例自动接管并视作首实例。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wakeEvent;
    private readonly EventWaitHandle? _stopEvent;
    private readonly Thread? _listener;

    /// <summary>true = 本进程是首个实例,持有互斥锁并监听唤醒通知。</summary>
    public bool IsFirst { get; }

    public SingleInstance(string mutexName, Action? onWake)
    {
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // 同名互斥体已存在:若原持有者已崩溃(abandoned)则抢过来接管为首实例;
            // 仍被持有则本进程是第二实例。WaitOne(0) 在 abandoned 时可能返回 true 或抛异常。
            try
            {
                if (_mutex.WaitOne(0)) createdNew = true;
            }
            catch (AbandonedMutexException) { createdNew = true; }
        }
        IsFirst = createdNew;

        // 唤醒事件(跨进程同内核对象):首实例监听,后续实例 Set
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + ".Wake");
        if (createdNew && onWake is not null)
        {
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            _listener = new Thread(() => Listen(onWake)) { IsBackground = true };
            _listener.Start();
        }
    }

    /// <summary>通知首实例把主窗口带到前台(第二实例的启动路径)。</summary>
    public void NotifyExisting()
    {
        try { _wakeEvent.Set(); } catch { /* 首实例可能已退出,忽略 */ }
    }

    private void Listen(Action onWake)
    {
        var handles = new WaitHandle[] { _wakeEvent, _stopEvent! };
        while (true)
        {
            int idx;
            try { idx = WaitHandle.WaitAny(handles); }
            catch { break; } // 句柄已释放,进程退出中
            if (idx != 0) break;
            try { onWake(); } catch { /* 单次唤醒失败不终止监听 */ }
        }
    }

    public void Dispose()
    {
        try { _stopEvent?.Set(); } catch { }
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        try { _wakeEvent.Dispose(); } catch { }
        try { _stopEvent?.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
