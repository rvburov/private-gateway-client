// Генератор темы Avalonia из токенов макета:
//   shared/specifications/design-tokens.json → desktop/src/Client.UI/Themes/Tokens.axaml
//
// Запуск из корня репозитория (файловое приложение .NET 10, отдельного проекта нет):
//   dotnet run scripts/gen-tokens.cs
//
// Tokens.axaml руками не правится. В шапку пишется SHA-256 файла токенов: тест UI сверяет его
// с текущим design-tokens.json и падает, если токены поменяли, а тему не пересобрали.
//
// Имена ресурсов: путь в JSON с заглавных букв через точку и префикс вида —
//   color.bg.window      → PgColor.Bg.Window (Color) и PgBrush.Bg.Window (SolidColorBrush);
//   type.h1.size         → PgFontSize.H1, PgFontWeight.H1, PgLetterSpacing.H1, PgLineHeight.H1;
//   space.card           → PgSpace.Card (Double) и PgPad.Card (Thickness);
//   radius.card          → PgRadius.Card (CornerRadius);
//   shadow.card          → PgShadow.Card (BoxShadows);
//   layout.island.height → PgLayout.Island.Height (Double).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = FindRoot();
var tokensPath = Path.Combine(root, "shared", "specifications", "design-tokens.json");
var outputPath = Path.Combine(root, "desktop", "src", "Client.UI", "Themes", "Tokens.axaml");

var bytes = File.ReadAllBytes(tokensPath);
var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
using var doc = JsonDocument.Parse(bytes);
var tokens = doc.RootElement;

var sb = new StringBuilder();
sb.AppendLine("<!--");
sb.AppendLine("  Сгенерировано scripts/gen-tokens.cs из shared/specifications/design-tokens.json.");
sb.AppendLine("  Руками не править: поменять токены и запустить «dotnet run scripts/gen-tokens.cs».");
sb.AppendLine(CultureInfo.InvariantCulture, $"  tokens-sha256: {hash}");
sb.AppendLine("-->");
sb.AppendLine("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"");
sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");

Section("Цвета", () =>
{
    foreach (var (key, value) in Leaves(tokens.GetProperty("color"), ""))
    {
        Color(key, value.GetString()!);
    }
});

Section("Состояния защиты", () =>
{
    foreach (var (key, value) in Leaves(tokens.GetProperty("state"), "State"))
    {
        if (!key.EndsWith(".Label", StringComparison.Ordinal))
        {
            Color(key, value.GetString()!);
        }
    }
});

Section("Стекло", () =>
{
    var glass = tokens.GetProperty("glass");
    Color("Glass.Fill", glass.GetProperty("fill").GetString()!);
    Color("Glass.Fallback", glass.GetProperty("fallback").GetString()!);
    Double("PgGlass.Blur", glass.GetProperty("blur").GetDouble());
});

Section("Шрифты (десктоп)", () =>
{
    var desktop = tokens.GetProperty("font").GetProperty("desktop");
    Line($"<FontFamily x:Key=\"PgFont.Ui\">{FontUri(desktop.GetProperty("ui").GetString()!)}</FontFamily>");
    Line($"<FontFamily x:Key=\"PgFont.Mono\">{FontUri(desktop.GetProperty("mono").GetString()!)}</FontFamily>");
});

Section("Типографика", () =>
{
    foreach (var style in tokens.GetProperty("type").EnumerateObject())
    {
        var name = Pascal(style.Name);
        var size = style.Value.GetProperty("size").GetDouble();
        Double($"PgFontSize.{name}", size);
        Line($"<FontWeight x:Key=\"PgFontWeight.{name}\">{Weight(style.Value.GetProperty("weight").GetInt32())}</FontWeight>");
        if (style.Value.TryGetProperty("weightActive", out var active))
        {
            Line($"<FontWeight x:Key=\"PgFontWeight.{name}.Active\">{Weight(active.GetInt32())}</FontWeight>");
        }

        if (style.Value.TryGetProperty("tracking", out var tracking))
        {
            Double($"PgLetterSpacing.{name}", Math.Round(tracking.GetDouble() * size, 2));
        }

        if (style.Value.TryGetProperty("lineHeight", out var lineHeight))
        {
            Double($"PgLineHeight.{name}", Math.Round(lineHeight.GetDouble() * size, 2));
        }
    }
});

Section("Отступы", () =>
{
    foreach (var space in tokens.GetProperty("space").EnumerateObject())
    {
        var value = space.Value.GetDouble();
        Double($"PgSpace.{Pascal(space.Name)}", value);
        Line($"<Thickness x:Key=\"PgPad.{Pascal(space.Name)}\">{Num(value)}</Thickness>");
    }
});

Section("Скругления", () =>
{
    foreach (var radius in tokens.GetProperty("radius").EnumerateObject())
    {
        if (radius.Value.ValueKind == JsonValueKind.Number)
        {
            Line($"<CornerRadius x:Key=\"PgRadius.{Pascal(radius.Name)}\">{Num(radius.Value.GetDouble())}</CornerRadius>");
        }
    }
});

Section("Тени", () =>
{
    foreach (var shadow in tokens.GetProperty("shadow").EnumerateObject())
    {
        var parts = shadow.Value.EnumerateArray().Select(s => Shadow(s.GetString()!));
        Line($"<BoxShadows x:Key=\"PgShadow.{Pascal(shadow.Name)}\">{string.Join(", ", parts)}</BoxShadows>");
    }
});

Section("Размеры", () =>
{
    foreach (var (key, value) in Leaves(tokens.GetProperty("layout"), ""))
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            Double($"PgLayout.{key}", value.GetDouble());
        }
    }

    // Толщина кольца кнопки-щита нужна и как Thickness — рамке Border/Button число не подходит.
    var ring = tokens.GetProperty("layout").GetProperty("powerButton").GetProperty("ringWidth").GetDouble();
    Line($"<Thickness x:Key=\"PgThickness.PowerButton.Ring\">{Num(ring)}</Thickness>");
});

sb.AppendLine("</ResourceDictionary>");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var text = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
File.WriteAllText(outputPath, text, new UTF8Encoding(false));
Console.WriteLine($"Записано: {Path.GetRelativePath(root, outputPath)} (tokens-sha256 {hash[..12]}…)");
return 0;

void Section(string title, Action body)
{
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"  <!-- {title} -->");
    body();
}

void Line(string xml) => sb.AppendLine("  " + xml);

void Double(string key, double value) => Line($"<x:Double x:Key=\"{key}\">{Num(value)}</x:Double>");

void Color(string key, string css)
{
    Line($"<Color x:Key=\"PgColor.{key}\">{Argb(css)}</Color>");
    Line($"<SolidColorBrush x:Key=\"PgBrush.{key}\" Color=\"{{StaticResource PgColor.{key}}}\" />");
}

static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

// Листья вложенного объекта: путь с заглавных букв через точку.
static IEnumerable<(string Key, JsonElement Value)> Leaves(JsonElement element, string prefix)
{
    foreach (var property in element.EnumerateObject())
    {
        var key = prefix.Length == 0 ? Pascal(property.Name) : prefix + "." + Pascal(property.Name);
        if (property.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var leaf in Leaves(property.Value, key))
            {
                yield return leaf;
            }
        }
        else
        {
            yield return (key, property.Value);
        }
    }
}

// «#RRGGBB» или «rgba(r, g, b, a)» → «#AARRGGBB».
static string Argb(string css)
{
    var hex = Regex.Match(css, "^#([0-9A-Fa-f]{6})$");
    if (hex.Success)
    {
        return "#FF" + hex.Groups[1].Value.ToUpperInvariant();
    }

    var rgba = Regex.Match(css, @"^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)$");
    if (!rgba.Success)
    {
        throw new FormatException($"Неизвестный формат цвета: {css}");
    }

    // Как в CSS: 0.70 × 255 = 178.5 → 179 (не банковское округление к четному).
    var alpha = (int)Math.Round(double.Parse(rgba.Groups[4].Value, CultureInfo.InvariantCulture) * 255, MidpointRounding.AwayFromZero);
    return string.Create(CultureInfo.InvariantCulture,
        $"#{alpha:X2}{int.Parse(rgba.Groups[1].Value, CultureInfo.InvariantCulture):X2}{int.Parse(rgba.Groups[2].Value, CultureInfo.InvariantCulture):X2}{int.Parse(rgba.Groups[3].Value, CultureInfo.InvariantCulture):X2}");
}

// «0 8 24 rgba(15, 23, 42, 0.06)» → «0 8 24 0 #0F0F172A» (смещение X, Y, размытие, растяжение, цвет).
static string Shadow(string css)
{
    var m = Regex.Match(css, @"^(-?\d+)\s+(-?\d+)\s+(\d+)\s+(rgba\(.+\))$");
    if (!m.Success)
    {
        throw new FormatException($"Неизвестный формат тени: {css}");
    }

    return $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} 0 {Argb(m.Groups[4].Value)}";
}

static string Weight(int weight) => weight switch
{
    400 => "Normal",
    500 => "Medium",
    600 => "SemiBold",
    700 => "Bold",
    _ => throw new ArgumentOutOfRangeException(nameof(weight), weight, "Нет имени для начертания"),
};

// Шрифты десктопа встроены: Inter — пакет Avalonia.Fonts.Inter, JetBrains Mono — Assets/Fonts.
static string FontUri(string family) => family switch
{
    "Inter" => "fonts:Inter#Inter",
    "JetBrains Mono" => "avares://PrivateGateway/Assets/Fonts#JetBrains Mono",
    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Шрифт не встроен в приложение"),
};

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Запускать из репозитория: не найден PrivateGateway.slnx");
}
