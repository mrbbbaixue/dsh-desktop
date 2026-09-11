using System.IO;
using System.Net;
using System.Text;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;

namespace DshDesktop.WebView;

/// <summary>
/// 把市场的一键重启接管到本壳(判定规则见 <see cref="MarketRestartPolicy"/>)。
///
/// 拦下 POST /dsh-market/restart(以及转调它的 /dsh-market/api/v1/restart),不转发给 dsh,
/// 改由 <see cref="DshProcessManager.RestartAsync"/> 重启我们自己托管的子进程,再照市场客户端的
/// 预期回 202 + {"ok":true}:页面继续轮询 /dsh-market/status,看到新 boot 后自行 reload
/// (壳同时会因为服务转为 Running 用新 launch-token 导航一次)。
///
/// 接管范围只限 WebView 里的请求:外部浏览器打开的 dsh 仍会走市场自重启。DSH_WEB_URL
/// 外部托管时不挂载——那时进程不归壳管,重启该由对方负责。
/// </summary>
internal sealed class MarketRestartInterceptor
{
    private readonly CoreWebView2 _core;
    private readonly DshProcessManager _manager;
    private readonly string _origin;
    private readonly Dispatcher _ui;
    private int _restarting;

    private MarketRestartInterceptor(CoreWebView2 core, DshProcessManager manager)
    {
        _core = core;
        _manager = manager;
        _origin = manager.Url.TrimEnd('/');
        // Configure 总在 UI 线程调用(EnsureCoreWebView2Async 之后),这里取到的是应用主 Dispatcher。
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// 挂上过滤器与事件。必须在首次导航之前调用:过滤器只对之后发起的请求生效。
    /// </summary>
    public static void Attach(CoreWebView2 core, DshProcessManager manager)
    {
        var interceptor = new MarketRestartInterceptor(core, manager);
        foreach (var path in MarketRestartPolicy.InterceptedPaths)
        {
            // 通配后缀是为了让带查询串的写法也落到这里;真判定在 IsRestartRequest,
            // 过滤器多匹配到的请求不设 Response 即照常发往网络。
            core.AddWebResourceRequestedFilter(interceptor._origin + path + "*",
                CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += interceptor.OnWebResourceRequested;
        Log.Info($"市场重启接管已挂载: {interceptor._origin}{MarketRestartPolicy.LegacyPath}");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (!MarketRestartPolicy.IsRestartRequest(e.Request.Method, uri, _origin))
            return;   // 不是市场的重启动作:不设 Response,请求照常发给 dsh

        // 探状态要发 HTTP,不能占着 UI 线程等;用 deferral 把这个请求挂住,拿定主意后在 UI 线程写响应。
        var deferral = e.GetDeferral();
        _ = HandleAsync(e, uri, deferral);
    }

    private async Task HandleAsync(CoreWebView2WebResourceRequestedEventArgs e, string uri,
        CoreWebView2Deferral deferral)
    {
        var (status, reason, body) = await DecideAsync(uri).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            try
            {
                e.Response = _core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(body)), status, reason,
                    "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
            }
            catch (Exception ex)
            {
                Log.Error($"写回市场重启响应失败: {ex.Message}");
            }
            finally
            {
                // 流交给响应对象后不再持有:WebView2 交付时才读,提前 Dispose 会读空。
                try { deferral.Complete(); } catch (Exception ex) { Log.Error($"结束市场重启响应失败: {ex.Message}"); }
            }
        });
    }

    private async Task<(int Status, string Reason, string Body)> DecideAsync(string uri)
    {
        // 市场同款 restarting:已在重启中就直接 409,不让第二次点击叠一次重启。
        if (Interlocked.Exchange(ref _restarting, 1) == 1)
            return (409, "Conflict", MarketRestartPolicy.ErrorBody(MarketRestartPolicy.AlreadyMessage, uri));
        try
        {
            // 安装中途杀掉 dsh 会留下半写入的 profile,市场自己也用这个状态挡 409。
            if (await Task.Run(ProbeBusy).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _restarting, 0);
                Log.Info("市场重启请求:有插件操作在进行,已挡下(409)");
                return (409, "Conflict", MarketRestartPolicy.ErrorBody(MarketRestartPolicy.BusyMessage, uri));
            }
            Log.Info("接管市场的一键重启:改由壳重启自己托管的 dsh 子进程");
            _ = RestartAsync();
            return (202, "Accepted", MarketRestartPolicy.AcceptedBody(uri));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _restarting, 0);
            Log.Error($"处理市场重启请求失败: {ex}");
            return (500, "Internal Server Error", MarketRestartPolicy.ErrorBody(ex.Message, uri));
        }
    }

    /// <summary>重启子进程,结束后放开守卫(重启本身不抛,异常在这里收口)。</summary>
    private async Task RestartAsync()
    {
        try { await _manager.RestartAsync(); }
        catch (Exception ex) { Log.Error($"接管市场重启失败: {ex}"); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }

    /// <summary>
    /// 只读看一眼市场在不忙。读不到(服务已停、市场没装、超时)按"不忙"处理:
    /// 那种情况下这个请求本来也到不了 dsh,而用户此刻要的多半正是重启。
    /// </summary>
    private bool ProbeBusy()
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(_origin + "/dsh-market/status");
            request.Method = "GET";
            request.Proxy = null;   // 本地直连,绕过系统代理
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            request.AllowAutoRedirect = false;
            using var response = (HttpWebResponse)request.GetResponse();
            using var stream = response.GetResponseStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return MarketRestartPolicy.StatusBusy(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Info($"读取市场状态失败(按不忙处理): {ex.Message}");
            return false;
        }
    }
}
