using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Строки-ссылки со стрелкой «›» («Для опытных», «Проверка», «Лаборатория», «О программе», «Маршрутизация»): строка —
/// во всю ширину блока, стрелки одного блока — на одной линии у правого края, а не сразу за текстом; под указателем
/// стрелка синеет вместе с заголовком строки; строки одного блока разделены полосами, как «Компоненты» (решение 486).
/// </summary>
public sealed class RowLayoutTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData(Pages.Settings, 7)]
    [InlineData(Pages.Check, 9)]
    [InlineData(Pages.About, 3)]
    [InlineData(Pages.Routing, 1)]
    public void Chevrons_of_a_card_line_up_at_its_right_edge(string page, int rows)
    {
        var (window, chevrons) = Chevrons(page);
        chevrons.Length.ShouldBe(rows);

        foreach (var icon in chevrons)
        {
            var row = icon.FindAncestorOfType<Button>()!;
            var holder = (Control)row.GetVisualParent()!;
            row.Bounds.Width.ShouldBe(holder.Bounds.Width, 0.5, $"строка «{row.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)}» — во всю ширину блока");
        }

        foreach (var card in chevrons.GroupBy(i => i.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"))))
        {
            var edges = card.Select(i => i.TranslatePoint(new Point(i.Bounds.Width, 0), window)!.Value.X).ToArray();
            (edges.Max() - edges.Min()).ShouldBeLessThan(0.5, "стрелки блока — на одной линии");
        }

        chevrons[^1].BringIntoView();
        Snapshot(window, "rows-" + page);
    }

    [AvaloniaTheory]
    [InlineData(Pages.Settings)]
    [InlineData(Pages.Check)]
    [InlineData(Pages.About)]
    [InlineData(Pages.Routing)]
    public void Chevron_turns_blue_with_its_row_under_the_pointer(string page)
    {
        var (window, chevrons) = Chevrons(page);
        chevrons.ShouldNotBeEmpty();
        chevrons.Select(i => ColorOf(i.Foreground)).ShouldAllBe(c => c == ColorOf(Resource("PgBrush.Text.Muted")), "без указателя — серые");

        foreach (var icon in chevrons)
        {
            var row = icon.FindAncestorOfType<Button>()!;
            Hover(window, row);
            ColorOf(icon.Foreground).ShouldBe(ColorOf(Resource("PgBrush.Accent.Base")), $"стрелка «{row.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)}»");
            chevrons.Where(i => i != icon).Select(i => ColorOf(i.Foreground))
                .ShouldAllBe(c => c == ColorOf(Resource("PgBrush.Text.Muted")), "у соседних строк — серые");
        }
    }

    [AvaloniaTheory]
    [InlineData(Pages.Settings, 6)]
    [InlineData(Pages.Check, 7)]
    [InlineData(Pages.About, 2)]
    public void Rows_of_a_card_are_separated_by_lines(string page, int lines)
    {
        var (window, chevrons) = Chevrons(page);
        var divider = ColorOf(Resource("PgBrush.Line.Divider"));
        var found = 0;

        foreach (var card in chevrons.GroupBy(i => i.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"))))
        {
            var rows = card.Select(i => i.FindAncestorOfType<Button>()!)
                .Select(b => (Top: b.TranslatePoint(default, window)!.Value.Y, Bottom: b.TranslatePoint(new Point(0, b.Bounds.Height), window)!.Value.Y))
                .OrderBy(r => r.Top)
                .ToArray();
            var bars = card.Key.GetVisualDescendants().OfType<Border>()
                .Where(b => b.IsEffectivelyVisible && b.Bounds.Height == 1 && ColorOf(b.Background) == divider)
                .Select(b => b.TranslatePoint(default, window)!.Value.Y)
                .ToArray();

            bars.ShouldAllBe(y => y > rows[0].Top, "над первой строкой полосы нет");
            for (var k = 1; k < rows.Length; k++)
            {
                var (above, below) = (rows[k - 1].Bottom, rows[k].Top);
                bars.Count(y => y >= above - 0.5 && y <= below + 0.5).ShouldBe(1, $"между строками {k} и {k + 1} — полоса");
                found++;
            }
        }

        found.ShouldBe(lines);
        if (page == Pages.About)
        {
            chevrons[^1].BringIntoView();
            Snapshot(window, "about-links");
        }
    }

    [Fact]
    public void Every_row_chevron_follows_the_hover_color()
    {
        // Стрелка с цветом в разметке не синела бы: локальное значение сильнее стиля наведения. Своего цвета —
        // только полоса «…идут мимо VPN» на главной (оранжевая).
        var icons = Directory.EnumerateFiles(System.IO.Path.Combine(Repo.UiSource, "Views"), "*.axaml")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "<c:Icon\\b[^>]*Icon\\.ChevronRight[^>]*/>")
                .Select(m => (File: System.IO.Path.GetFileName(f), Tag: m.Value)))
            .ToArray();

        icons.Length.ShouldBeGreaterThanOrEqualTo(15);
        icons.Where(i => !i.Tag.Contains("Classes=\"chevron\"", StringComparison.Ordinal)).Select(i => i.File).ShouldBe(["MainPage.axaml"]);
        icons.Where(i => i.Tag.Contains("Classes=\"chevron\"", StringComparison.Ordinal) && i.Tag.Contains("Foreground=", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private (Window Window, Icon[] Chevrons) Chevrons(string page)
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(page);
        Dispatcher.UIThread.RunJobs();
        var chevron = Resource("Icon.ChevronRight");
        var chevrons = window.GetVisualDescendants().OfType<Icon>()
            .Where(i => ReferenceEquals(i.Data, chevron) && i.IsEffectivelyVisible && i.FindAncestorOfType<Button>() is not null)
            .ToArray();
        return (window, chevrons);
    }

    /// <summary>Строку — в верх видимой части страницы (внизу ее закрыл бы островок) и указатель на нее.</summary>
    private static void Hover(Window window, Control control)
    {
        var scroll = control.FindAncestorOfType<ScrollViewer>()!;
        var top = control.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Max(0, top - 80));
        Dispatcher.UIThread.RunJobs();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        control.IsPointerOver.ShouldBeTrue("указатель над строкой");
    }

    private static object Resource(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value) ? value! : throw new KeyNotFoundException(key);

    private static Color ColorOf(object? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        null => Colors.Transparent,
        _ => throw new InvalidOperationException($"не сплошной цвет: {brush}"),
    };
}
