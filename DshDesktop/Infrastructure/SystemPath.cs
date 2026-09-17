using Microsoft.Win32;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 取「当前最新的」PATH:进程启动之后系统 PATH 可能已被改动
/// (Node MSI 写的是机器级 PATH,刚装好的 node/npm/dsh 要立刻能用),
/// 所以每次都从注册表重读机器级与用户级的 Path,与进程 PATH 合并去重。
/// </summary>
internal static class SystemPath
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = @"Environment";

    /// <summary>注册表(机器级 + 用户级)与进程 PATH 的合并结果,已展开环境变量、去重、去空项。</summary>
    public static string Current()
    {
        var parts = new List<string>();
        Append(parts, ReadRegistry(Registry.LocalMachine, MachineKey));
        Append(parts, ReadRegistry(Registry.CurrentUser, UserKey));
        Append(parts, Environment.GetEnvironmentVariable("PATH"));
        return string.Join(";", parts);
    }

    private static string? ReadRegistry(RegistryKey root, string subKey)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue("Path") as string;
        }
        catch
        {
            return null; // 读不到就只用进程 PATH
        }
    }

    private static void Append(List<string> parts, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var item in Environment.ExpandEnvironmentVariables(raw).Split(';'))
        {
            var dir = item.Trim();
            if (dir.Length == 0) continue;
            if (parts.Exists(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase))) continue;
            parts.Add(dir);
        }
    }
}
