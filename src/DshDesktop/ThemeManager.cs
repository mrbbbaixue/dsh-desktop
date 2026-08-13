using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DshDesktop;

/// <summary>
/// 系统原生标题栏深浅色(仅跟随系统主题,无手动开关)。
/// 通过 DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE) 切换,
/// 系统主题切换由 MainWindow 的 WM_SETTINGCHANGE(ImmersiveColorSet) 监听驱动。
/// </summary>
internal static class ThemeManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>读取系统深浅色主题(AppsUseLightTheme: 0 = 深色, 1 = 浅色)。</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch
        {
            return false;
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
