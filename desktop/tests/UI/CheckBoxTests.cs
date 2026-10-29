using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Флажок по дизайн-системе (доска 24, docs/design/mockup/DSComponents.dc.html и Backup.dc.html): квадрат 20 со
/// скруглением 6; отмечен — акцент с белой галочкой 12 (линия 3,4 из 24, как stroke-width в SVG макета); не отмечен —
/// белый с рамкой 1,5; подпись — через 12 от квадрата; строка с подписью — не ниже 46.
/// </summary>
public sealed class CheckBoxTests : WindowHarness
{
    [AvaloniaFact]
    public void Check_box_matches_the_design_system()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Backup);
        Dispatcher.UIThread.RunJobs();
        var on = Find<CheckBox>(window, "PartServers");
        var off = Find<CheckBox>(window, "PartKeys");
        on.IsChecked.ShouldBe(true);
        off.IsChecked.ShouldBe(false);

        var box = Named<Border>(on, "Box");
        box.Bounds.Size.ShouldBe(new Size(20, 20));
        box.CornerRadius.ShouldBe(new CornerRadius(6));
        ColorOf(box.Background).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")));
        var mark = Named<Icon>(on, "Mark");
        mark.IsEffectivelyVisible.ShouldBeTrue();
        mark.Bounds.Size.ShouldBe(new Size(12, 12), "галочка 12 в квадрате 20, как в макете");
        mark.StrokeThickness.ShouldBe(3.4);
        ColorOf(mark.Foreground).ShouldBe(Colors.White);

        var empty = Named<Border>(off, "Box");
        empty.BorderThickness.ShouldBe(new Thickness(1.5));
        ColorOf(empty.BorderBrush).ShouldBe(ColorOf(Resource("PgBrush.Control.CheckboxBorder")));
        ColorOf(empty.Background).ShouldBe(Colors.White);
        Named<Icon>(off, "Mark").IsEffectivelyVisible.ShouldBeFalse();

        var label = on.GetVisualDescendants().OfType<ContentPresenter>().Single();
        var gap = label.TranslatePoint(default, on)!.Value.X - box.TranslatePoint(new Point(box.Bounds.Width, 0), on)!.Value.X;
        gap.ShouldBe(12, 0.5, "подпись — через 12 от квадрата");
        on.Bounds.Height.ShouldBeGreaterThanOrEqualTo(46, "строка с подписью — не ниже 46");
        var middle = box.TranslatePoint(new Point(0, box.Bounds.Height / 2), on)!.Value.Y;
        middle.ShouldBe(on.Bounds.Height / 2, 0.5, "квадрат — по середине строки, как align-items: center");

        Snapshot(window, "backup-checkboxes");
    }

    [AvaloniaFact]
    public void Icon_takes_its_size_from_a_template_or_a_style()
    {
        // По умолчанию — 24; размер из стиля (значки первого запуска 20 и 16) и из шаблона (галочка флажка 12) сильнее.
        var plain = new Icon();
        var styled = new Icon();
        styled.Classes.Add("small");
        var panel = new StackPanel { Children = { plain, styled } };
        var window = new Window { Width = 200, Height = 200, Content = panel };
        window.Styles.Add(new Style(x => x.OfType<Icon>().Class("small"))
        {
            Setters = { new Setter(Layoutable.WidthProperty, 16d), new Setter(Layoutable.HeightProperty, 16d) },
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        plain.Bounds.Size.ShouldBe(new Size(24, 24));
        styled.Bounds.Size.ShouldBe(new Size(16, 16));
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static object Resource(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value) ? value! : throw new KeyNotFoundException(key);

    private static Color ColorOf(object? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        null => Colors.Transparent,
        _ => throw new InvalidOperationException($"не сплошной цвет: {brush}"),
    };
}
