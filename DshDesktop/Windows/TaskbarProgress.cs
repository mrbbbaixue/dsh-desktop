using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DshDesktop.Windows;

/// <summary>任务栏按钮进度状态(TBPFLAG)。</summary>
internal enum TBPF
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8,
}

/// <summary>
/// ITaskbarList3:方法顺序就是 vtable 顺序,前面的槽位必须原样声明
/// (ITaskbarList 的 5 个 + ITaskbarList2 的 1 个),不能省略或调序。
/// </summary>
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    void SetProgressState(IntPtr hwnd, TBPF tbpFlags);
}

/// <summary>CLSID_TaskbarList。</summary>
[ComImport]
[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarListCoClass
{
}

/// <summary>
/// 窗口任务栏按钮上的进度条(挂在诊断窗口自己的 HWND 上,不影响主窗口按钮)。
/// COM 不可用时全部降级为 no-op —— 进度显示绝不能拖累安装流程。
/// 只在窗口线程调用。
/// </summary>
internal sealed class TaskbarProgress : IDisposable
{
    private readonly ITaskbarList3? _list;
    private readonly IntPtr _hwnd;

    public TaskbarProgress(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            var list = (ITaskbarList3)new TaskbarListCoClass();
            list.HrInit();
            _list = list;
        }
        catch (Exception ex)
        {
            Log.Error($"任务栏进度不可用(不影响安装): {ex.Message}");
        }
    }

    /// <summary>绿色滚动条:阶段内部没有百分比(winget 安装、npm 安装、解压式步骤)。</summary>
    public void SetIndeterminate() => SetState(TBPF.Indeterminate);

    /// <summary>绿色百分比条。total 为 0 时忽略。</summary>
    public void SetValue(ulong completed, ulong total)
    {
        if (_list is null || total == 0) return;
        try
        {
            _list.SetProgressValue(_hwnd, Math.Min(completed, total), total);
            _list.SetProgressState(_hwnd, TBPF.Normal);
        }
        catch (Exception ex)
        {
            Log.Error($"任务栏进度设置失败: {ex.Message}");
        }
    }

    /// <summary>红色错误条。</summary>
    public void SetError() => SetState(TBPF.Error);

    /// <summary>清除进度显示。</summary>
    public void Clear() => SetState(TBPF.NoProgress);

    private void SetState(TBPF state)
    {
        if (_list is null) return;
        try
        {
            _list.SetProgressState(_hwnd, state);
        }
        catch (Exception ex)
        {
            Log.Error($"任务栏进度状态设置失败: {ex.Message}");
        }
    }

    public void Dispose() => Clear();
}
