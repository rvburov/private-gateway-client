using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Экран «Серверы» на демо-каталоге; снимки — для сверки с docs/design/screens/02-servers.png.</summary>
public sealed class ServersPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task List_matches_mockup_when_protected()
    {
        var (window, shell, page) = OpenServers();
        await Connect(shell);

        var group = page.Groups.ShouldHaveSingleItem();
        group.Title.ShouldBe("Основная подписка · 7");
        group.UpdatedText.ShouldBe("обновлено " + LocalTime(Time.GetUtcNow().AddMinutes(-32)));
        group.Rows.Select(r => r.Name).ShouldBe(
            ["Финляндия 01", "Нидерланды 01", "Германия 02", "Швеция 01", "Польша 03", "США 01", "Турция 01"]);
        var nl = group.Rows[1];
        nl.Tag.ShouldBe("активен");
        nl.Address.ShouldBe("203.0.113.24 · VLESS · REALITY");
        nl.LatencyText.ShouldBe("42 мс");
        group.Rows[5].IsSlow.ShouldBeTrue("148 мс — медленно, серым");
        group.Rows[6].LatencyText.ShouldBe("нет");
        group.Rows[6].IsDown.ShouldBeTrue();
        page.AllTabText.ShouldBe("Все · 7");
        page.HasFilterCount.ShouldBeFalse();
        Snapshot(window, "servers");
    }

    [AvaloniaFact]
    public void Selected_server_is_marked_selected_until_protection_is_on()
    {
        var (_, _, page) = OpenServers();

        var selected = page.Groups.SelectMany(g => g.Rows).Single(r => r.IsSelected);

        selected.Name.ShouldBe("Нидерланды 01");
        selected.Tag.ShouldBe("выбран", "без защиты сервер выбран, но не используется");
    }

    [AvaloniaFact]
    public void Search_narrows_the_list_and_explains_an_empty_result()
    {
        var (window, _, page) = OpenServers();

        Find<TextBox>(window, "Search").Text = "герм";
        Dispatcher.UIThread.RunJobs();
        page.Groups.SelectMany(g => g.Rows).ShouldHaveSingleItem().Name.ShouldBe("Германия 02");

        page.SearchText = "Атлантида";
        Dispatcher.UIThread.RunJobs();
        page.IsEmpty.ShouldBeTrue();
        Text(window, "EmptyText").ShouldBe("Нет серверов по выбранным фильтрам");
    }

    [AvaloniaFact]
    public void Filter_hides_protocols_changes_sort_and_resets()
    {
        var (window, _, page) = OpenServers();

        Press(window, "FilterButton");
        Find<Border>(window, "FilterPanel").IsVisible.ShouldBeTrue();
        page.Protocols.Select(p => p.Name).ShouldBe(["VLESS", "Hysteria2", "TUIC", "WireGuard", "Trojan"]);

        page.Protocols[0].IsOn = false;
        Find<RadioButton>(window, "SortName").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Text(window, "FilterCount").ShouldBe("2");
        page.Groups.SelectMany(g => g.Rows).Select(r => r.Name)
            .ShouldBe(["Польша 03", "США 01", "Финляндия 01", "Швеция 01"], "без VLESS, по алфавиту");
        Snapshot(window, "servers-filter");

        page.ResetFilterCommand.Execute(null);
        page.CloseFilterCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.HasFilterCount.ShouldBeFalse();
        page.Protocols.ShouldAllBe(p => p.IsOn);
        page.Sort.ShouldBe(ServerSort.Latency);
        page.Groups.SelectMany(g => g.Rows).Count().ShouldBe(7);
        Find<Border>(window, "FilterPanel").IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Tabs_show_favorites_and_recent_servers()
    {
        var (window, _, page) = OpenServers();

        Find<RadioButton>(window, "TabFavorites").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        var favorites = page.Groups.ShouldHaveSingleItem();
        favorites.Title.ShouldBe("Избранные · 3");
        favorites.HasUpdated.ShouldBeFalse("избранные бывают из разных подписок");
        favorites.Rows.Select(r => r.Name).ShouldBe(["Финляндия 01", "Нидерланды 01", "Германия 02"]);

        Find<RadioButton>(window, "TabRecent").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        page.Groups.ShouldHaveSingleItem().Rows.Select(r => r.Name).ShouldBe(["Нидерланды 01", "Германия 02", "Швеция 01"]);
    }

    [AvaloniaFact]
    public async Task Pressing_a_row_while_protected_reconnects_to_that_server()
    {
        var (_, shell, page) = OpenServers();
        await Connect(shell);
        var de = page.Groups[0].Rows.Single(r => r.Name == "Германия 02");

        var choose = de.ChooseCommand.ExecuteAsync(de.Id);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connecting);
        page.Groups[0].Rows.Single(r => r.IsSelected).Name.ShouldBe("Германия 02");

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await choose;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        shell.Protection.Status.ServerId.ShouldBe("demo-de-02");
        shell.Protection.Status.Exit.Ip.ShouldBe("198.51.100.140", "выход — выбранный сервер, а не прежний");
    }

    [AvaloniaFact]
    public void Pressing_a_row_while_off_only_selects_it()
    {
        var (_, shell, page) = OpenServers();
        var se = page.Groups[0].Rows.Single(r => r.Name == "Швеция 01");

        se.ChooseCommand.Execute(se.Id);
        Dispatcher.UIThread.RunJobs();

        shell.Protection.Status.State.ShouldBe(ConnectionState.Off);
        page.Groups[0].Rows.Single(r => r.IsSelected).Name.ShouldBe("Швеция 01");
    }

    [AvaloniaFact]
    public async Task Refresh_spins_the_icon_and_reports_when_done()
    {
        var (window, shell, page) = OpenServers();
        var icon = Find<Button>(window, "RefreshButton").GetVisualDescendants().OfType<Icon>().Single();

        var refresh = page.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        page.IsRefreshing.ShouldBeTrue();
        icon.Classes.ShouldContain("spin");

        Time.Advance(DemoServerCatalog.RefreshDelay);
        await refresh;
        WaitFor(() => !page.IsRefreshing);

        icon.Classes.ShouldNotContain("spin");
        shell.Toast.Text.ShouldBe("Серверы обновлены · пинг проверен");
        WaitFor(() => page.Groups[0].UpdatedText == "обновлено " + LocalTime(Time.GetUtcNow()));
    }

    [AvaloniaFact]
    public void Plus_opens_import()
    {
        var (_, shell, page) = OpenServers();

        page.AddCommand.Execute(null);

        shell.CurrentKey.ShouldBe(Pages.Add);
    }

    [AvaloniaFact]
    public void English_labels_switch_at_once()
    {
        var (window, _, page) = OpenServers();

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();

        page.AllTabText.ShouldBe("All · 7");
        page.Groups[0].Rows[^1].LatencyText.ShouldBe("n/a");
        page.Groups[0].Rows[1].Tag.ShouldBe("selected");
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("Servers");
        texts.ShouldContain("ОСНОВНАЯ ПОДПИСКА · 7", "название подписки дал пользователь — не переводится");
        Snapshot(window, "servers-en");
    }

    private (MainWindow Window, ShellViewModel Shell, ServersPageViewModel Page) OpenServers()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>());
    }

    private static string LocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
