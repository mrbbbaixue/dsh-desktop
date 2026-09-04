using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;

namespace DshDesktop.Tray;

/// <summary>
/// 应用图标:嵌入资源 favicon.png。
/// 窗口用原色 WPF ImageSource;托盘按任务栏深浅染色成黑/白 WinForms Icon
/// (调用方负责 DestroyIcon 释放 HICON)。
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

    /// <summary>
    /// 托盘图标:浅色任务栏 → 黑色鲸鱼,深色任务栏 → 白色鲸鱼。
    /// 保留原图 alpha,只替换 RGB,抗锯齿边缘不会糊成色块。
    /// </summary>
    public static Icon? LoadTrayIcon(bool lightTaskbar)
    {
        using var stream = OpenPng();
        if (stream is null) return null;
        using var src = new Bitmap(stream);
        using var tinted = Recolor(src, lightTaskbar ? DrawingColor.Black : DrawingColor.White);
        return Icon.FromHandle(tinted.GetHicon());
    }

    /// <summary>保留 alpha,把所有像素的 RGB 换成指定颜色。</summary>
    private static Bitmap Recolor(Bitmap src, DrawingColor color)
    {
        var dst = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        using var attrs = new ImageAttributes();
        float r = color.R / 255f, green = color.G / 255f, b = color.B / 255f;
        attrs.SetColorMatrix(new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [r, green, b, 0, 1],
        ]));
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    private static Stream? OpenPng()
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("favicon.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }
}
