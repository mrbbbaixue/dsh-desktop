using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshDesktop.Windows;

/// <summary>
/// 主窗口:WebView2 填充 + 系统原生标题栏(深浅色跟随系统)。
/// 关窗只隐藏到托盘,WebView 继续在后台跑;同一实例贯穿整个进程。最小化走系统默认。
/// 窗口尺寸/最大化状态记忆在 %USERPROFILE%\.dsh\desktop.ini,启动恢复、退出前保存。
/// 仅首次就绪或 dsh 换了启动 URL(重启后新的 launch-token)才导航。
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private readonly string _userDataFolder;
    private readonly DshProcessManager _manager;
    private readonly WindowPrefs _prefs;
    private ImageSource? _colorIcon;
    private bool _webReady;
    private bool _navigateInFlight;
    private string? _navigatedUrl;

    public MainWindow(DshProcessManager manager, WindowPrefs? prefs = null)
    {
        InitializeComponent();
        _manager = manager;
        _prefs = prefs ?? new WindowPrefs();
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshDesktop", "WebView2");

        RestoreWindowState();

        Icon = AppIcons.LoadWindowIcon();
        SourceInitialized += (_, _) =>
        {
            ThemeManager.Apply(this);
            HookSystemThemeChange();
        };
    }

    /// <summary>启动时按记忆恢复窗口尺寸与最大化状态;数据损坏/越界时回退默认。</summary>
    private void RestoreWindowState()
    {
        _prefs.TryLoad();
        if (_prefs.Width >= MinWidth && _prefs.Height >= MinHeight)
        {
            Width = _prefs.Width;
            Height = _prefs.Height;
        }
        if (_prefs.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>退出前把当前窗口状态写回配置文件(最大化时记录还原尺寸)。</summary>
    public void SaveWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            _prefs.Width = RestoreBounds.Width;
            _prefs.Height = RestoreBounds.Height;
            _prefs.Maximized = true;
        }
        else
        {
            _prefs.Width = ActualWidth;
            _prefs.Height = ActualHeight;
            _prefs.Maximized = false;
        }
        _prefs.Save();
    }

    private void HookSystemThemeChange()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    /// <summary>系统深浅色主题切换(ImmersiveColorSet)时实时跟随标题栏。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE)
        {
            var setting = Marshal.PtrToStringUni(lParam);
            if (setting == ImmersiveColorSet)
            {
                ThemeManager.Apply(this);
                ApplyWebViewBackground();
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>把任务栏大图标换为单色剪影(与托盘图标同主题一致)。原彩色图标保留,Dispose 时还原。</summary>
    public void ApplyTaskbarIcon(bool lightTaskbar)
    {
        _colorIcon ??= Icon;
        Icon = AppIcons.LoadMonoIconSource(lightTaskbar) ?? Icon;
    }

    /// <summary>还原彩色主图标(退出清空托盘时调用)。</summary>
    public void RestoreColorTaskbarIcon()
    {
        if (_colorIcon is not null)
        {
            Icon = _colorIcon;
            _colorIcon = null;
        }
    }

    /// <summary>关窗:隐藏到托盘,不销毁,WebView 继续渲染。</summary>
    public void HideToBackground()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (IsVisible)
            Hide();
    }

    /// <summary>托盘「打开窗口」:只把已有窗口带回来,不重新导航。</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>
    /// 初始化 WebView2(首次)并等待 dsh 服务就绪后导航。
    /// 同一 session URL 只导航一次;dsh 重启换了 launch-token 才会再加载。
    /// 遮罩上同时显示一行环境检测(Node.js / npm / dsh 已安装还是未安装)。
    /// </summary>
    public async Task NavigateWhenReadyAsync()
    {
        if (_navigateInFlight) return;
        _navigateInFlight = true;
        try
        {
            await NavigateWhenReadyCoreAsync();
        }
        finally
        {
            _navigateInFlight = false;
        }
    }

    private async Task NavigateWhenReadyCoreAsync()
    {
        if (!await InitializeWebViewAsync())
            return;

        if (WebView.CoreWebView2 is not null
            && _manager.State == DshProcessManager.ServiceState.Running
            && string.Equals(_navigatedUrl, _manager.NavigateUrl, StringComparison.Ordinal))
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        StatusOverlay.Visibility = Visibility.Visible;
        StatusProgress.IsIndeterminate = true;
        StatusText.Text = "正在启动 dsh 服务…";
        var probe = ShellLogic.ProbeRuntime();
        Log.Info($"环境检测: {ShellLogic.FormatRuntimeSummary(probe)}");
        EnvStatusText.Text = ShellLogic.FormatRuntimeSummary(probe);

        var ready = await _manager.WaitReadyAsync(TimeSpan.FromSeconds(95));
        if (ready && WebView.CoreWebView2 is not null)
        {
            var target = _manager.NavigateUrl;
            if (string.Equals(_navigatedUrl, target, StringComparison.Ordinal))
            {
                StatusOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            StatusOverlay.Visibility = Visibility.Collapsed;
            Log.Info($"导航 WebView: {target}");
            WebView.CoreWebView2.Navigate(target);
            _navigatedUrl = target;
        }
        else
        {
            StatusProgress.IsIndeterminate = false;
            // 再探测一次(用户可能在等待期间装好了 Node),失败提示按原因区分
            probe = ShellLogic.ProbeRuntime();
            EnvStatusText.Text = ShellLogic.FormatRuntimeSummary(probe);
            if (!probe.NodeFound)
            {
                StatusText.Text = "未检测到 Node.js,无法在后台启动 dsh 服务。请安装 Node.js 后,从托盘菜单「重启 dsh 服务」重试。";
            }
            else if (DshProcessManager.PortOpen(_manager.Port))
            {
                StatusText.Text = $"端口 {_manager.Port} 已被占用,不会接管已有进程。请先结束占用该端口的程序,再从托盘菜单重启。";
            }
            else
            {
                StatusText.Text = "dsh 服务未能就绪,请查看日志 %USERPROFILE%\\.dsh\\desktop.log,或从托盘菜单重启。";
            }
        }
    }

    /// <summary>dsh 进入 Running 时刷新(含窗口隐藏:后台 WebView 也要换新 token)。</summary>
    public void ReloadWhenReadyAsync() => _ = NavigateWhenReadyAsync();

    private async Task<bool> InitializeWebViewAsync()
    {
        if (_webReady) return true;
        try
        {
            var env = await SharedEnvironment.GetAsync(_userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);
            WebViewSetup.Configure(WebView.CoreWebView2!, _userDataFolder);
            _webReady = true;
            ApplyWebViewBackground();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"WebView2 初始化失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>WebView2 加载遮罩的背景/文字颜色跟随系统深浅色,避免深色模式下白屏闪烁。</summary>
    private void ApplyWebViewBackground()
    {
        var dark = ThemeManager.IsSystemDarkMode();
        StatusOverlay.Background =
            new System.Windows.Media.SolidColorBrush(dark
                ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)
                : System.Windows.Media.Colors.White);
        StatusText.Foreground =
            new System.Windows.Media.SolidColorBrush(dark
                ? System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)
                : System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
    }
}
