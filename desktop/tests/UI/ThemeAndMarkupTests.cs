using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
        var tokens = File.ReadAllBytes(Path.Combine(Repo.Root, "shared", "specifications", "design-tokens.json"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(tokens));
        var theme = File.ReadAllText(Path.Combine(Repo.UiSource, "Themes", "Tokens.axaml"));

        theme.ShouldContain("tokens-sha256: " + hash,
            customMessage: "Токены поменялись, а тема нет: dotnet run scripts/gen-tokens.cs");
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
