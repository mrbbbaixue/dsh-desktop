using DshDesktop.Theme;
using Xunit;

namespace DshDesktop.Tests.Theme;

public class ThemePaletteTests
{
    [Fact]
    public void For_PicksMatchingPalette()
    {
        Assert.Same(ThemePalette.Dark, ThemePalette.For(dark: true));
        Assert.Same(ThemePalette.Light, ThemePalette.For(dark: false));
    }

    [Fact]
    public void BackgroundAndForeground_AreInvertedBetweenThemes()
    {
        Assert.True(Luminance(ThemePalette.Light.Background) > Luminance(ThemePalette.Light.Foreground));
        Assert.True(Luminance(ThemePalette.Dark.Background) < Luminance(ThemePalette.Dark.Foreground));
    }

    /// <summary>深红落在深色底上几乎看不见,错误色必须比所在底色更亮。</summary>
    [Fact]
    public void ErrorColor_StaysReadableInBothThemes()
    {
        Assert.True(Luminance(ThemePalette.Dark.Error) > Luminance(ThemePalette.Dark.Background));
        Assert.True(Luminance(ThemePalette.Light.Error) < Luminance(ThemePalette.Light.Background));
    }

    [Fact]
    public void Surfaces_DifferFromEachOther()
    {
        Assert.NotEqual(ThemePalette.Light.Background, ThemePalette.Light.Input);
        Assert.NotEqual(ThemePalette.Dark.Background, ThemePalette.Dark.Input);
        Assert.NotEqual(ThemePalette.Light.Border, ThemePalette.Light.Background);
        Assert.NotEqual(ThemePalette.Dark.Border, ThemePalette.Dark.Background);
    }

    private static double Luminance(System.Windows.Media.Color c) =>
        0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
}
