using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace DshDesktop.Windows;

/// <summary>
/// 诊断窗口:环境诊断(Node.js / npm / WebView2 / dsh)与一键安装缺失组件(系统级,走 winget 或镜像 MSI)。
/// 安装进度同时显示在窗口进度条、日志区与任务栏按钮上;进度条只在安装中/完成/失败时出现,空闲时收起。
/// 关闭即销毁(不像主窗口隐藏到托盘);安装进行中不允许关闭,避免留下半截状态。
/// 深浅色跟随系统:整组替换配色资源(XAML 用 DynamicResource 引用),系统主题切换时实时刷新。
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private readonly DshProcessManager? _manager;
    private TaskbarProgress? _taskbar;
    private EnvReport? _report;
    private CancellationTokenSource? _cts;
    private bool _installing;
    private bool _succeeded;

    public DiagnosticsWindow(DshProcessManager? manager)
    {
        _manager = manager;
        InitializeComponent();
        Icon = AppIcons.LoadWindowIcon();

        SourceInitialized += (_, _) =>
        {
            ThemeManager.Apply(this);
            ApplyTheme();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            _taskbar = new TaskbarProgress(this);
        };
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ---- 环境诊断 ----

    private async void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        ResetProgress();
        await RefreshAsync();
    }

    /// <summary>重新探测并刷新列表(探测会起进程,放后台线程)。</summary>
    private async Task RefreshAsync()
    {
        RecheckButton.IsEnabled = false;
        var report = await Task.Run(() => EnvProbe.Run());
        _report = report;
        Items.ItemsSource = report.Items;
        InstallButton.IsEnabled = report.NeedsInstall;
        InstallHint.Text = report.NeedsInstall
            ? report.Summary
            : "环境完整,无需安装";
        RecheckButton.IsEnabled = true;
    }

    // ---- 一键安装 ----

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        // 安装中同一个按钮变「取消」
        if (_installing)
        {
            _cts?.Cancel();
            return;
        }
        var report = _report;
        if (report is null || !report.NeedsInstall) return;

        var source = SourceBox.SelectedIndex == 1 ? InstallSourceKind.Mirror : InstallSourceKind.Official;
        _installing = true;
        _succeeded = false;
        _cts = new CancellationTokenSource();
        // 先清掉上一次的完成/失败状态,再从第一阶段重新开始
        ResetProgress();
        InstallButton.Content = "取消";
        SourceBox.IsEnabled = false;
        RecheckButton.IsEnabled = false;
        LogBox.Clear();
        AppendLog($"安装源: {(source == InstallSourceKind.Mirror ? "国内镜像(npmmirror)" : "官方源(winget)")}");

        var progress = new Progress<InstallProgress>(OnProgress);
        try
        {
            // InstallMissingAsync 内部整体跑在线程池上,UI 线程只收进度回调
            var installer = new ToolchainInstaller(source, AppendLog);
            await installer.InstallMissingAsync(report, progress, _cts.Token);
        }
        finally
        {
            _installing = false;
            InstallButton.Content = "安装缺失组件";
            SourceBox.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await RefreshAsync();
            if (_succeeded) await RestartServiceIfNeededAsync();
        }
    }

    /// <summary>进度回调(Progress&lt;T&gt; 已切回 UI 线程);窗口进度条与任务栏进度同一份状态,一起维护。</summary>
    private void OnProgress(InstallProgress p)
    {
        switch (p.Stage)
        {
            case InstallStage.InstallingNode when p.Percent is double percent:
                // 镜像源自己下载 MSI:有真实百分比,窗口与任务栏都用确定态
                SetBar(percent);
                _taskbar?.SetValue((ulong)(percent * 10), 1000);
                break;
            case InstallStage.Done:
                SetBar(100);
                _taskbar?.Clear();
                _succeeded = true;
                break;
            case InstallStage.Failed:
                SetBar(0, error: true);
                _taskbar?.SetError();
                break;
            case InstallStage.Cancelled:
                // 取消不是失败:收掉进度条与任务栏状态,不留红条
                ResetProgress();
                break;
            default:
                // 准备 / winget 装 Node / 装 dsh / 校验:没有百分比可报,一律不定态
                SetBar(null);
                _taskbar?.SetIndeterminate();
                break;
        }
        ProgressText.Text = p.Message;
    }

    /// <summary>确定态(percent 有值)或不定态(percent 为 null)显示进度条;error 时文字转错误色。</summary>
    private void SetBar(double? percent, bool error = false)
    {
        InstallBar.Visibility = Visibility.Visible;
        InstallBar.IsIndeterminate = percent is null;
        InstallBar.Value = percent ?? 0;
        ProgressText.SetResourceReference(ForegroundProperty, error ? "ErrorFg" : "TextFg");
    }

    /// <summary>回到空闲:收起进度条、清空任务栏进度、文字归位(重新检测与取消安装都走这里)。</summary>
    private void ResetProgress()
    {
        InstallBar.Visibility = Visibility.Collapsed;
        InstallBar.IsIndeterminate = false;
        InstallBar.Value = 0;
        ProgressText.SetResourceReference(ForegroundProperty, "MutedFg");
        ProgressText.Text = "就绪";
        _taskbar?.Clear();
    }

    /// <summary>装完若服务还没起来,顺手重启一次(装之前多半因缺 Node 而失败)。</summary>
    private async Task RestartServiceIfNeededAsync()
    {
        if (_manager is null || _manager.ExternalManaged) return;
        if (_manager.State == DshProcessManager.ServiceState.Running) return;
        AppendLog("重启 dsh 服务…");
        try
        {
            await _manager.RestartAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"重启 dsh 服务失败: {ex.Message}");
        }
    }

    // ---- 日志 ----

    /// <summary>日志可能来自后台线程(进程输出、下载回调),统一切回 UI 线程。</summary>
    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => AppendLog(line));
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ---- 主题与关闭 ----

    /// <summary>按当前系统主题整组替换配色资源:XAML 里全用 DynamicResource 引用,换掉即刷新。</summary>
    private void ApplyTheme()
    {
        var p = ThemePalette.For(ThemeManager.IsSystemDarkMode());
        Resources["WindowBg"] = Frozen(p.Background);
        Resources["TextFg"] = Frozen(p.Foreground);
        Resources["MutedFg"] = Frozen(p.Muted);
        Resources["PanelBorder"] = Frozen(p.Border);
        Resources["InputBg"] = Frozen(p.Input);
        Resources["HoverBg"] = Frozen(p.Hover);
        Resources["ErrorFg"] = Frozen(p.Error);
        Resources["AccentFg"] = Frozen(p.Accent);
    }

    /// <summary>系统深浅色切换(ImmersiveColorSet)时实时跟随:原生标题栏 + 窗口配色。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == ImmersiveColorSet)
        {
            ThemeManager.Apply(this);
            ApplyTheme();
        }
        return IntPtr.Zero;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 安装中不关窗:避免留下装到一半的系统状态
        if (_installing)
        {
            e.Cancel = true;
            return;
        }
        try { _taskbar?.Dispose(); } catch { /* 关闭失败不影响退出 */ }
        base.OnClosing(e);
    }
}
