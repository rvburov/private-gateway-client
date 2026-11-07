using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «События сети» — сетевая лаборатория (этап 8): что произошло с защитой и сетью с запуска окна и почему; новые сверху,
/// эксперту — что служба сделала с маршрутами, DNS и брандмауэром. Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class EventsPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Connect_and_disconnect_show_up_as_explained_events()
    {
        var (window, shell) = Open();
        Service<NetworkEventLog>().Start();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "EventsButton");
        var page = shell.CurrentPage.ShouldBeOfType<EventsPageViewModel>();
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.HasEvents.ShouldBeFalse("состояние на момент запуска — не событие");

        await Connect(shell);
        WaitFor(() => page.Rows.Any(r => r.Kind == NetworkEventKind.ExitMeasured));

        page.Rows.Select(r => r.Kind).ShouldBe([NetworkEventKind.ExitMeasured, NetworkEventKind.Connected, NetworkEventKind.Connecting], "новые сверху");
        page.Rows[0].Title.ShouldBe("Адрес для сайтов: 203.0.113.24");
        var connected = page.Rows[1];
        (connected.Title, connected.State, connected.Icon).ShouldBe(("Защита включена", CheckState.Ok, "Icon.Mark.Ok"));
        connected.Text.ShouldStartWith("Туннель поднят");
        connected.HasExpert.ShouldBeFalse("для начинающего — без подробностей");
        connected.Time.ShouldBe(Time.GetUtcNow().ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        page.Rows[2].State.ShouldBe(CheckState.Waiting);

        window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "ExpertMode").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.LabExpert.ShouldBeTrue("режим лаборатории общий и запоминается");
        page.Rows[1].Expert.ShouldStartWith("TUN-адаптер получил маршруты 0.0.0.0/1 и 128.0.0.0/1");
        Snapshot(window, "events");

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => page.Rows[0].Kind == NetworkEventKind.Off);
        page.Rows[0].Title.ShouldBe("Защита выключена");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Rows.Select(r => r.Title).ShouldBe(["Protection is off", "Address seen by sites: 203.0.113.24", "Protection is on", "Connecting"]);
    }

    [AvaloniaFact]
    public void Every_event_kind_has_texts_and_details_where_they_matter()
    {
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        foreach (var kind in Enum.GetValues<NetworkEventKind>())
        {
            var row = new EventRowViewModel(new NetworkEvent(at, kind, kind == NetworkEventKind.Warning ? "dns" : "PGC-NET-001"), expert: true);
            row.Title.ShouldNotStartWith("event.", customMessage: kind.ToString());
            row.Text.ShouldNotStartWith("event.", customMessage: kind.ToString());
            row.Expert.ShouldNotBeNullOrEmpty(kind.ToString());
        }

        new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.Reconnecting, "2"), false).Title.ShouldBe("Переподключение — попытка 2");
        var blocked = new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.Blocked, "PGC-NET-001"), false);
        (blocked.Detail, blocked.State).ShouldBe(("PGC-NET-001", CheckState.Warning));
        new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.NetworkChanged, "Cafe"), false).Detail.ShouldBe("Cafe");
        new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.ExitMeasured, "203.0.113.24"), false).Title.ShouldBe("Адрес для сайтов: 203.0.113.24");
        new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.Warning, "dns"), false).Text
            .ShouldBe("Запросы DNS идут мимо туннеля. Откройте «Проверку» — там подробности и что делать.");
        new EventRowViewModel(new NetworkEvent(at, NetworkEventKind.Off), false).ShouldSatisfyAllConditions(
            r => r.HasDetail.ShouldBeFalse(),
            r => r.State.ShouldBe(CheckState.Off));
    }
}
