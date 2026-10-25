using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Контраст текста по WCAG 2.1, уровень AA (не меньше 4,5 : 1) — для пар «текст на фоне», которые есть
/// в окне. Полупрозрачный фон считается поверх белой карточки, полупрозрачный текст — поверх фона.
/// </summary>
public sealed partial class ContrastTests
{
    private const double AA = 4.5;

    private static readonly JsonElement Tokens =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "shared", "specifications", "design-tokens.json"))).RootElement;

    [Theory]
    [InlineData("color.text.primary", "color.bg.window")]
    [InlineData("color.text.secondary", "color.bg.window")]
    [InlineData("color.text.muted", "color.bg.window")]
    [InlineData("color.text.primary", "color.surface.solid")]
    [InlineData("color.text.secondary", "color.surface.solid")]
    [InlineData("color.text.muted", "color.surface.solid")]
    [InlineData("color.accent.text", "color.surface.solid")]
    [InlineData("color.accent.text", "color.accent.tint")]
    [InlineData("color.status.success.badge", "color.status.success.tint")]
    [InlineData("color.status.warning.badge", "color.status.warning.tint")]
    [InlineData("color.status.caution.badge", "color.status.caution.tint")]
    [InlineData("color.status.danger.badge", "color.status.danger.tint")]
    [InlineData("color.status.success.text", "color.surface.solid")]
    [InlineData("color.status.warning.text", "color.surface.solid")]
    [InlineData("color.status.caution.text", "color.surface.solid")]
    [InlineData("color.status.danger.text", "color.surface.solid")]
    [InlineData("color.surface.solid", "color.accent.base")]
    [InlineData("color.surface.solid", "color.tile.violet")]
    [InlineData("color.surface.solid", "color.tile.teal")]
    [InlineData("color.surface.solid", "color.tile.amber")]
    [InlineData("state.connected.icon", "color.surface.solid")]
    [InlineData("state.connecting.icon", "color.surface.solid")]
    [InlineData("state.blocked.icon", "color.surface.solid")]
    [InlineData("state.partial.icon", "color.surface.solid")]
    [InlineData("state.error.icon", "color.surface.solid")]
    [InlineData("state.off.icon", "color.surface.solid")]
    public void Text_meets_wcag_aa(string text, string background)
    {
        var back = Over(Color(background), White);
        var front = Over(Color(text), back);

        Ratio(front, back).ShouldBeGreaterThanOrEqualTo(AA, $"{text} на {background}");
    }

    [Fact]
    public void Ratio_matches_known_values()
    {
        Ratio(White, (0, 0, 0)).ShouldBe(21, 0.01);
        Ratio(White, White).ShouldBe(1, 0.01);
        Ratio((0x25, 0x63, 0xEB), White).ShouldBe(5.17, 0.01);
    }

    private static readonly (double R, double G, double B) White = (255, 255, 255);

    private static (double R, double G, double B, double A) Color(string path)
    {
        var node = Tokens;
        foreach (var key in path.Split('.'))
        {
            node = node.GetProperty(key);
        }

        var text = node.GetString()!;
        if (text.StartsWith('#'))
        {
            return (Hex(text, 1), Hex(text, 3), Hex(text, 5), 1);
        }

        var parts = Rgba().Match(text).Groups[1].Value.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return (parts[0], parts[1], parts[2], parts[3]);

        static double Hex(string hex, int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static (double R, double G, double B) Over((double R, double G, double B, double A) top, (double R, double G, double B) under) =>
        (top.R * top.A + under.R * (1 - top.A), top.G * top.A + under.G * (1 - top.A), top.B * top.A + under.B * (1 - top.A));

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance((double R, double G, double B) c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(double value)
    {
        var s = value / 255;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    [GeneratedRegex(@"rgba\(([^)]*)\)")]
    private static partial Regex Rgba();
}
