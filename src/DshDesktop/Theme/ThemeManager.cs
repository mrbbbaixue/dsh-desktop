using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DshDesktop.Theme;

/// <summary>
/// 系统深浅色:原生标题栏跟随应用主题,托盘图标跟随任务栏主题。
/// 无手动开关。窗口侧由 MainWindow 的 WM_SETTINGCHANGE(ImmersiveColorSet) 驱动;
/// 托盘侧由 TrayIcon 订阅 SystemEvents.UserPreferenceChanged 驱动(关窗藏托盘时也能切)。
/// </summary>
internal static class ThemeManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>应用深浅色(AppsUseLightTheme: 0 = 深色, 1 = 浅色)。供标题栏/等待遮罩使用。</summary>
    public static bool IsSystemDarkMode() => ReadPersonalize("AppsUseLightTheme") == 0;

    /// <summary>
    /// 任务栏是否浅色(SystemUsesLightTheme: 1 = 浅色)。
    /// 托盘图标落在任务栏上,必须跟这个值而不是应用主题
    /// (用户可以「深色任务栏 + 浅色应用」分开设置)。
    /// </summary>
    public static bool IsTaskbarLight() => ReadPersonalize("SystemUsesLightTheme") == 1;

    private static int? ReadPersonalize(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(valueName) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把窗口标题栏切换为当前系统主题对应的深浅色。</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsSystemDarkMode() ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
