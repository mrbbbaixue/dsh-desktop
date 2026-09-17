using Microsoft.Web.WebView2.Core;

namespace DshDesktop.Services;

/// <summary>诊断项。</summary>
internal enum ToolKind
{
    Node,
    Npm,
    WebView2,
    Dsh,
}

/// <summary>单项诊断结果:Status 是给人看的一行状态,Detail 是路径或下一步提示。</summary>
internal sealed record EnvItem(ToolKind Kind, string Name, bool Ok, string Status, string? Detail);

/// <summary>一次完整诊断的结果。</summary>
internal sealed record EnvReport(IReadOnlyList<EnvItem> Items)
{
    public bool NodeReady => IsOk(ToolKind.Node) && IsOk(ToolKind.Npm);
    public bool DshReady => IsOk(ToolKind.Dsh);

    /// <summary>一键安装是否还有活要干(Node/npm/dsh 三者均已就绪时无需安装)。</summary>
    public bool NeedsInstall => !NodeReady || !DshReady;

    public bool IsOk(ToolKind kind) => Items.Any(i => i.Kind == kind && i.Ok);

    public string Summary => NeedsInstall ? "缺少必需组件" : "环境完整";
}

/// <summary>
/// 本机工具链诊断:Node.js / npm / WebView2 Runtime / dsh。
/// 探测都是"失败即视为未就绪",不抛异常 —— 诊断本身绝不能让窗口打不开。
/// dsh 服务状态不在这里诊断:壳自己拉起它,拿到启动链接就算成功,与工具链是否装好无关。
/// </summary>
internal static class EnvProbe
{
    public static EnvReport Run()
    {
        var items = new List<EnvItem>
        {
            ProbeNode(),
            ProbeNpm(),
            ProbeWebView2(),
            ProbeDsh(),
        };
        foreach (var item in items)
            Log.Info($"诊断: {item.Name} — {item.Status}{(item.Detail is null ? "" : $" ({item.Detail})")}");
        return new EnvReport(items);
    }

    /// <summary>"已安装 v24.19.0" / "已安装" / "未安装";版本读不到时只报已安装。</summary>
    internal static string Describe(bool found, string? version) =>
        !found ? "未安装" : string.IsNullOrWhiteSpace(version) ? "已安装" : $"已安装 {version!.Trim()}";

    private static EnvItem ProbeNode()
    {
        var version = ShellLogic.RunForOutput("node", "--version");
        var found = !string.IsNullOrWhiteSpace(version);
        return new EnvItem(
            ToolKind.Node, "Node.js", found, Describe(found, version),
            found ? ShellLogic.WherePath("node") : "缺少 Node.js,无法在后台启动 dsh 服务");
    }

    private static EnvItem ProbeNpm()
    {
        var version = ShellLogic.RunForOutput("npm.cmd", "--version");
        var found = !string.IsNullOrWhiteSpace(version) && ShellLogic.CommandExists("npx");
        return new EnvItem(
            ToolKind.Npm, "npm", found, Describe(found, version),
            found
                ? ShellLogic.WherePath("npm.cmd")
                : "npm/npx 随 Node.js 一起安装,重装 Node.js 可修复");
    }

    private static EnvItem ProbeWebView2()
    {
        try
        {
            NativeLoader.Ensure();
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return new EnvItem(ToolKind.WebView2, "WebView2 Runtime", true, Describe(true, version), "系统已安装");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return new EnvItem(ToolKind.WebView2, "WebView2 Runtime", false, "未安装",
                "缺少 WebView2 Runtime,程序无法显示页面");
        }
        catch (Exception ex)
        {
            return new EnvItem(ToolKind.WebView2, "WebView2 Runtime", false, "检测失败", ex.Message);
        }
    }

    private static EnvItem ProbeDsh()
    {
        // dsh 是批处理包装(.cmd),版本读不到时只要命令在就算已安装
        var version = ShellLogic.RunForOutput("dsh.cmd", "--version");
        var path = ShellLogic.WherePath("dsh");
        var found = !string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(version);
        return new EnvItem(
            ToolKind.Dsh, "dsh", found, Describe(found, version),
            found ? path : "未安装,将自动用 npx 临时启动(首次较慢)");
    }
}
