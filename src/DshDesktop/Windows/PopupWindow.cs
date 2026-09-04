using System.ComponentModel;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshDesktop.Windows;

/// <summary>
/// 插件内部弹窗用的轻量窗口(与主窗口共享 WebView2 用户数据,保持登录态/会话)。
/// 纯代码构建,生命周期短,不进入 XAML。
/// </summary>
internal sealed class PopupWindow : Window
{
    public WebView2 Web { get; }

    private readonly string _userDataFolder;
    private bool _init;

    public PopupWindow(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        Title = "DeepSeek Harness";
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcons.LoadWindowIcon();
        Web = new WebView2 { Focusable = true };
        Content = Web;
    }

    /// <summary>初始化 WebView2 并接线(NewWindowRequested 流程中 await 后才设置 e.NewWindow)。</summary>
    public async Task InitializeAsync()
    {
        if (_init) return;
        _init = true;
        var env = await SharedEnvironment.GetAsync(_userDataFolder);
        await Web.EnsureCoreWebView2Async(env);
        WebViewSetup.Configure(Web.CoreWebView2!, _userDataFolder);
        Web.CoreWebView2!.DocumentTitleChanged += (_, _) =>
        {
            var title = Web.CoreWebView2.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(title)) Title = title;
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try { Web.Dispose(); } catch { /* ignore */ }
        base.OnClosing(e);
    }
}
