using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>
/// QR-код вектором: темные модули — прямоугольниками одной геометрии, вписанной в размер элемента.
/// Белое поле и фон дает рамка вокруг; растровых картинок в окне нет.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<bool[,]?> ModulesProperty =
        AvaloniaProperty.Register<QrCodeView, bool[,]?>(nameof(Modules));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(Foreground));

    static QrCodeView() => AffectsRender<QrCodeView>(ModulesProperty, ForegroundProperty);

    /// <summary>Модули [строка, столбец], true — темный.</summary>
    public bool[,]? Modules
    {
        get => GetValue(ModulesProperty);
        set => SetValue(ModulesProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Modules is not { } modules || Foreground is not { } brush || modules.GetLength(0) == 0)
        {
            return;
        }

        var count = modules.GetLength(0);
        var cell = Math.Min(Bounds.Width, Bounds.Height) / count;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var y = 0; y < count; y++)
            {
                for (var x = 0; x < modules.GetLength(1); x++)
                {
                    if (!modules[y, x])
                    {
                        continue;
                    }

                    path.BeginFigure(new Point(x * cell, y * cell), true);
                    path.LineTo(new Point((x + 1) * cell, y * cell));
                    path.LineTo(new Point((x + 1) * cell, (y + 1) * cell));
                    path.LineTo(new Point(x * cell, (y + 1) * cell));
                    path.EndFigure(true);
                }
            }
        }

        context.DrawGeometry(brush, null, geometry);
    }
}
