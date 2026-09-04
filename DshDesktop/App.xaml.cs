// 项目同时启用 UseWPF + UseWindowsForms(托盘),此处用别名消除 System.Windows.Forms.Application 歧义
using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace DshDesktop;

/// <summary>
/// 应用入口:单实例、托盘生命周期、dsh 进程管理、主窗口。
/// - 只有一个主窗口:关窗隐藏到托盘(不重载);最小化走系统默认;托盘打开只激活
/// - 托盘「退出」才真正退出并停止服务
/// - 注销/关机(SessionEnding)时停止 dsh,避免子进程残留
/// </summary>
public partial class App : Application
{
    private readonly string _url;
    private readonly int _port;
    private SingleInstance? _single;
    private DshProcessManager? _manager;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exitRequested;

    public App()
    {
        (_url, _port) = ShellLogic.ResolveTarget(Environment.GetEnvironmentVariable("DSH_WEB_URL"));
        // 设置 DSH_WEB_URL 时视为"外部托管服务",壳不再自动拉起/停止 dsh。
        var externalManaged = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));
        _manager = new DshProcessManager(_url, _port, externalManaged);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        NativeLoader.Ensure();
        base.OnStartup(e);

        // 单实例互锁:同一用户会话全局唯一(与目标端口无关)。
        // 已存在实例时,通知对方把窗口带到前台后立即退出,不重复拉起 dsh。
        _single = new SingleInstance("Local\\DshDesktop.SingleInstance", RequestWakeFromSecondInstance);
        if (!_single.IsFirst)
        {
            _single.NotifyExisting();
            Shutdown();
            return;
        }

        Log.Init();
        Log.Info($"DshDesktop 启动: url={_url} args={string.Join(" ", e.Args)}");
        ClearLegacyAutostart();
        // 启动即探测 Node.js / npm / dsh,一行写清"已安装还是未安装"。
        Log.Info($"环境检测: {ShellLogic.FormatRuntimeSummary(ShellLogic.ProbeRuntime())}");

        _manager!.StateChanged += state =>
        {
            // 服务就绪且窗口可见 → 刷新页面(覆盖:托盘手动重启、崩溃自动重启)
            if (state == DshProcessManager.ServiceState.Running)
                Dispatcher.InvokeAsync(() => _window?.ReloadWhenReadyAsync());
        };

        _tray = new TrayIcon(_manager, openWindow: ShowMainWindow, exitApp: RequestExit);
        _tray.Show();
        ShowMainWindow();
        _tray.AttachWindow(_window!);

        // 后台拉起 dsh 服务(未启动时)
        _ = _manager.EnsureRunningAsync();
    }

    /// <summary>
    /// 第二实例启动时在本进程触发:把主窗口带回前台(隐藏/最小化均恢复)。
    /// 监听线程回调,须经 Dispatcher 切回 UI 线程。
    /// </summary>
    private void RequestWakeFromSecondInstance()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_exitRequested) return;
            Log.Info("检测到重复启动,把主窗口带到前台");
            _window?.Reveal();
        });
    }

    /// <summary>显示主窗口;终身只创建一次。关窗是隐藏,最小化保持系统行为。</summary>
    private void ShowMainWindow()
    {
        if (_manager is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_manager);
            _window.Closing += (_, ev) =>
            {
                if (_exitRequested) return;
                ev.Cancel = true;
                _window.HideToBackground();
            };
            _window.Show();
            _window.Activate();
            _ = _window.NavigateWhenReadyAsync();
            return;
        }
        _window.Reveal();
    }

    /// <summary>清掉旧版写入 HKCU Run 的开机自启项,避免升级后仍被拉起。</summary>
    private static void ClearLegacyAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("DshDesktop", throwOnMissingValue: false);
        }
        catch { /* 清不掉不影响主流程 */ }
    }

    private void RequestExit()
    {
        _exitRequested = true;
        // 真正退出前保存窗口状态(隐藏到托盘不触发)
        try { _window?.SaveWindowState(); } catch { /* 保存失败不影响退出 */ }
        Shutdown();
    }

    /// <summary>注销/关机:同步停止 dsh 进程,避免系统结束后残留。</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info("系统会话结束,停止 dsh 服务");
        _manager?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 停止自己拉起的 dsh 进程(外部托管进程不碰);SessionEnding 已停过,此处幂等
        try { _tray?.Dispose(); } catch { }
        try { _manager?.Dispose(); } catch { }
        try { _single?.Dispose(); } catch { }
        Log.Info("DshDesktop 退出");
        base.OnExit(e);
    }
}
