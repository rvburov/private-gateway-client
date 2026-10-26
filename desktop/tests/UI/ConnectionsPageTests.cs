using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Соединения и трафик» — сетевой инспектор (этап 7): открывается из «Проверки»; скорость адаптеров — по двум соседним
/// счетчикам службы (в демо — растут каждую секунду имитации), сокеты с процессами — соединения и ждущие входящих. Снимки —
/// в artifacts/ui-snapshots.
/// </summary>
public sealed class ConnectionsPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Traffic_speed_comes_from_two_samples_and_connections_are_grouped_by_program()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "ConnectionsButton");
        WaitFor(() => shell.CurrentPage is ConnectionsPageViewModel { IsLoading: false, HasTraffic: true });
        var page = (ConnectionsPageViewModel)shell.CurrentPage!;
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");

        page.Traffic.Select(t => (t.Name, t.Speed)).ShouldBe([("Wi-Fi", "меряем…"), ("Private Gateway", "меряем…")], "по одному замеру скорости нет");
        Time.Advance(TimeSpan.FromSeconds(1));
        WaitFor(() => page.Traffic.Count > 0 && page.Traffic[0].Speed != "меряем…");
        page.Traffic[0].Speed.ShouldBe("↓ 1,5 МБ/с · ↑ 120 КБ/с");
        page.Traffic[0].Total.ShouldBe("принято 1,2 ГБ · отправлено 230 МБ");

        page.IsActive.ShouldBeTrue();
        page.Connections.Select(c => (c.Process, c.Kind, c.Endpoints)).ShouldBe([
            ("firefox · 4120", "TCP · установлено", "192.0.2.23:51240 → 198.51.100.14:443"),
            ("firefox · 4120", "TCP · установлено", "192.0.2.23:51234 → 203.0.113.80:443"),
            ("firefox · 4120", "UDP · открыт", "192.0.2.23:50000 → 203.0.113.90:443"),
            ("telegram · 5310", "TCP · установлено", "192.0.2.23:51302 → 198.51.100.200:5222"),
            ("telegram · 5310", "TCP · закрывается", "192.0.2.23:51310 → 203.0.113.24:443"),
        ], "по программам, внутри — по адресу другой стороны");
        page.CountText.ShouldBe("Всего: 5");
        page.HasHiddenOwners.ShouldBeTrue("у одного сокета ОС не назвала программу");
        Snapshot(window, "connections");

        page.IsListening = true;
        page.Connections.Select(c => (c.Process, c.Endpoints)).ShouldBe([
            ("sshd · 880", "все адреса:22"),
            ("xray · 2208", "127.0.0.1:10808"),
            ("программа неизвестна", "все адреса:5353"),
        ], "слушающие и UDP без связи; без программы — последним");
        page.CountText.ShouldBe("Всего: 3");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Connections[2].Process.ShouldBe("unknown program");
        page.Traffic[0].Total.ShouldBe("received 1.2 GB · sent 230 MB");
    }

    [AvaloniaFact]
    public void Counters_stop_when_the_page_leaves_the_screen()
    {
        var (_, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Connections);
        WaitFor(() => shell.CurrentPage is ConnectionsPageViewModel { HasTraffic: true });
        var page = (ConnectionsPageViewModel)shell.CurrentPage!;

        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Time.Advance(TimeSpan.FromSeconds(3));
        Thread.Sleep(50);
        Dispatcher.UIThread.RunJobs();

        page.Traffic.ShouldAllBe(t => t.Speed == "меряем…", "ушли со страницы — счетчики больше не идут");
    }

    [AvaloniaFact]
    public void Sizes_and_rates_read_like_people_write_them()
    {
        ConnectionsPageViewModel.Size(0).ShouldBe("0 Б");
        ConnectionsPageViewModel.Size(1023).ShouldBe("1023 Б");
        ConnectionsPageViewModel.Size(1536).ShouldBe("1,5 КБ");
        ConnectionsPageViewModel.Size(150 * 1024 * 1024).ShouldBe("150 МБ");
        ConnectionsPageViewModel.Size(5.5 * 1024 * 1024 * 1024 * 1024).ShouldBe("5,5 ТБ");
        ConnectionsPageViewModel.Rate(2048).ShouldBe("2 КБ/с");
    }

    [AvaloniaFact]
    public void Without_a_service_the_page_says_why_it_is_empty()
    {
        var (window, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Connections);
        WaitFor(() => shell.CurrentPage is ConnectionsPageViewModel { IsLoading: false });
        var page = (ConnectionsPageViewModel)shell.CurrentPage!;

        page.IsUnavailable.ShouldBeTrue();
        page.HasTraffic.ShouldBeFalse();
        page.Connections.ShouldBeEmpty();
        Snapshot(window, "connections-no-service");
    }
}
