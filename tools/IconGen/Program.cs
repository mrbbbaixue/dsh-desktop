using Svg.Skia;
using SkiaSharp;

// 用法: dotnet run --project tools/IconGen -- <input.svg> <outputDir>
// 渲染 16/24/32/48/64/128/256 PNG,并拼成多尺寸 ICO (Vista+ PNG-in-ICO),
// 另输出 favicon.png (64x64) 复制到 src/DshDesktop/Resources/ 作为嵌入资源。
var svgPath = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

var svgText = File.ReadAllText(svgPath);
using var svg = new SKSvg();
svg.FromSvg(svgText);
var bounds = svg.Picture?.CullRect ?? throw new InvalidOperationException("SVG 解析失败");

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var images = new List<(byte[] Png, int Size)>();

foreach (var size in sizes)
{
    using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    var scale = size / Math.Max(bounds.Width, bounds.Height);
    canvas.Scale(scale);
    canvas.Translate((size / scale - bounds.Width) / 2f, (size / scale - bounds.Height) / 2f);
    canvas.DrawPicture(svg.Picture);
    canvas.Flush();

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    var png = data.ToArray();
    File.WriteAllBytes(Path.Combine(outDir, $"favicon-{size}.png"), png);
    images.Add((png, size));
}

// 64x64 作为嵌入式 favicon.png(托盘/窗口共用)
File.Copy(Path.Combine(outDir, "favicon-64.png"), Path.Combine(outDir, "favicon.png"), overwrite: true);

// 多尺寸 ICO: ICONDIR + N × ICONDIRENTRY + PNG 数据
using var fs = File.Create(Path.Combine(outDir, "favicon.ico"));
using var bw = new BinaryWriter(fs);
bw.Write((ushort)0);           // reserved
bw.Write((ushort)1);           // type = icon
bw.Write((ushort)images.Count);
uint offset = (uint)(6 + 16 * images.Count);
foreach (var (png, size) in images)
{
    bw.Write((byte)(size >= 256 ? 0 : size)); // 0 = 256
    bw.Write((byte)(size >= 256 ? 0 : size));
    bw.Write((byte)0);          // color count
    bw.Write((byte)0);          // reserved
    bw.Write((ushort)1);        // planes
    bw.Write((ushort)32);       // bit count
    bw.Write((uint)png.Length);
    bw.Write(offset);
    offset += (uint)png.Length;
}
foreach (var (png, _) in images) bw.Write(png);

Console.WriteLine($"OK: {images.Count} sizes -> {Path.Combine(outDir, "favicon.ico")}");
