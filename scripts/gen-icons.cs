// Значок программы — щит цвета «Защищено» с белой галочкой, тот же, что в трее (TrayIconRenderer):
//   desktop/src/Client.UI/Themes/Icons.axaml (Icon.Shield, Icon.Mark.Ok) + design-tokens.json (state.connected.ring)
//     → shared/specifications/icons/app.svg — Linux: значок записи .desktop (hicolor/scalable/apps);
//     → shared/specifications/icons/app.ico — Windows: файл окна, ярлыки, «Установленные приложения» (MSI);
//     → shared/specifications/icons/app.icns — macOS: значок программы (Private Gateway.app, scripts/package-macos.sh).
//     → desktop/packaging/windows/banner.bmp, dialog.bmp — картинки мастера установки MSI в стиле программы: светлый фон
//       окна (color.bg.window), синяя панель (color.accent.base) и тот же щит; без них WiX рисует свои красные картинки.
//
// Запуск из корня репозитория (файловое приложение .NET 10, отдельного проекта нет):
//   dotnet run scripts/gen-icons.cs
//
// Руками значки не правятся. В шапку app.svg пишется SHA-256 источников: тест UI сверяет его с Icons.axaml и токенами
// и падает, если щит или цвет поменяли, а значки не пересобрали.

#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Linux@3.119.4
#:property ManagePackageVersionsCentrally=false

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SkiaSharp;

var root = FindRoot();
var icons = XDocument.Load(Path.Combine(root, "desktop", "src", "Client.UI", "Themes", "Icons.axaml"));
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
string Geometry(string key) => icons.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Value.Trim();
var shield = Geometry("Icon.Shield");
var mark = Geometry("Icon.Mark.Ok");
using var tokens = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "shared", "specifications", "design-tokens.json")));
var color = tokens.RootElement.GetProperty("state").GetProperty("connected").GetProperty("ring").GetString()!;
var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{shield}\n{mark}\n{color}")));

var output = Directory.CreateDirectory(Path.Combine(root, "shared", "specifications", "icons")).FullName;
File.WriteAllText(Path.Combine(output, "app.svg"), $"""
    <!-- Сгенерировано scripts/gen-icons.cs из Icons.axaml и design-tokens.json; руками не править. icon-sha256: {hash} -->
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="256" height="256">
      <path d="{shield}" fill="{color}" stroke="{color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>
      <path d="{mark}" fill="none" stroke="#FFFFFF" stroke-width="2.6" stroke-linejoin="round" stroke-linecap="round"/>
    </svg>

    """.ReplaceLineEndings("\n"));

// ICO с картинками PNG (Windows Vista и новее): размеры, которые просят Проводник, панель задач и список программ.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = sizes.Select(size => Render(size, shield, mark, SKColor.Parse(color))).ToList();
using (var ico = new BinaryWriter(File.Create(Path.Combine(output, "app.ico"))))
{
    ico.Write((ushort)0);
    ico.Write((ushort)1);
    ico.Write((ushort)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((ushort)1);
        ico.Write((ushort)32);
        ico.Write(images[i].Length);
        ico.Write(offset);
        offset += images[i].Length;
    }

    images.ForEach(ico.Write);
}

// ICNS с картинками PNG (macOS 10.7 и новее): тип записи → размер в точках; длины — big-endian, как в формате.
(string Type, int Size)[] icns = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
    ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)];
var entries = icns.Select(e => (e.Type, Png: Render(e.Size, shield, mark, SKColor.Parse(color)))).ToList();
using (var file = new BinaryWriter(File.Create(Path.Combine(output, "app.icns"))))
{
    static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    file.Write("icns"u8);
    file.Write(BigEndian(8 + entries.Sum(e => 8 + e.Png.Length)));
    foreach (var (type, png) in entries)
    {
        file.Write(Encoding.ASCII.GetBytes(type));
        file.Write(BigEndian(8 + png.Length));
        file.Write(png);
    }
}

// Картинки мастера установки WiX (WixUI): полоса сверху 493×58 и боковая панель приветствия и итога 493×312 (текст
// мастера стоит справа от 164 точек). 24-битный BMP — формат, который принимает установщик Windows.
var accent = SKColor.Parse(tokens.RootElement.GetProperty("color").GetProperty("accent").GetProperty("base").GetString()!);
var window = SKColor.Parse(tokens.RootElement.GetProperty("color").GetProperty("bg").GetProperty("window").GetString()!);
var packaging = Path.Combine(root, "desktop", "packaging", "windows");
File.WriteAllBytes(Path.Combine(packaging, "banner.bmp"), Bitmap(493, 58, canvas =>
{
    canvas.Clear(SKColors.White);
    using var line = new SKPaint { Color = window };
    canvas.DrawRect(0, 56, 493, 2, line);
    DrawShield(canvas, shield, mark, accent, SKColors.White, 437, 7, 44);
}));
// Правая часть — цвет поверхности диалогов Windows (COLOR_BTNFACE, #F0F0F0), а не белый: флажок «Запустить Private Gateway»
// на последней странице MSI прозрачным не бывает и рисуется этим цветом поверх картинки — на белом он был серым
// прямоугольником (пользователь 2026-11-05). Тот же цвет у полосы кнопок внизу мастера, так что страница — одного тона.
var dialogFace = new SKColor(0xF0, 0xF0, 0xF0);
File.WriteAllBytes(Path.Combine(packaging, "dialog.bmp"), Bitmap(493, 312, canvas =>
{
    canvas.Clear(dialogFace);
    using var panel = new SKPaint
    {
        Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 312),
            [accent, new SKColor((byte)(accent.Red * 0.62), (byte)(accent.Green * 0.62), (byte)(accent.Blue * 0.62))], SKShaderTileMode.Clamp),
    };
    canvas.DrawRect(0, 0, 164, 312, panel);
    DrawShield(canvas, shield, mark, SKColors.White, accent, 32, 96, 100);
}));

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Значок: {output}/app.svg, app.ico ({string.Join(", ", sizes)} px) и app.icns ({string.Join(", ", icns.Select(e => e.Size).Distinct())} px), icon-sha256 {hash}"));

// Щит и галочка в квадрате size×size: геометрии — в сетке 24×24, как у значков окна.
static byte[] Render(int size, string shield, string mark, SKColor color)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 24f);
    using var shieldPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 2, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    using var markPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    using var shieldPath = SKPath.ParseSvgPathData(shield);
    using var markPath = SKPath.ParseSvgPathData(mark);
    canvas.DrawPath(shieldPath, shieldPaint);
    canvas.DrawPath(markPath, markPaint);
    using var image = surface.Snapshot();
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
}

// Щит цвета shieldColor с галочкой цвета markColor в квадрате size в точке (x, y).
static void DrawShield(SKCanvas canvas, string shield, string mark, SKColor shieldColor, SKColor markColor, float x, float y, float size)
{
    canvas.Save();
    canvas.Translate(x, y);
    canvas.Scale(size / 24f);
    using var shieldPaint = new SKPaint { Color = shieldColor, IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 2, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    using var markPaint = new SKPaint { Color = markColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    using var shieldPath = SKPath.ParseSvgPathData(shield);
    using var markPath = SKPath.ParseSvgPathData(mark);
    canvas.DrawPath(shieldPath, shieldPaint);
    canvas.DrawPath(markPath, markPaint);
    canvas.Restore();
}

// 24-битный BMP снизу вверх, строки выровнены по 4 байта.
static byte[] Bitmap(int width, int height, Action<SKCanvas> draw)
{
    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    draw(surface.Canvas);
    using var image = surface.Snapshot();
    using var pixels = image.PeekPixels();
    var source = pixels.GetPixelSpan();
    var stride = ((width * 3) + 3) & ~3;
    var size = 54 + (stride * height);
    using var stream = new MemoryStream(size);
    using var bmp = new BinaryWriter(stream);
    bmp.Write("BM"u8);
    bmp.Write(size);
    bmp.Write(0);
    bmp.Write(54);
    bmp.Write(40);
    bmp.Write(width);
    bmp.Write(height);
    bmp.Write((ushort)1);
    bmp.Write((ushort)24);
    bmp.Write(0);
    bmp.Write(stride * height);
    bmp.Write(2835);
    bmp.Write(2835);
    bmp.Write(0);
    bmp.Write(0);
    var row = new byte[stride];
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var at = ((y * width) + x) * 4;
            row[x * 3] = source[at];
            row[(x * 3) + 1] = source[at + 1];
            row[(x * 3) + 2] = source[at + 2];
        }

        bmp.Write(row);
    }

    return stream.ToArray();
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx): запускать из него.");
}
