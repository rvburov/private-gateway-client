using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using PrivateGateway.Client.UI.Controls;

namespace PrivateGateway.Client.Tests.UI;

public partial class ThemeAndMarkupTests
{
    [Fact]
    public void Tokens_axaml_is_generated_from_current_design_tokens()
    {
        // Как в генераторе: переводы строк — LF, иначе на Windows (CRLF при checkout) хеш другой.
        var tokens = File.ReadAllText(Path.Combine(Repo.Root, "shared", "specifications", "design-tokens.json"))
            .ReplaceLineEndings("\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokens)));
        var theme = File.ReadAllText(Path.Combine(Repo.UiSource, "Themes", "Tokens.axaml"));

        theme.ShouldContain("tokens-sha256: " + hash,
            customMessage: "Токены поменялись, а тема нет: dotnet run scripts/gen-tokens.cs");
    }

    [Fact]
    public void App_icon_is_generated_from_the_current_shield_and_color()
    {
        // Как в scripts/gen-icons.cs: щит, галочка и цвет «Защищено» — источники значка.
        var icons = XDocument.Load(Path.Combine(Repo.UiSource, "Themes", "Icons.axaml"));
        string Geometry(string key) => icons.Descendants().Single(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == key).Value.Trim();
        using var tokens = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repo.Root, "shared", "specifications", "design-tokens.json")));
        var color = tokens.RootElement.GetProperty("state").GetProperty("connected").GetProperty("ring").GetString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Geometry("Icon.Shield")}\n{Geometry("Icon.Mark.Ok")}\n{color}")));
        var folder = Path.Combine(Repo.Root, "shared", "specifications", "icons");

        File.ReadAllText(Path.Combine(folder, "app.svg")).ShouldContain("icon-sha256: " + hash,
            customMessage: "Щит или цвет поменялись, а значок нет: dotnet run scripts/gen-icons.cs");
        var ico = File.ReadAllBytes(Path.Combine(folder, "app.ico"));
        (BitConverter.ToUInt16(ico, 2), BitConverter.ToUInt16(ico, 4)).ShouldBe(((ushort)1, (ushort)9), "ICO с девятью размерами");
        Enumerable.Range(0, 9).Select(i => ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)]).ShouldBe([16, 20, 24, 32, 40, 48, 64, 128, 256]);
        XDocument.Load(Path.Combine(Repo.UiSource, "Client.UI.csproj")).Descendants("ApplicationIcon").Single().Value
            .ShouldEndWith("shared/specifications/icons/app.ico", customMessage: "у файла окна — тот же значок");
    }

    [Fact]
    public void Views_take_colors_only_from_the_theme()
    {
        var offenders = Directory.EnumerateFiles(Repo.UiSource, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(Path.Combine("Themes", "Tokens.axaml"), StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => HexColor().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToArray();

        offenders.ShouldBeEmpty("цвет в разметке — только ресурс из Tokens.axaml");
    }

    [Fact]
    public void Views_are_vector_only_and_stay_sharp_at_any_scale()
    {
        // Значки — геометрии из Icons.axaml, растровых картинок в разметке нет: при 125–200 % все четкое.
        var raster = Directory.EnumerateFiles(Repo.UiSource, "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("<Image", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        raster.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void Theme_resources_resolve_with_token_values()
    {
        var app = Avalonia.Application.Current!;
        app.TryGetResource("PgColor.State.Connected.Ring", ThemeVariant.Light, out var ring).ShouldBeTrue();
        ring.ShouldBe(Color.Parse("#16A34A"));
        app.TryGetResource("PgColor.Bg.Glass", ThemeVariant.Light, out var glass).ShouldBeTrue();
        ((Color)glass!).A.ShouldBe((byte)179); // 0.70 × 255
        app.TryGetResource("PgShadow.Card", ThemeVariant.Light, out var shadow).ShouldBeTrue();
        ((BoxShadows)shadow!).Count.ShouldBe(2);
    }

    [Fact]
    public void Island_outline_matches_the_mockup_for_desktop_width()
    {
        // Контур из docs/design/mockup/Main.dc.html для окна 440 и полей 24.
        IslandGeometry.Path(440, 24).ShouldBe(
            "M50 70 H156.5 A18 18 0 0 0 173.82 56.91 A48 48 0 0 1 266.18 56.91 A18 18 0 0 0 283.5 70 " +
            "H390 A26 26 0 0 1 416 96 V112 A26 26 0 0 1 390 138 H50 A26 26 0 0 1 24 112 V96 A26 26 0 0 1 50 70 Z");
    }

    [GeneratedRegex("#[0-9A-Fa-f]{3,8}\\b")]
    private static partial Regex HexColor();
}
