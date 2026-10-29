using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>
/// Контурная иконка 24×24 из Themes/Icons.axaml, вписанная в размер элемента. Цвет линии —
/// <see cref="TextElement.ForegroundProperty"/> (наследуется от кнопки или текста вокруг), как у
/// шрифтовых иконок. <see cref="Detail"/> — вторая часть иконки (например, импульс внутри
/// «Проверки»), рисуется тем же цветом без заливки.
/// </summary>
public sealed class Icon : Viewbox
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<Geometry?> DetailProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Detail));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.8);

    /// <summary>Цвет линии; по умолчанию наследуется от окружения, как цвет текста.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Fill));

    private const double Canvas24 = 24;

    static Icon()
    {
        // 24 — значение по умолчанию, а не локальное: локальное сильнее шаблона и стиля, и галочка флажка (12 в
        // шаблоне) или значки первого запуска (20 и 16 из стиля) рисовались бы 24.
        WidthProperty.OverrideDefaultValue<Icon>(Canvas24);
        HeightProperty.OverrideDefaultValue<Icon>(Canvas24);
    }

    public Icon()
    {
        Stretch = Stretch.Uniform;
        var main = MakePath();
        main[!Path.DataProperty] = this[!DataProperty];
        main[!Shape.FillProperty] = this[!FillProperty];
        var detail = MakePath();
        detail[!Path.DataProperty] = this[!DetailProperty];
        Child = new Canvas { Width = Canvas24, Height = Canvas24, Children = { main, detail } };
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Geometry? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private Path MakePath()
    {
        var path = new Path
        {
            Width = Canvas24,
            Height = Canvas24,
            Stretch = Stretch.None,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        path[!Shape.StrokeProperty] = this[!ForegroundProperty];
        path[!Shape.StrokeThicknessProperty] = this[!StrokeThicknessProperty];
        return path;
    }
}
