using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshDesktop.Windows;

/// <summary>
/// 主窗口:WebView2 填充 + 系统原生标题栏(深浅色跟随系统)。
/// WebView2 首次显示时才初始化(懒加载),避免开机自启 --minimized 时白占内存。
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly DshProcessManager _manager;
    private bool _webReady;

    public MainWindow(string url, DshProcessManager manager)
    {
        InitializeComponent();
        _url = url;
        _manager = manager;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshDesktop", "WebView2");

        Icon = AppIcons.LoadWindowIcon();
        SourceInitialized += (_, _) =>
        {
            ThemeManager.Apply(this);
            HookSystemThemeChange();
        };
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

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await NavigateWhenReadyAsync();
    }

    /// <summary>
    /// 初始化 WebView2(首次)并等待 dsh 服务就绪后导航。
    /// 幂等:重启服务后再次调用即重新加载页面。
    /// 遮罩上同时显示一行环境检测(Node.js / npm / dsh 已安装还是未安装),
    /// 未检测到 Node.js 时给出明确指引,不再只显示笼统的"未能就绪"。
    /// </summary>
    public async Task NavigateWhenReadyAsync()
    {
        if (!await InitializeWebViewAsync())
            return;

        StatusOverlay.Visibility = Visibility.Visible;
        StatusProgress.IsIndeterminate = true;
        StatusText.Text = "正在启动 dsh 服务…";
        var probe = ShellLogic.ProbeRuntime();
        Log.Info($"环境检测: {ShellLogic.FormatRuntimeSummary(probe)}");
        EnvStatusText.Text = ShellLogic.FormatRuntimeSummary(probe);

        var ready = await _manager.WaitReadyAsync(TimeSpan.FromSeconds(95));
        if (ready && WebView.CoreWebView2 is not null)
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            WebView.CoreWebView2.Navigate(_url);
        }
        else
        {
            StatusProgress.IsIndeterminate = false;
            // 再探测一次(用户可能在等待期间装好了 Node),失败提示按原因区分
            probe = ShellLogic.ProbeRuntime();
            EnvStatusText.Text = ShellLogic.FormatRuntimeSummary(probe);
            if (!probe.NodeFound)
            {
                StatusText.Text = "未检测到 Node.js,无法在后台启动 dsh 服务。请安装 Node.js 后,从托盘菜单「启动 dsh 服务」重试。";
            }
            else if (DshProcessManager.PortOpen(_manager.Port))
            {
                StatusText.Text = $"端口 {_manager.Port} 已被占用,不会接管已有进程。请先结束占用该端口的程序,再从托盘菜单重试。";
            }
            else
            {
                StatusText.Text = "dsh 服务未能就绪,请查看日志 %USERPROFILE%\\.dsh-desktop.log,或从托盘菜单重试。";
            }
        }
    }

    /// <summary>服务就绪事件触发的刷新(窗口可见时才动作,避免隐藏窗口上无谓导航)。</summary>
    public void ReloadWhenReadyAsync()
    {
        if (IsVisible)
            _ = NavigateWhenReadyAsync();
    }

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
