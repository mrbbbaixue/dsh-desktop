using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop.WebView;

/// <summary>
/// 统一的 WebView2 接线:设置 + 权限 + 下载 + 弹窗 + 崩溃自愈。
/// 主窗口与插件弹出的内部窗口共用,保证行为一致。
/// </summary>
internal static class WebViewSetup
{
    /// <summary>渲染进程崩溃自动重载的节流时间戳(避免崩溃死循环,主窗口与弹窗共享)。</summary>
    private static long _lastReloadTick;

    public static void Configure(CoreWebView2 core, string userDataFolder)
    {
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = true;   // 保留右键菜单(复制/粘贴等)
        settings.AreDevToolsEnabled = true;              // 保留 F12(仅实际打开时才占用内存)
        settings.IsGeneralAutofillEnabled = false;       // 关闭表单自动填充,减少后台开销
        settings.IsPasswordAutosaveEnabled = false;      // 不保存密码

        // 权限:自动放行插件/DSH 依赖的能力(见 ShellLogic.IsAutoGrantedPermission),
        // 其余保持默认拒绝。麦克风/摄像头默认拒绝(隐私)。
        core.PermissionRequested += (_, e) =>
        {
            if (ShellLogic.IsAutoGrantedPermission(e.PermissionKind))
                e.State = CoreWebView2PermissionState.Allow;
        };

        // 下载:固定保存到系统"下载"文件夹(自动避开同名文件),完成后用默认程序打开
        core.DownloadStarting += (_, e) =>
        {
            try
            {
                var downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var name = ShellLogic.SanitizeFileName(ShellLogic.SuggestDownloadName(
                    e.DownloadOperation.ContentDisposition, e.DownloadOperation.Uri, e.DownloadOperation.MimeType));
                var path = Path.Combine(downloads, name);
                for (var i = 1; File.Exists(path); i++)
                    path = Path.Combine(downloads,
                        $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                e.Handled = true;   // 禁用 WebView2 默认下载对话框
                e.ResultFilePath = path;
                e.DownloadOperation.StateChanged += (_, _) =>
                {
                    if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(e.DownloadOperation.ResultFilePath) { UseShellExecute = true });
                        }
                        catch { /* 无默认程序打开时忽略 */ }
                    }
                };
            }
            catch { /* 处理失败时回退 WebView2 默认下载行为 */ }
        };

        // 弹窗策略(分类逻辑见 ShellLogic.ClassifyPopup):
        // - 外部 http(s) 链接 → 系统默认浏览器
        // - 同源 http(s) 弹窗 → 新建轻量壳窗口(保留会话,避免主窗口被导航走)
        // - blob: / data: / about: 等 → WebView2 默认行为(插件生成的预览等)
        core.NewWindowRequested += async (_, e) =>
        {
            switch (ShellLogic.ClassifyPopup(e.Uri))
            {
                case ShellLogic.PopupTarget.External:
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                    return;
                case ShellLogic.PopupTarget.Internal:
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        var popup = new PopupWindow(userDataFolder);
                        await popup.InitializeAsync();
                        e.NewWindow = popup.Web.CoreWebView2;
                        popup.Show();
                    }
                    finally { deferral.Complete(); }
                    return;
                }
                default:
                    return;
            }
        };

        // 渲染进程崩溃/无响应:自动重载避免白屏(每 10 秒最多一次,防止崩溃死循环)
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                var now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref _lastReloadTick) > 10_000 * TimeSpan.TicksPerMillisecond)
                {
                    Interlocked.Exchange(ref _lastReloadTick, now);
                    try { core.Reload(); } catch { }
                }
            }
        };
    }
}
