using System.Text.RegularExpressions;

namespace DshDesktop.WebView;

/// <summary>
/// 市场「立即重启」请求的判定与响应构造(纯策略,不碰 WebView2 与网络,规则由单测钉住)。
///
/// 背景:dsh-market 安装/升级插件后,对不能热挂载的改动会给出「立即重启」按钮,它自己的实现是
/// 「SIGTERM 当前 dsh + 一个脱离终端的帮手重放原命令拉起替代进程」(dsh-market src/restart.ts)。
/// 那条路绕开本壳的进程托管:替代进程不在我们的跟踪范围,还会和壳"子进程退出即自动重启"
/// 抢同一个端口——要么市场那边 EADDRINUSE 秒死(只写进 %TEMP% 日志),要么壳落到 Failed
/// 并丢掉控制台绑定与 launch-token。所以壳在 WebView 层接管这个请求,改由
/// <see cref="DshDesktop.Services.DshProcessManager.RestartAsync"/> 重启自己的子进程。
///
/// 市场 UI 侧不需要任何改动:按钮照旧可点,发起后它照旧轮询 /dsh-market/status 等 boot 变化再
/// reload,而重启后的 dsh 会给出新的 boot。
/// </summary>
internal static class MarketRestartPolicy
{
    /// <summary>市场 UI「立即重启」按钮打的路径(legacy)。</summary>
    internal const string LegacyPath = "/dsh-market/restart";

    /// <summary>对外更新 API 的同名接口;它内部转调 legacy 路由,不拦就会漏一次市场自重启。</summary>
    internal const string ApiV1Path = "/dsh-market/api/v1/restart";

    /// <summary>需要接管的两个路径。</summary>
    internal static readonly string[] InterceptedPaths = [LegacyPath, ApiV1Path];

    /// <summary>v1 响应信封,与市场 update-api/v1 保持一致。</summary>
    internal const string UpdateApiV1Schema = "dsh-market/update-api/v1";

    /// <summary>市场自己的 409 文案:有插件操作在跑(客户端会对 409 每 1.5s 重试,最多 10 次)。</summary>
    internal const string BusyMessage = "cannot restart while a plugin operation is running";

    /// <summary>市场自己的 409 文案:已经在重启中(对应它的 restarting 守卫)。</summary>
    internal const string AlreadyMessage = "restart already scheduled";

    /// <summary>
    /// 一次请求是不是市场的重启动作:必须是 POST 到两个已知路径之一,且来自我们托管的那个 origin。
    /// 只认 POST 是刻意的——非 POST 交给 dsh 自己回 405(市场路由第一件事就是拒非 POST),
    /// 不会把"方法写错"当成一次重启。查询串忽略,尾斜杠不认(服务端也认不出)。
    /// </summary>
    internal static bool IsRestartRequest(string? method, string? uri, string? origin)
    {
        if (!string.Equals(method, "POST", StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (!IsInterceptedPath(parsed.AbsolutePath)) return false;
        // 过滤器本身已按 origin 限定;给得出 origin 时再核一次,避免策略被别处复用时越界。
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return string.Equals(parsed.GetLeftPart(UriPartial.Authority), origin!.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>响应是否要套 v1 信封。</summary>
    internal static bool IsApiV1(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.AbsolutePath, ApiV1Path, StringComparison.Ordinal);

    /// <summary>
    /// 市场 status 载荷里是否有插件操作在跑——等价于市场自己那条 409 守卫的可观察部分。
    /// busy 是路由级操作锁(安装退出后仍持有到后处理结束),active 覆盖 pnpm 运行期;
    /// 两者任一为 true 就不动进程。纯 writing(补丁/主题写入)是亚秒级操作,status 不外露,
    /// 这一层挡不住,但安装中途杀 dsh 会留下半写入的 profile,正是这里要防的。
    /// </summary>
    internal static bool StatusBusy(string? statusJson) =>
        !string.IsNullOrEmpty(statusJson) && BusyField.IsMatch(statusJson!);

    /// <summary>status 里的 busy / active 布尔字段。</summary>
    private static readonly Regex BusyField = new(
        @"""(?:busy|active)""\s*:\s*true",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>接管成功的 202 响应体。市场客户端只认 <c>ok:true</c>,managedBy 是给日志/排查看的。</summary>
    internal static string AcceptedBody(string? uri)
    {
        const string inner = "\"ok\":true,\"managedBy\":\"desktop-shell\"";
        return IsApiV1(uri)
            ? "{\"schema\":\"" + UpdateApiV1Schema + "\"," + inner + ",\"result\":{" + inner + "}}"
            : "{" + inner + "}";
    }

    /// <summary>错误响应体(409/500),沿用市场自己的文案与信封。</summary>
    internal static string ErrorBody(string message, string? uri)
    {
        var inner = "\"error\":\"" + Escape(message) + "\"";
        return IsApiV1(uri)
            ? "{\"schema\":\"" + UpdateApiV1Schema + "\"," + inner + "}"
            : "{" + inner + "}";
    }

    private static bool IsInterceptedPath(string path) =>
        Array.IndexOf(InterceptedPaths, path) >= 0;

    /// <summary>消息来自本类常量,仍需转义:以后有人塞进异常消息也不会拼出坏 JSON。</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
