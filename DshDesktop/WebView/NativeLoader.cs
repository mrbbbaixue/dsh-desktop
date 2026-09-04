using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop.WebView;

/// <summary>
/// WebView2Loader.dll 是原生库。Costura 嵌托管 DLL 后,SDK 用
/// Core 程序集 Location 去拼 loader 路径;Location 为空会直接报错。
/// Costura CreateTemporaryAssemblies 把 Core 解到临时目录后,再把 loader 抽到旁边。
/// 必须在任何 WebView2 API 之前调用。
/// </summary>
internal static class NativeLoader
{
    public static void Ensure()
    {
        var coreDir = Path.GetDirectoryName(typeof(CoreWebView2Environment).Assembly.Location);
        if (string.IsNullOrEmpty(coreDir))
            coreDir = AppDomain.CurrentDomain.BaseDirectory;

        Directory.CreateDirectory(coreDir);
        var dest = Path.Combine(coreDir, "WebView2Loader.dll");
        if (File.Exists(dest))
            return;
        try
        {
            ExtractEmbedded(dest);
        }
        catch when (File.Exists(dest))
        {
            /* 已抽出且可能被占用 */
        }
    }

    private static void ExtractEmbedded(string dest)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = OpenLoaderStream(asm)
            ?? throw new InvalidOperationException("嵌入资源中没有 WebView2Loader.dll");
        var tmp = dest + ".tmp";
        using (var output = File.Create(tmp))
            src.CopyTo(output);
        File.Copy(tmp, dest, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    private static Stream? OpenLoaderStream(Assembly asm)
    {
        var raw = asm.GetManifestResourceStream("WebView2Loader.dll");
        if (raw is not null)
            return raw;
        var compressed = asm.GetManifestResourceStream("costura.webview2loader.dll.compressed");
        if (compressed is null)
            return null;
        return new DeflateStream(compressed, CompressionMode.Decompress);
    }
}
