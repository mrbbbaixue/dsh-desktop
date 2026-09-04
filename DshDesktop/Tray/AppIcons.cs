using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DshDesktop.Tray;

/// <summary>
/// 应用图标:嵌入资源(彩色 favicon.png、黑/白 favicon-*.ico)。
/// 窗口用原色 WPF ImageSource;托盘用制作期烘焙的固定黑/白多尺寸 ICO
/// (浅色任务栏 → favicon-black.ico,深色任务栏 → favicon-white.ico),
/// 调用方负责 DestroyIcon 释放 HICON。
/// </summary>
internal static class AppIcons
{
    public static ImageSource? LoadWindowIcon()
    {
        using var stream = OpenResource("favicon.png");
        if (stream is null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>托盘图标:浅色任务栏用固定黑色 ICO,深色任务栏用固定白色 ICO。</summary>
    public static Icon? LoadTrayIcon(bool lightTaskbar)
    {
        using var stream = OpenResource(lightTaskbar ? "favicon-black.ico" : "favicon-white.ico");
        if (stream is null) return null;
        return new Icon(stream);
    }

    private static Stream? OpenResource(string fileName)
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }
}
