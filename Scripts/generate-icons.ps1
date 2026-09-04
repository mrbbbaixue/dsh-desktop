# 生成应用图标:从源 SVG 渲染彩色/黑/白三套多尺寸 PNG,合并为 ICO。
# 黑/白版本在制作期固定烘焙(纯色替换渐变后渲染),运行时不再染色。
# 用法:
#   ./Scripts/generate-icons.ps1
#   ./Scripts/generate-icons.ps1 -SvgPath "D:\下载\deepseek.svg"
# 输出(覆盖写入 DshDesktop\Resources):
#   favicon.ico         彩色多尺寸(程序 exe / 窗口图标)
#   favicon-black.ico   纯黑多尺寸(浅色任务栏托盘图标)
#   favicon-white.ico   纯白多尺寸(深色任务栏托盘图标)
#   favicon.png         彩色 256(窗口标题栏)

param(
    [string]$SvgPath = (Join-Path $PSScriptRoot "deepseek.svg"),
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\DshDesktop\Resources")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if (-not (Test-Path $SvgPath)) { throw "找不到源 SVG: $SvgPath" }

# 源 SVG 纳入仓库(Scripts\deepseek.svg),后续构建不再依赖下载目录
$repoSvg = Join-Path $PSScriptRoot "deepseek.svg"
if ([System.IO.Path]::GetFullPath($SvgPath) -ne [System.IO.Path]::GetFullPath($repoSvg)) {
    Copy-Item $SvgPath $repoSvg -Force
    Write-Host "源 SVG 已同步到仓库: $repoSvg"
}

$work = Join-Path $env:TEMP ("dsh-icon-gen-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $proj = Join-Path $work "IconGen"
    New-Item -ItemType Directory -Path $proj | Out-Null

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Svg.Skia" Version="2.0.0" />
  </ItemGroup>
</Project>
'@ | Set-Content -Path (Join-Path $proj "IconGen.csproj") -Encoding utf8

    @'
using SkiaSharp;
using Svg.Skia;

var svgPath = Path.GetFullPath(Environment.GetCommandLineArgs()[1]);
var outDir = Path.GetFullPath(Environment.GetCommandLineArgs()[2]);
Directory.CreateDirectory(outDir);

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var source = File.ReadAllText(svgPath);

foreach (var (file, color) in new[]
{
    ("favicon.ico", (string?)null),
    ("favicon-black.ico", "#000000"),
    ("favicon-white.ico", "#FFFFFF"),
})
{
    var text = color is null ? source : Recolor(source, color);
    using var svg = new SKSvg();
    svg.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));

    // 彩色版本额外输出窗口标题栏用 favicon.png(256)
    if (color is null)
        File.WriteAllBytes(Path.Combine(outDir, "favicon.png"), RenderPng(svg, 256));

    var frames = new List<(int w, int h, byte[] png)>();
    foreach (var size in sizes)
        frames.Add((size, size, RenderPng(svg, size)));
    File.WriteAllBytes(Path.Combine(outDir, file), BuildIco(frames));
}

// 去除渐变定义,把填充替换成固定纯色:制作期烘焙黑/白版本
static string Recolor(string svg, string color)
{
    var text = System.Text.RegularExpressions.Regex.Replace(svg, "(?s)<defs.*?</defs>", "");
    text = text.Replace("style=\"fill:url(#linearGradient2)\"", $"style=\"fill:{color}\"");
    text = text.Replace("fill=\"#4D6BFE\"", $"fill=\"{color}\"");
    return text;
}

static byte[] RenderPng(SKSvg svg, int size)
{
    // 自绘:透明画布上按目标尺寸等比缩放绘制矢量
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKImageInfo.PlatformColorType, SKAlphaType.Unpremul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / svg.Picture!.CullRect.Width, size / svg.Picture.CullRect.Height);
    canvas.DrawPicture(svg.Picture);
    canvas.Flush();
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100)
        ?? throw new InvalidOperationException("PNG 编码失败");
    return data.ToArray();
}

// ICO 容器:所有帧用 PNG 编码(Vista+),256 尺寸以 0 表示
static byte[] BuildIco(List<(int w, int h, byte[] png)> frames)
{
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write((ushort)0);            // reserved
    bw.Write((ushort)1);            // type: icon
    bw.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var f in frames)
    {
        bw.Write((byte)(f.w >= 256 ? 0 : f.w));
        bw.Write((byte)(f.h >= 256 ? 0 : f.h));
        bw.Write((byte)0);          // palette
        bw.Write((byte)0);          // reserved
        bw.Write((ushort)1);        // planes
        bw.Write((ushort)32);       // bit count
        bw.Write(f.png.Length);
        bw.Write(offset);
        offset += f.png.Length;
    }
    foreach (var f in frames)
        bw.Write(f.png);
    return ms.ToArray();
}
'@ | Set-Content -Path (Join-Path $proj "Program.cs") -Encoding utf8

    dotnet run --project $proj -- $SvgPath $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "图标生成失败" }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

# 校验
$ico = [System.IO.File]::ReadAllBytes((Join-Path $OutputDir "favicon.ico"))
$count = [BitConverter]::ToUInt16($ico, 4)
Write-Host "已生成图标:" -ForegroundColor Green
Get-ChildItem $OutputDir -File | Where-Object { $_.Name -like "favicon*" } | ForEach-Object {
    Write-Host "  $($_.Name) ($($_.Length) 字节)"
}
Write-Host "  favicon.ico 共 $count 帧"