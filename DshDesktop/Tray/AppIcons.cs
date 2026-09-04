using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DshDesktop.Tray;

/// <summary>
/// 应用图标:嵌入资源(彩色 favicon.png、黑/白 favicon-*.ico)。
/// 窗口标题栏用原色 WPF ImageSource;托盘与任务栏大图标用制作期烘焙的
/// 固定黑/白多尺寸 ICO(浅色任务栏 → favicon-black.ico,深色任务栏 → favicon-white.ico),
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

    /// <summary>单色图标:浅色任务栏用固定黑色 ICO,深色任务栏用固定白色 ICO(托盘与任务栏共用)。</summary>
    public static Icon? LoadMonoIcon(bool lightTaskbar)
    {
        using var stream = OpenResource(lightTaskbar ? "favicon-black.ico" : "favicon-white.ico");
        if (stream is null) return null;
        return new Icon(stream);
    }

    /// <summary>单色图标的多尺寸帧渲染为 WPF ImageSource(主窗口任务栏大图标用,128 帧保证高 DPI 清晰)。</summary>
    public static ImageSource? LoadMonoIconSource(bool lightTaskbar)
    {
        using var stream = OpenResource(lightTaskbar ? "favicon-black.ico" : "favicon-white.ico");
        if (stream is null) return null;
        using var icon = new Icon(stream, 128, 128);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Stream? OpenResource(string fileName)
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }
}
