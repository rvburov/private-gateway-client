using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Прокрутка разделов островка переживает подстраницу: «Проверка» → «Как меняется путь при VPN» → «Назад» — то же место,
/// что было; подстраница при новом открытии — с начала.
/// </summary>
public sealed class ScrollMemoryTests : WindowHarness
{
    [AvaloniaFact]
    public void Back_from_a_lab_page_returns_to_the_same_place_of_the_check_page()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        var place = ScrollTo(window, 300);

        Press(window, "VpnPathButton");
        shell.CurrentKey.ShouldBe(Pages.VpnPath);
        Page(window).Offset.Y.ShouldBe(0, "подстраница — с начала");
        Press(window, "BackButton");

        shell.CurrentKey.ShouldBe(Pages.Check);
        Page(window).Offset.Y.ShouldBe(place, 0.5, "то же место, что до перехода");
    }

    [AvaloniaFact]
    public void Back_from_a_settings_page_returns_to_the_same_place_of_the_settings()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        var place = ScrollTo(window, 250);

        shell.NavigateCommand.Execute(Pages.Protection);
        Dispatcher.UIThread.RunJobs();
        Press(window, "BackButton");

        shell.CurrentKey.ShouldBe(Pages.Settings);
        Page(window).Offset.Y.ShouldBe(place, 0.5);
    }

    [AvaloniaFact]
    public void Unreachable_place_does_not_undo_the_users_scrolling()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        var bottom = ScrollTo(window, 10_000);

        // Пока «Настроек» не было, их место стало недостижимым: окно выше — страница прокручивается меньше.
        shell.NavigateCommand.Execute(Pages.Protection);
        Dispatcher.UIThread.RunJobs();
        window.Height += 200;
        Dispatcher.UIThread.RunJobs();
        Press(window, "BackButton");
        var page = Page(window);
        page.Offset.Y.ShouldBeLessThan(bottom, "дальше нового низа страницы не прокрутить");
        page.Offset.Y.ShouldBeGreaterThan(0, "насколько можно — ближе к прежнему месту");

        page.Offset = new Vector(0, 10);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        page.Offset.Y.ShouldBe(10, "прокрутку пользователя возврат не перебивает");
    }

    [AvaloniaFact]
    public void Sub_page_opens_from_the_top_again()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "VpnPathButton");
        ScrollTo(window, 200).ShouldBeGreaterThan(0);
        Press(window, "BackButton");

        Press(window, "VpnPathButton");

        Page(window).Offset.Y.ShouldBe(0);
    }

    private static ScrollViewer Page(Window window) =>
        window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Classes.Contains("page") && s.IsEffectivelyVisible);

    /// <summary>Прокрутить страницу вниз (насколько позволяет содержимое) и вернуть, где она встала.</summary>
    private static double ScrollTo(Window window, double y)
    {
        var page = Page(window);
        page.Offset = new Vector(0, y);
        Dispatcher.UIThread.RunJobs();
        page.Offset.Y.ShouldBeGreaterThan(0, "страница длиннее окна");
        return page.Offset.Y;
    }
}
