using System.Runtime.InteropServices;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 单实例控制:按目标端口隔离的互斥锁;
/// 重复启动时把已打开的窗口带到前台(FindWindow 按标题定位)。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int SW_RESTORE = 9;

    private readonly Mutex _mutex;

    /// <summary>true = 本进程是首个实例,持有锁。</summary>
    public bool IsFirst { get; }

    public SingleInstance(string mutexName)
    {
        _mutex = new Mutex(true, mutexName, out var createdNew);
        IsFirst = createdNew;
    }

    /// <summary>把已存在的窗口恢复并带到前台(第二实例的激活路径)。</summary>
    public static void ActivateExisting(string windowTitle)
    {
        var hwnd = FindWindow(null, windowTitle);
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        try
        {
            if (IsFirst) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        catch { /* 已释放等情况忽略 */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
