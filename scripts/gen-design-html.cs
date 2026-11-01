// Генератор основы HTML-макета (docs/design/html) из тех же источников, что у программы:
//   shared/specifications/design-tokens.json     → docs/design/html/assets/tokens.css (переменные CSS)
//   desktop/src/Client.UI/Themes/Icons.axaml     → docs/design/html/assets/icons.js   (контуры значков 24×24)
//
// Запуск из корня репозитория (файловое приложение .NET 10, отдельного проекта нет):
//   dotnet run scripts/gen-design-html.cs
//
// Оба файла руками не правятся. В шапку пишется SHA-256 источника: тест UI сверяет его и падает, если токены или
// значки поменяли, а макет не пересобрали — макет не расходится с программой.
//
// Имена переменных: путь в JSON через дефис с префиксом --pg- —
//   color.bg.window        → --pg-color-bg-window
//   state.connected.ring   → --pg-state-connected-ring
//   type.h1.size           → --pg-type-h1-size (px), .weight, .tracking (em), .line-height
//   space.card             → --pg-space-card (px);  radius.card → --pg-radius-card (px)
//   shadow.card            → --pg-shadow-card (box-shadow);  layout.island.height → --pg-layout-island-height (px)

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = FindRoot();
var tokensPath = Path.Combine(root, "shared", "specifications", "design-tokens.json");
var iconsPath = Path.Combine(root, "desktop", "src", "Client.UI", "Themes", "Icons.axaml");
var assets = Path.Combine(root, "docs", "design", "html", "assets");
Directory.CreateDirectory(assets);

// Хеш — от текста с переводами строк LF: на Windows git отдает файлы с CRLF, а источники те же.
var tokensText = File.ReadAllText(tokensPath).ReplaceLineEndings("\n");
var tokensHash = Hash(tokensText);
using var doc = JsonDocument.Parse(tokensText);
var tokens = doc.RootElement;

var css = new StringBuilder();
css.AppendLine("/*");
css.AppendLine("  Сгенерировано scripts/gen-design-html.cs из shared/specifications/design-tokens.json.");
css.AppendLine("  Руками не править: поменять токены и запустить «dotnet run scripts/gen-design-html.cs».");
css.AppendLine(CultureInfo.InvariantCulture, $"  tokens-sha256: {tokensHash}");
css.AppendLine("*/");
css.AppendLine(":root {");
foreach (var section in new[] { "color", "state", "type", "space", "radius", "shadow", "glass", "layout" })
{
    css.AppendLine(CultureInfo.InvariantCulture, $"  /* {section} */");
    foreach (var (path, value) in Leaves(tokens.GetProperty(section), section))
    {
        if (CssValue(path, value) is { } cssValue)
        {
            css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-{path}: {cssValue};");
        }
    }
}

// Шрифты платформ: десктоп — встроенные Inter и JetBrains Mono (assets/fonts), телефоны — системные (как в программе).
var fonts = tokens.GetProperty("font");
css.AppendLine("  /* font */");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-desktop-ui: \"{fonts.GetProperty("desktop").GetProperty("ui").GetString()}\", system-ui, sans-serif;");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-desktop-mono: \"{fonts.GetProperty("desktop").GetProperty("mono").GetString()}\", ui-monospace, monospace;");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-android-ui: \"{fonts.GetProperty("android").GetProperty("ui").GetString()}\", \"Inter\", system-ui, sans-serif;");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-android-mono: \"{fonts.GetProperty("android").GetProperty("mono").GetString()}\", \"JetBrains Mono\", monospace;");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-ios-ui: -apple-system, \"{fonts.GetProperty("ios").GetProperty("ui").GetString()} Text\", \"Inter\", system-ui, sans-serif;");
css.AppendLine(CultureInfo.InvariantCulture, $"  --pg-font-ios-mono: ui-monospace, \"{fonts.GetProperty("ios").GetProperty("mono").GetString()}\", \"JetBrains Mono\", monospace;");
css.AppendLine("}");
Write(Path.Combine(assets, "tokens.css"), css.ToString());

// Значки: те же контуры, что у Icon программы, — ключ без «Icon.» → путь SVG.
var iconsText = File.ReadAllText(iconsPath).ReplaceLineEndings("\n");
var icons = new StringBuilder();
icons.AppendLine("// Сгенерировано scripts/gen-design-html.cs из desktop/src/Client.UI/Themes/Icons.axaml.");
icons.AppendLine("// Руками не править: поменять Icons.axaml и запустить «dotnet run scripts/gen-design-html.cs».");
icons.AppendLine(CultureInfo.InvariantCulture, $"// icons-sha256: {Hash(iconsText)}");
icons.AppendLine("window.PG_ICONS = {");
var count = 0;
foreach (Match m in Regex.Matches(iconsText, "<StreamGeometry x:Key=\"Icon\\.([^\"]+)\">([^<]+)</StreamGeometry>"))
{
    icons.AppendLine(CultureInfo.InvariantCulture, $"  \"{m.Groups[1].Value}\": \"{m.Groups[2].Value.Trim()}\",");
    count++;
}

icons.AppendLine("};");
Write(Path.Combine(assets, "icons.js"), icons.ToString());

Console.WriteLine($"Записано: docs/design/html/assets/tokens.css (tokens-sha256 {tokensHash[..12]}…), icons.js ({count} значков)");
return 0;

static string? CssValue(string path, JsonElement value)
{
    if (path.StartsWith("state.", StringComparison.Ordinal) && path.EndsWith(".label", StringComparison.Ordinal))
    {
        return null;
    }

    if (value.ValueKind == JsonValueKind.Array)
    {
        return string.Join(", ", value.EnumerateArray().Select(s => Shadow(s.GetString()!)));
    }

    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
    {
        return null;
    }

    if (value.ValueKind == JsonValueKind.String)
    {
        var text = value.GetString()!;
        return path.StartsWith("layout-island-nav-columns", StringComparison.Ordinal) || text.EndsWith('%')
            || text.StartsWith('#') || text.StartsWith("rgba(", StringComparison.Ordinal) ? text : null;
    }

    var number = value.GetDouble();
    return path.EndsWith("-weight", StringComparison.Ordinal) || path.EndsWith("-weight-active", StringComparison.Ordinal)
        || path.EndsWith("-saturate", StringComparison.Ordinal) || path.EndsWith("-line-height", StringComparison.Ordinal)
        ? Num(number)
        : path.EndsWith("-tracking", StringComparison.Ordinal) ? Num(number) + "em" : Num(number) + "px";
}

// «0 8 24 rgba(15, 23, 42, 0.06)» → «0 8px 24px rgba(15, 23, 42, 0.06)».
static string Shadow(string css)
{
    var m = Regex.Match(css, @"^(-?\d+)\s+(-?\d+)\s+(\d+)\s+(rgba\(.+\))$");
    if (!m.Success)
    {
        throw new FormatException($"Неизвестный формат тени: {css}");
    }

    return $"{Px(m.Groups[1].Value)} {Px(m.Groups[2].Value)} {Px(m.Groups[3].Value)} {m.Groups[4].Value}";
}

static string Px(string number) => number == "0" ? "0" : number + "px";

static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

// Листья вложенного объекта: путь через дефис, имена из camelCase — в kebab-case.
static IEnumerable<(string Path, JsonElement Value)> Leaves(JsonElement element, string prefix)
{
    foreach (var property in element.EnumerateObject())
    {
        var path = prefix + "-" + Kebab(property.Name);
        if (property.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var leaf in Leaves(property.Value, path))
            {
                yield return leaf;
            }
        }
        else
        {
            yield return (path, property.Value);
        }
    }
}

static string Kebab(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();

static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

static void Write(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

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
