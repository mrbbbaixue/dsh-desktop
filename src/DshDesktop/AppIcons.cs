using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DshDesktop;

/// <summary>
/// 应用图标:嵌入资源 favicon.png。
/// 窗口用 WPF ImageSource;托盘用 WinForms Icon(调用方负责 DestroyIcon 释放 HICON)。
/// </summary>
internal static class AppIcons
{
    public static ImageSource? LoadWindowIcon()
    {
        using var stream = OpenPng();
        if (stream is null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static Icon? LoadNativeIcon()
    {
        using var stream = OpenPng();
        if (stream is null) return null;
        using var bmp = new Bitmap(stream);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Stream? OpenPng()
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("favicon.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }
}
