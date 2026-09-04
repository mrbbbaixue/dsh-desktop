using System;
using System.IO;
using DshDesktop.Infrastructure;
using Xunit;

namespace DshDesktop.Tests.Infrastructure;

public class WindowPrefsTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "dsh-windowprefs-" + Guid.NewGuid().ToString("N") + ".ini");

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
    public void TryLoad_RespectsCommentsAndBlankLines()
    {
        var path = TempPath();
        try
        {
            File.WriteAllLines(path,
            [
                "; 注释行",
                "# 注释行2",
                "",
                "width=900",
                "height=600",
                "maximized=false",
            ]);
            var prefs = new WindowPrefs(path);
            Assert.True(prefs.TryLoad());
            Assert.Equal(900, prefs.Width);
            Assert.Equal(600, prefs.Height);
            Assert.False(prefs.Maximized);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_CorruptLines_SkipAndKeepDefaults()
    {
        var path = TempPath();
        try
        {
            File.WriteAllLines(path, ["width=not-a-number", "garbage", "height=720"]);
            var prefs = new WindowPrefs(path);
            Assert.True(prefs.TryLoad());
            Assert.Equal(WindowPrefs.DefaultWidth, prefs.Width); // 坏行保留默认
            Assert.Equal(720, prefs.Height);                     // 好行照常生效
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
        var path = Path.Combine(dir, "nested", "desktop.ini");
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
