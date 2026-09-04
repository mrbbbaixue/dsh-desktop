using System;
using System.IO;
using DshDesktop.Infrastructure;
using Xunit;

namespace DshDesktop.Tests.Infrastructure;

public class WindowPrefsTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "dsh-windowprefs-" + Guid.NewGuid().ToString("N") + ".xml");

    [Fact]
    public void TryLoad_MissingFile_KeepsDefaults()
    {
        var prefs = new WindowPrefs(TempPath());
        Assert.False(prefs.TryLoad());
        Assert.Equal(WindowPrefs.DefaultWidth, prefs.Width);
        Assert.Equal(WindowPrefs.DefaultHeight, prefs.Height);
        Assert.False(prefs.Maximized);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var path = TempPath();
        try
        {
            var saved = new WindowPrefs(path)
            {
                Width = 1024.5,
                Height = 768.25,
                Maximized = true,
            };
            saved.Save();

            var loaded = new WindowPrefs(path);
            Assert.True(loaded.TryLoad());
            Assert.Equal(1024.5, loaded.Width);
            Assert.Equal(768.25, loaded.Height);
            Assert.True(loaded.Maximized);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesXmlRootElement()
    {
        var path = TempPath();
        try
        {
            new WindowPrefs(path).Save();
            var doc = new System.Xml.XmlDocument();
            doc.Load(path);
            Assert.Equal("desktop", doc.DocumentElement!.Name);
            Assert.NotNull(doc.DocumentElement.SelectSingleNode("width"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_CorruptXml_KeepsDefaults()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "<desktop><width>这不是数字");
            var prefs = new WindowPrefs(path);
            Assert.False(prefs.TryLoad());
            Assert.Equal(WindowPrefs.DefaultWidth, prefs.Width);
            Assert.Equal(WindowPrefs.DefaultHeight, prefs.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_MissingElements_UsesDefaults()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "<desktop><width>900</width></desktop>");
            var prefs = new WindowPrefs(path);
            Assert.True(prefs.TryLoad());
            Assert.Equal(900, prefs.Width);
            Assert.Equal(WindowPrefs.DefaultHeight, prefs.Height);
            Assert.False(prefs.Maximized);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-windowprefs-dir-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "desktop.xml");
        try
        {
            new WindowPrefs(path).Save();
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}