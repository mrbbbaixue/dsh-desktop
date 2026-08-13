// 项目同时启用 UseWPF + UseWindowsForms(托盘),此处用别名消除 System.Windows.Forms.Application 歧义
using System.Windows;
using Application = System.Windows.Application;

namespace DshDesktop;

/// <summary>
/// 应用入口:单实例、托盘生命周期、dsh 进程管理、主窗口。
/// 窗口关闭时隐藏到托盘(服务常驻),托盘"退出"才真正退出并停止服务。
/// </summary>
public partial class App : Application
{
    private SingleInstance? _single;
    private DshProcessManager? _manager;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var (url, port) = ShellLogic.ResolveTarget(Environment.GetEnvironmentVariable("DSH_WEB_URL"));
        // 设置 DSH_WEB_URL 时视为"外部托管服务",壳不再自动拉起/停止 dsh。
        var externalManaged = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));

        // 单实例:按目标端口隔离,重复启动只把已开窗口带到前台。
        _single = new SingleInstance($"Local\\DshDesktop.SingleInstance.{port}");
        if (!_single.IsFirst)
        {
            SingleInstance.ActivateExisting("DeepSeek Harness");
            Shutdown();
            return;
        }

        Log.Init();
        Log.Info($"DshDesktop 启动: url={url} externalManaged={externalManaged} args={string.Join(' ', e.Args)}");

        _manager = new DshProcessManager(url, port, externalManaged);
        _manager.StateChanged += state =>
        {
            // 服务就绪且窗口可见 → 刷新页面(覆盖:托盘手动重启、崩溃自动重启)
            if (state == DshProcessManager.ServiceState.Running)
                Dispatcher.InvokeAsync(() => _window?.ReloadWhenReadyAsync());
        };

        _window = new MainWindow(url, _manager);
        _tray = new TrayIcon(_manager, openWindow: ShowMainWindow, exitApp: RequestExit);
        _tray.Show();

        // 点关闭按钮 → 隐藏到托盘,进程与窗口对象都保留
        _window.Closing += (_, ev) =>
        {
            if (_exitRequested) return;
            ev.Cancel = true;
            _window.Hide();
        };

        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();

        // 后台拉起 dsh 服务(未启动时)
        _ = _manager.EnsureRunningAsync();
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.Activate();
        _ = _window.NavigateWhenReadyAsync();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 停止自己拉起的 dsh 进程(外部托管进程不碰)
        try { _tray?.Dispose(); } catch { }
        try { _manager?.Dispose(); } catch { }
        try { _single?.Dispose(); } catch { }
        Log.Info("DshDesktop 退出");
        base.OnExit(e);
    }
}
