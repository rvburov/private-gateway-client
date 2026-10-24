using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace PrivateGateway.Client.UI.Controls;

public partial class IslandNav : UserControl
{
    public IslandNav()
    {
        InitializeComponent();
        Island.Effect = ShadowFromToken();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Island.Data = StreamGeometry.Parse(IslandGeometry.Path(e.NewSize.Width, Resource("PgLayout.Island.SideMargin")));
    }

    // Тень островка — из токена shadow.island (BoxShadows); Path умеет только эффект тени.
    private static DropShadowEffect? ShadowFromToken()
    {
        if (Avalonia.Application.Current?.TryGetResource("PgShadow.Island", ThemeVariant.Light, out var value) != true
            || value is not BoxShadows { Count: > 0 } shadows)
        {
            return null;
        }

        var shadow = shadows[0];
        return new DropShadowEffect
        {
            OffsetX = shadow.OffsetX,
            OffsetY = shadow.OffsetY,
            BlurRadius = shadow.Blur,
            Color = shadow.Color,
            Opacity = 1,
        };
    }

    private static double Resource(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, ThemeVariant.Light, out var value) == true && value is double d
            ? d
            : throw new KeyNotFoundException(key);
}

/// <summary>
/// Контур островка, как в макете: плашка от <c>side</c> до <c>width − side</c> на высоте 70–138,
/// скругления 26, выступ — дуга R 48 вокруг кнопки-щита с плавными скруглениями R 18.
/// </summary>
public static class IslandGeometry
{
    public static string Path(double width, double side)
    {
        var cx = width / 2;
        var right = width - side;
        return string.Create(CultureInfo.InvariantCulture,
            $"M{side + 26} 70 H{cx - 63.5:0.##} A18 18 0 0 0 {cx - 46.18:0.##} 56.91 A48 48 0 0 1 {cx + 46.18:0.##} 56.91 " +
            $"A18 18 0 0 0 {cx + 63.5:0.##} 70 H{right - 26:0.##} A26 26 0 0 1 {right:0.##} 96 V112 A26 26 0 0 1 {right - 26:0.##} 138 " +
            $"H{side + 26} A26 26 0 0 1 {side} 112 V96 A26 26 0 0 1 {side + 26} 70 Z");
    }
}
