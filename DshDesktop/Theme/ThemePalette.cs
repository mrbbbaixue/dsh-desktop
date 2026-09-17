// WinForms 的隐式 using 会带进 System.Drawing,Color 必须显式指向 WPF 的
using Color = System.Windows.Media.Color;

namespace DshDesktop.Theme;

/// <summary>
/// 诊断窗口的深浅色配色。XAML 里的默认值就是浅色一套,
/// 运行时由窗口按当前系统主题整组替换(资源键不变,WPF 自动刷新)。
/// </summary>
internal sealed record ThemePalette(
    Color Background,
    Color Foreground,
    Color Muted,
    Color Border,
    Color Input,
    Color Hover,
    Color Error,
    Color Accent)
{
    /// <summary>浅色:白底深字。</summary>
    public static readonly ThemePalette Light = new(
        Background: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Foreground: Color.FromRgb(0x33, 0x33, 0x33),
        Muted: Color.FromRgb(0x77, 0x77, 0x77),
        Border: Color.FromRgb(0xDD, 0xDD, 0xDD),
        Input: Color.FromRgb(0xF7, 0xF7, 0xF7),
        Hover: Color.FromRgb(0xE9, 0xE9, 0xE9),
        Error: Color.FromRgb(0xC6, 0x28, 0x28),
        Accent: Color.FromRgb(0x2E, 0xA0, 0x43));

    /// <summary>深色:深底浅字,红色要用亮红才够对比(深红在深底上几乎看不清)。</summary>
    public static readonly ThemePalette Dark = new(
        Background: Color.FromRgb(0x20, 0x20, 0x20),
        Foreground: Color.FromRgb(0xDC, 0xDC, 0xDC),
        Muted: Color.FromRgb(0x9A, 0x9A, 0x9A),
        Border: Color.FromRgb(0x40, 0x40, 0x40),
        Input: Color.FromRgb(0x18, 0x18, 0x18),
        Hover: Color.FromRgb(0x33, 0x33, 0x33),
        Error: Color.FromRgb(0xFF, 0x7B, 0x72),
        Accent: Color.FromRgb(0x3F, 0xB9, 0x50));

    public static ThemePalette For(bool dark) => dark ? Dark : Light;
}
