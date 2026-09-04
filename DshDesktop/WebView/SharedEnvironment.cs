using Microsoft.Web.WebView2.Core;

namespace DshDesktop.WebView;

/// <summary>
/// 共享 WebView2 环境:主窗口与插件弹窗共用同一用户数据目录与浏览器参数,
/// 保证会话/登录态互通。AdditionalBrowserArguments 放行无手势自动播放:
/// WebView2 当前 SDK 不会为 Autoplay 触发 PermissionRequested(直接静默拒绝),
/// --autoplay-policy=no-user-gesture-required 是唯一可用的开关(声音类插件依赖)。
/// 同时显式绕过代理访问本机回环地址与 DSH 外部地址,避免本地直连被系统代理拦走。
/// </summary>
internal static class SharedEnvironment
{
    private static CoreWebView2Environment? _instance;

    public static async Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        if (_instance is null)
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Join(" ",
                    "--autoplay-policy=no-user-gesture-required",
                    "--proxy-bypass-list=" + ProxyBypassList()),
            };
            _instance = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        return _instance;
    }

    /// <summary>
    /// 代理旁路列表(Chromium --proxy-bypass-list 语法,逗号分隔):
    /// 回环/本机地址一律直连 —— 本应用访问的是 127.0.0.1 上的 dsh 服务,
    /// 若走系统代理(如公司代理、Clash 等)会导致连接失败或绕道;
    /// 顺带放行常见内网/容器主机名,避免内部资源被代理。公有域名不在此列,照常走代理。
    /// </summary>
    private static string ProxyBypassList() =>
        "<local>,127.0.0.1,localhost,[::1]";
}
