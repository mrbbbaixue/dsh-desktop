using System.Globalization;
using System.IO;
using System.Text;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 窗口状态记忆:%USERPROFILE%\.dsh\desktop.ini。
/// .NET 无内置 INI 解析器,文件采用极简 key=value 文本(INI 子集),
/// 用原生 File API 读写,不依赖第三方库;缺文件或内容损坏时保留默认值。
/// </summary>
public sealed class WindowPrefs
{
    /// <summary>与 MainWindow.xaml 的默认尺寸保持一致。</summary>
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 840;

    private readonly string _path;

    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    public bool Maximized { get; set; }

    public WindowPrefs(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "desktop.ini");
    }

    /// <summary>读取配置;文件不存在时返回 false(保持默认值)。坏行跳过,不影响其余键。</summary>
    public bool TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                var sep = line.IndexOf('=');
                if (sep <= 0) continue;
                var key = line.Substring(0, sep).Trim();
                var value = line.Substring(sep + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "width":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
                            Width = w;
                        break;
                    case "height":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0)
                            Height = h;
                        break;
                    case "maximized":
                        if (bool.TryParse(value, out var m))
                            Maximized = m;
                        break;
                }
            }
            return true;
        }
        catch
        {
            return false; // 读失败(占用/权限)不干扰启动
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(_path,
            [
                "width=" + Width.ToString("R", CultureInfo.InvariantCulture),
                "height=" + Height.ToString("R", CultureInfo.InvariantCulture),
                "maximized=" + (Maximized ? "true" : "false"),
            ], new UTF8Encoding(false));
        }
        catch
        {
            // 写失败静默,窗口状态记忆绝不影响主流程
        }
    }
}
