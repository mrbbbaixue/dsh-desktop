using System.IO;
using Microsoft.Win32;

namespace DshDesktop;

/// <summary>
/// 开机自启:当前用户 HKCU Run 键,写入 "exe路径 --minimized"(登录后静默启动,不弹窗口)。
/// 仅当前用户生效,无需管理员权限。
/// </summary>
internal static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DshDesktop";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string v
                && v.Contains("DshDesktop", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath)
            ?? throw new InvalidOperationException("无法打开 Run 注册表键");
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "DshDesktop.exe");
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
