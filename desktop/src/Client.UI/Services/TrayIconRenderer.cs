using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PrivateGateway.Client.UI.Converters;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Значок в трее — щит цвета состояния с белым знаком внутри (как кнопка-щит): виден и на светлой,
/// и на темной панели задач. Рисуется из тех же геометрий и цветов темы, что и окно.
/// </summary>
public static class TrayIconRenderer
{
    private const int Size = 64;

    public static Bitmap Render(Tone tone, string markKey)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(Size / 24.0, Size / 24.0)))
        {
            var color = (IBrush)ThemeResources.Get($"PgBrush.State.{tone}.Ring");
            context.DrawGeometry(color, new Pen(color, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                (Geometry)ThemeResources.Get("Icon.Shield"));
            context.DrawGeometry(null, new Pen(Brushes.White, 2.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                (Geometry)ThemeResources.Get(markKey));
        }

        return bitmap;
    }
}
