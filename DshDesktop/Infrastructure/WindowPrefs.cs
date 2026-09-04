using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 桌面壳持久化配置:%USERPROFILE%\.dsh\desktop.xml。
/// 存储格式用框架原生内置的 XML(XmlSerializer 自动映射 public 属性,
/// 后续新增字段只需加属性)。读失败/损坏时保留默认值,写失败静默。
/// </summary>
[XmlRoot("desktop")]
public sealed class WindowPrefs
{
    /// <summary>与 MainWindow.xaml 的默认尺寸保持一致。</summary>
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 840;

    private static readonly XmlSerializer Serializer = new(typeof(WindowPrefs));

    private readonly string _path;

    [XmlElement("width")]
    public double Width { get; set; } = DefaultWidth;

    [XmlElement("height")]
    public double Height { get; set; } = DefaultHeight;

    [XmlElement("maximized")]
    public bool Maximized { get; set; }

    /// <summary>供 XmlSerializer 反序列化使用;序列化输出不包含路径。</summary>
    public WindowPrefs() : this((string?)null) { }

    public WindowPrefs(string? path)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "desktop.xml");
    }

    /// <summary>读取配置;文件缺失/损坏时返回 false(保持默认值)。</summary>
    public bool TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            using var stream = File.OpenRead(_path);
            var loaded = (WindowPrefs?)Serializer.Deserialize(stream);
            if (loaded is null) return false;
            Width = loaded.Width;
            Height = loaded.Height;
            Maximized = loaded.Maximized;
            return true;
        }
        catch
        {
            return false; // 反序列化失败(损坏/版本差异)不干扰启动
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false),
            };
            using var writer = XmlWriter.Create(_path, settings);
            Serializer.Serialize(writer, this);
        }
        catch
        {
            // 写失败静默,配置持久化绝不影响主流程
        }
    }
}
