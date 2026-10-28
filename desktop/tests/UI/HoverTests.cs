using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Converters;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Наведение и подписи островка (решение 485): блоки не заливаются серым темы Fluent — плитка и кнопка-щит
/// загораются своим цветом, строки и списки — синим текстом; подписи островка не упираются в его скругления.
/// </summary>
public sealed class HoverTests : WindowHarness
{
    [AvaloniaFact]
    public void Check_tile_under_the_pointer_gets_a_frame_and_a_brighter_fill_of_its_own_color()
    {
        var (window, _) = Open();
        var tiles = Buttons(window).Where(b => b.Classes.Contains("chip")).ToArray();
        var tile = tiles[0];
        var state = ((CheckChipViewModel)tile.DataContext!).State;

        Hover(window, tile);

        var edge = Named<Border>(tile, "Edge");
        edge.IsVisible.ShouldBeTrue("рамка плитки при наведении");
        ColorOf(edge.BorderBrush).ShouldBe(ColorOf(Check(state, "Dot")), "рамка — цвета плитки, а не серая или синяя");
        var fill = tile.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("hover"));
        fill.IsVisible.ShouldBeTrue();
        ColorOf(fill.Background).ShouldBe(ColorOf(Check(state, "Hover")), "фон — тот же цвет, ярче");
        ColorOf(fill.Background).A.ShouldBeGreaterThan(ColorOf(Check(state, "Tint")).A, "ярче обычного оттенка");

        Named<Border>(tiles[1], "Edge").IsVisible.ShouldBeFalse("соседняя плитка без наведения — без рамки");
        tiles[1].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("hover")).IsVisible.ShouldBeFalse();
        Snapshot(window, "main-tile-hover");
    }

    [AvaloniaFact]
    public void Chosen_tile_keeps_the_frame_of_its_own_color()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        main.Chips[3].ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var tile = Buttons(window).Where(b => b.Classes.Contains("chip")).ElementAt(3);
        var edge = Named<Border>(tile, "Edge");
        edge.IsVisible.ShouldBeTrue();
        ColorOf(edge.BorderBrush).ShouldBe(ColorOf(Check(main.Chips[3].State, "Dot")));
    }

    [AvaloniaFact]
    public void Shield_under_the_pointer_keeps_its_ring_and_mark_and_fills_inside_with_the_state_color()
    {
        var (window, shell) = Open();
        var power = Buttons(window).Single(b => b.Name == "Power");
        var tone = shell.Protection.Tone;
        Named<Ellipse>(power, "PowerFill").IsVisible.ShouldBeFalse("без наведения круг белый");

        Hover(window, power);

        var ring = power.GetVisualDescendants().OfType<Border>().Single(b => b.BorderThickness.Left > 0 && b.Name is null);
        ColorOf(ring.BorderBrush).ShouldBe(ColorOf(Tone(tone, "Ring")), "кольцо остается цвета состояния");
        ColorOf(ring.Background).ShouldBe(ColorOf(Resource("PgBrush.Surface.Solid")));
        var mark = power.GetVisualDescendants().OfType<Icon>().Single();
        ColorOf(mark.Foreground).ShouldBe(ColorOf(Tone(tone, "Icon")), "знак не чернеет");
        var fill = Named<Ellipse>(power, "PowerFill");
        fill.IsVisible.ShouldBeTrue("круг внутри — цветом состояния");
        ColorOf(fill.Fill).ShouldBe(ColorOf(Tone(tone, "Fill")));
        ColorOf(fill.Fill).A.ShouldBeLessThan((byte)255, "прозрачным");
        Snapshot(window, "main-shield-hover");
    }

    [AvaloniaFact]
    public void Settings_row_under_the_pointer_turns_blue_without_a_gray_fill()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        var row = Buttons(window).First(b => b.Classes.Contains("plain") && b.IsEffectivelyVisible);

        Hover(window, row);

        var presenter = Presenter(row);
        ColorOf(presenter.Background).ShouldBe(ColorOf(row.Background), "фон строки не меняется");
        ColorOf(presenter.Foreground).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")));
        var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("strong"));
        ColorOf(title.Foreground).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")), "заголовок строки — синий");
    }

    [AvaloniaFact]
    public void Server_switch_is_a_drop_down_like_the_advanced_settings_and_turns_blue_under_the_pointer()
    {
        var (window, _) = Open();
        var change = Buttons(window).Single(b => b.Name == "SwitchButton");
        change.Classes.ShouldContain("select", "как выпадающие списки «Подключения и ядра»");
        Find<TextBlock>(window, "ServerTitle").FindAncestorOfType<Button>().ShouldBeNull("строка сервера не подсвечивается целиком");

        Hover(window, change);

        var presenter = Presenter(change);
        ColorOf(presenter.Background).ShouldBe(ColorOf(Resource("PgBrush.Surface.Solid")), "без серой заливки");
        ColorOf(presenter.BorderBrush).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")));
        ColorOf(presenter.Foreground).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")));
        Snapshot(window, "main-switch-hover");
    }

    [AvaloniaFact]
    public void Chosen_item_of_a_drop_down_is_blue_and_bold_without_a_fill()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Core);
        Dispatcher.UIThread.RunJobs();
        var core = (CorePageViewModel)shell.CurrentPage!;
        core.ToggleLogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<Popup>().Single(p => p.PlacementTarget?.Name == "LogChoice");
        list.IsOpen.ShouldBeTrue();
        list.Width.ShouldBe(double.NaN, "ширина списка — по тексту");
        list.MinWidth.ShouldBe(0);
        var chosen = list.Child!.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("current"));
        ColorOf(chosen.Foreground).ShouldBe(ColorOf(Resource("PgBrush.Accent.Text")));
        chosen.FontWeight.ShouldBe(FontWeight.SemiBold);
        ColorOf(Presenter(chosen).Background).A.ShouldBe((byte)0, "выбранный пункт без заливки");
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Island_labels_stay_clear_of_its_rounded_edges(string language)
    {
        Loc.Instance.SetLanguage(language);
        var (window, _) = Open();
        var island = window.GetVisualDescendants().OfType<IslandNav>().Single();
        var side = (double)Resource("PgLayout.Island.SideMargin");
        var corner = (double)Resource("PgLayout.Island.CornerRadius");
        var labels = island.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.FindAncestorOfType<Button>()?.Classes.Contains("nav") == true)
            .ToArray();
        labels.Length.ShouldBe(4);

        foreach (var label in labels)
        {
            var left = label.TranslatePoint(default, island)!.Value.X;
            var right = left + label.Bounds.Width;
            // Подпись — не ближе половины скругления к краю плашки: там контур уже загибается вверх.
            left.ShouldBeGreaterThanOrEqualTo(side + (corner / 2), $"«{label.Text}» слева");
            right.ShouldBeLessThanOrEqualTo(island.Bounds.Width - side - (corner / 2), $"«{label.Text}» справа");
        }

        Snapshot(window, "island-" + language);
    }

    private static void Hover(MainWindow window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        control.IsPointerOver.ShouldBeTrue("указатель над элементом");
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static ContentPresenter Presenter(Button button) => Named<ContentPresenter>(button, "PART_ContentPresenter");

    private static object Resource(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value) ? value! : throw new KeyNotFoundException(key);

    private static object Check(CheckState state, string part) =>
        new CheckBrushConverter().Convert(state, typeof(IBrush), part, System.Globalization.CultureInfo.InvariantCulture)!;

    private static object Tone(Tone tone, string part) =>
        new ToneBrushConverter().Convert(tone, typeof(IBrush), part, System.Globalization.CultureInfo.InvariantCulture)!;

    private static Color ColorOf(object? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        null => Colors.Transparent,
        _ => throw new InvalidOperationException($"не сплошной цвет: {brush}"),
    };
}
