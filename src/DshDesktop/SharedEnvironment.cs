using Microsoft.Web.WebView2.Core;

namespace DshDesktop;

/// <summary>
/// 共享 WebView2 环境:主窗口与插件弹窗共用同一用户数据目录与浏览器参数,
/// 保证会话/登录态互通。AdditionalBrowserArguments 放行无手势自动播放:
/// WebView2 当前 SDK 不会为 Autoplay 触发 PermissionRequested(直接静默拒绝),
/// --autoplay-policy=no-user-gesture-required 是唯一可用的开关(声音类插件依赖)。
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
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            _instance = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        return _instance;
    }
}
