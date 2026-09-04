using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;
using DshDesktop.Windows;
using Microsoft.Win32;

namespace DshDesktop.Tray;

/// <summary>
/// 系统托盘:服务控制(重启)、显示/隐藏 dsh 终端(后台控制台)、打开窗口、退出。
/// dsh 服务常驻时关窗隐藏到托盘,进程生命周期由壳自动托管。
/// 图标按任务栏深浅色实时切换为黑/白剪影(浅色任务栏用黑标,深色用白标);
/// 同步把主窗口任务栏大图标换为同一套黑白 ICO,保持两处深浅色一致。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly DshProcessManager _manager;
    private MainWindow? _window;
    private readonly Icon? _black;
    private readonly Icon? _white;
    private readonly ToolStripMenuItem _miRestart;
    private readonly ToolStripMenuItem _miTerminal;
    private bool? _lightTaskbar;
    private bool _disposed;

    public TrayIcon(DshProcessManager manager, Action openWindow, Action exitApp, MainWindow? window = null)
    {
        _manager = manager;
        _black = AppIcons.LoadMonoIcon(lightTaskbar: true);
        _white = AppIcons.LoadMonoIcon(lightTaskbar: false);
        _window = window;

        _notify = new NotifyIcon { Text = "DeepSeek Harness" };
        ApplyThemeIcon();
        _notify.Visible = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _miRestart = new ToolStripMenuItem("重启 dsh 服务");
        _miRestart.Click += async (_, _) => await RunAsync(_manager.RestartAsync());
        _miTerminal = new ToolStripMenuItem("显示 dsh 终端");
        _miTerminal.Click += (_, _) =>
        {
            _manager.SetConsoleVisible(!_manager.IsConsoleVisible);
            UpdateState();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开窗口", null, (_, _) => openWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miRestart);
        menu.Items.Add(_miTerminal);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApp());
        menu.Opening += (_, _) => UpdateState();
        _notify.ContextMenuStrip = menu;

        // 双击托盘图标打开窗口
        _notify.DoubleClick += (_, _) => openWindow();

        _manager.StateChanged += _ => OnUi(UpdateState);
        UpdateState();
    }

    public void Show() { }

    /// <summary>主窗口创建后挂接:把任务栏大图标设为当前任务栏主题对应的黑白图标。</summary>
    public void AttachWindow(MainWindow window)
    {
        if (_disposed) return;
        _window = window;
        if (_lightTaskbar is bool light)
            window.ApplyTaskbarIcon(light);
    }

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

    /// <summary>浅色任务栏用黑色图标,深色任务栏用白色图标;主题未变则跳过。同时同步主窗口任务栏大图标。</summary>
    private void ApplyThemeIcon()
    {
        if (_disposed) return;
        var light = ThemeManager.IsTaskbarLight();
        if (_lightTaskbar == light) return;
        _lightTaskbar = light;
        var icon = (light ? _black : _white)
            ?? _white ?? _black ?? SystemIcons.Application;
        _notify.Icon = icon;
        _window?.ApplyTaskbarIcon(light);
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
            DshProcessManager.ServiceState.Failed => "DeepSeek Harness — dsh 异常,右键重启",
            _ => "DeepSeek Harness — dsh 已停止",
        };
        // 服务由壳自动托管,不提供单独停止;重启只在"能到达运行态"的状态下可用,
        // 避免卡在启动中(例如 90s 等待)时重复点重启制造竞态。
        _miRestart.Enabled = s is DshProcessManager.ServiceState.Running
            or DshProcessManager.ServiceState.Failed
            or DshProcessManager.ServiceState.Stopped;
        // 启动中即可看 npx 下载等输出;句柄已绑上时即使状态抖动也可切换
        _miTerminal.Enabled = _manager.CanToggleConsole;
        _miTerminal.Text = _manager.IsConsoleVisible ? "隐藏 dsh 终端" : "显示 dsh 终端";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try { _notify.Visible = false; _notify.Dispose(); } catch { }
        _window?.RestoreColorTaskbarIcon();
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
