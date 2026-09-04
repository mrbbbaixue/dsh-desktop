using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshDesktop.Tray;

/// <summary>
/// 系统托盘:进程控制(启动/重启/停止)、打开窗口、退出。
/// dsh 服务常驻时关窗隐藏到托盘,进程生命周期由托盘菜单管理。
/// 图标按任务栏深浅色在黑/白两套之间实时切换(浅色任务栏用黑标,深色用白标)。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly DshProcessManager _manager;
    private readonly Icon? _black;
    private readonly Icon? _white;
    private readonly ToolStripMenuItem _miStart;
    private readonly ToolStripMenuItem _miRestart;
    private readonly ToolStripMenuItem _miStop;
    private bool? _lightTaskbar;
    private bool _disposed;

    public TrayIcon(DshProcessManager manager, Action openWindow, Action exitApp)
    {
        _manager = manager;
        _black = AppIcons.LoadTrayIcon(lightTaskbar: true);
        _white = AppIcons.LoadTrayIcon(lightTaskbar: false);

        _notify = new NotifyIcon { Text = "DeepSeek Harness" };
        ApplyThemeIcon();
        _notify.Visible = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _miStart = new ToolStripMenuItem("启动 dsh 服务");
        _miStart.Click += async (_, _) => await RunAsync(_manager.EnsureRunningAsync());
        _miRestart = new ToolStripMenuItem("重启 dsh 服务");
        _miRestart.Click += async (_, _) => await RunAsync(_manager.RestartAsync());
        _miStop = new ToolStripMenuItem("停止 dsh 服务");
        _miStop.Click += async (_, _) => await RunAsync(_manager.StopAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开窗口", null, (_, _) => openWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miStart);
        menu.Items.Add(_miRestart);
        menu.Items.Add(_miStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApp());
        _notify.ContextMenuStrip = menu;

        // 双击托盘图标打开窗口
        _notify.DoubleClick += (_, _) => openWindow();

        _manager.StateChanged += _ => OnUi(UpdateState);
        UpdateState();
    }

    public void Show() { }

    /// <summary>执行托盘操作并记录异常,避免 async void 静默丢失。</summary>
    private static async Task RunAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    private void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || e.Category != UserPreferenceCategory.General) return;
        OnUi(ApplyThemeIcon);
    }

    /// <summary>浅色任务栏用黑色图标,深色任务栏用白色图标;主题未变则跳过。</summary>
    private void ApplyThemeIcon()
    {
        if (_disposed) return;
        var light = ThemeManager.IsTaskbarLight();
        if (_lightTaskbar == light) return;
        _lightTaskbar = light;
        _notify.Icon = (light ? _black : _white)
            ?? _white ?? _black ?? SystemIcons.Application;
    }

    /// <summary>根据服务状态刷新托盘提示文本与菜单可用性(状态事件可能来自线程池)。</summary>
    private void UpdateState()
    {
        var s = _manager.State;
        _notify.Text = s switch
        {
            DshProcessManager.ServiceState.Running => "DeepSeek Harness — dsh 运行中",
            DshProcessManager.ServiceState.Starting => "DeepSeek Harness — dsh 启动中…",
            DshProcessManager.ServiceState.Stopping => "DeepSeek Harness — dsh 停止中…",
            DshProcessManager.ServiceState.Failed => "DeepSeek Harness — dsh 异常,右键重试",
            _ => "DeepSeek Harness — dsh 已停止",
        };
        _miStart.Enabled = s is DshProcessManager.ServiceState.Stopped or DshProcessManager.ServiceState.Failed;
        _miRestart.Enabled = s is DshProcessManager.ServiceState.Running or DshProcessManager.ServiceState.Failed;
        _miStop.Enabled = s is DshProcessManager.ServiceState.Running or DshProcessManager.ServiceState.Starting;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try { _notify.Visible = false; _notify.Dispose(); } catch { }
        DestroyTrayIcon(_black);
        DestroyTrayIcon(_white);
    }

    private static void DestroyTrayIcon(Icon? icon)
    {
        if (icon is null) return;
        try
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        catch { /* ignore */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool DestroyIcon(IntPtr handle);
}
