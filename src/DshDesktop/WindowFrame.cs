using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DshDesktop;

/// <summary>
/// 无边框窗口(WindowChrome + DWM 玻璃扩展)的 DWM 外观辅助。
/// 帧本身(右上角标题栏按钮、阴影、边框)由 DWM 绘制,这里只处理圆角等细节。
/// </summary>
internal static class WindowFrame
{
    // DwmSetWindowAttribute 属性(仅 Windows 11 22000+ 支持圆角偏好,旧系统调用返回错误,忽略即可)
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 关闭 Win11 自动圆角(直角)。
    /// WebView2 是独立子 HWND,直角可避免其方形内容与 DWM 圆角帧之间出现拼缝/杂色;
    /// 网页全屏沉浸(无边框 + 盖住标题栏)下直角也更干净。Win10 上是空操作。
    /// </summary>
    public static void ApplySquareCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var preference = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
