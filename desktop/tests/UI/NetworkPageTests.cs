using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Сеть машины» — сетевой инспектор (этап 7): открывается из «Проверки», показывает снимок службы (в демо — адреса и MAC
/// из диапазонов документации) и внешний IP с провайдером по кнопке, без службы говорит, почему пусто. Снимки — в
/// artifacts/ui-snapshots.
/// </summary>
public sealed class NetworkPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Network_of_the_machine_opens_from_the_check_and_shows_adapters_and_routes()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();

        Press(window, "NetworkButton");
        WaitFor(() => shell.CurrentPage is NetworkPageViewModel { IsLoading: false, Adapters.Count: > 0 });

        var page = (NetworkPageViewModel)shell.CurrentPage!;
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки» — островок на «Проверке»");
        page.IsUnavailable.ShouldBeFalse();
        page.Adapters.Select(a => (a.Name, a.StatusText)).ShouldBe([
            ("Wi-Fi", "Включен"), ("Private Gateway", "Включен"), ("Ethernet", "Выключен"), ("Loopback", "Включен"),
        ], "сначала включенные карты, потом туннель, выключенные, петля — последней");

        var wifi = page.Adapters[0];
        wifi.Subtitle.ShouldBe("Wi-Fi · Intel(R) Wi-Fi 6 AX201");
        wifi.Icon.ShouldBe("Icon.Net.WiFi");
        wifi.Rows.Select(r => (r.Label, r.Value)).ShouldBe([
            ("IPv4", "192.0.2.23/24"),
            ("IPv6", "2001:db8:1::23/64"),
            ("Шлюз", "192.0.2.1"),
            ("DNS", "192.0.2.1"),
            ("Адрес", "автоматически (DHCP) · сервер 192.0.2.1 · осталось 20 ч"),
            ("MAC", "00:00:5e:00:53:01"),
            ("Канал", "MTU 1500 · 866 Мбит/с"),
        ]);
        page.Adapters[1].Rows.ShouldNotContain(r => r.Label == "Адрес" || r.Label == "MAC", "у туннеля нет ни DHCP, ни MAC");
        page.Adapters[2].Rows.Select(r => r.Label).ShouldBe(["MAC", "Канал"], "у выключенной карты адресов нет — и пустых строк тоже");

        page.Routes[0].ShouldSatisfyAllConditions(
            r => r.Destination.ShouldBe("Весь интернет"),
            r => r.Via.ShouldBe("через 192.0.2.1 · Wi-Fi"),
            r => r.Metric.ShouldBe("метрика 25"));
        page.Routes[1].Via.ShouldBe("Wi-Fi", "сеть рядом — без шлюза");
        Text(window, "TakenAt").ShouldStartWith("Снимок ");
        Snapshot(window, "network");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Adapters[0].Rows.Single(r => r.Label == "Address").Value.ShouldBe("automatic (DHCP) · server 192.0.2.1 · 20 h left");
        page.Routes[0].Destination.ShouldBe("Whole internet");
        Snapshot(window, "network-en");

        Press(window, "BackButton");
        shell.CurrentKey.ShouldBe(Pages.Check);
    }

    [AvaloniaFact]
    public async Task Public_ip_shows_its_provider_only_on_request_and_keeps_the_answer()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Network);
        WaitFor(() => shell.CurrentPage is NetworkPageViewModel { IsLoading: false });
        var page = (NetworkPageViewModel)shell.CurrentPage!;

        var direct = page.PublicAddresses.ShouldHaveSingleItem("защита выключена — выхода нет, есть реальный адрес");
        (direct.Label, direct.AddressText).ShouldBe(("Напрямую", "198.51.100.7"));
        direct.Rows.ShouldBeEmpty("о провайдере не спрашивают, пока пользователь не попросит");
        direct.CanDescribe.ShouldBeTrue();
        Text(window, "PublicSource").ShouldStartWith("Сведения — RIPEstat (RIPE NCC)");
        Snapshot(window, "network-public-ask");

        await direct.DescribeCommand.ExecuteAsync(null);
        direct.Rows.Select(r => (r.Label, r.Value)).ShouldBe([
            ("Провайдер", "AS64496 · Example Broadband"),
            ("Сеть", "198.51.100.0/24"),
            ("Место", "RU · Москва, примерно"),
        ]);
        direct.CanDescribe.ShouldBeFalse("ответ есть — второй раз спрашивать незачем");
        Snapshot(window, "network-public");

        await Connect(shell);
        page.PublicAddresses.Select(a => (a.Label, a.AddressText)).ShouldBe([("Через сервер", "203.0.113.24"), ("Напрямую", "198.51.100.7")]);
        page.PublicAddresses[1].ShouldBeSameAs(direct, "тот же адрес — та же строка с уже полученным ответом");
        await page.PublicAddresses[0].DescribeCommand.ExecuteAsync(null);
        page.PublicAddresses[0].Rows[0].Value.ShouldBe("AS64500 · Example Hosting B.V.");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        direct.Label.ShouldBe("Direct");
        direct.Rows.Select(r => r.Label).ShouldBe(["Provider", "Network", "Location"]);
        direct.Rows[2].Value.ShouldBe("RU · Москва, approximate");
    }

    [AvaloniaFact]
    public async Task Without_an_answer_the_reason_is_named_and_asking_again_is_possible()
    {
        var (_, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Network);
        WaitFor(() => shell.CurrentPage is NetworkPageViewModel { IsLoading: false });
        ((NetworkPageViewModel)shell.CurrentPage!).HasPublicAddresses.ShouldBeFalse("служба ничего не измеряла");

        var silent = new PublicAddressViewModel(Service<IGatewayClient>(), "network.public.direct", IPAddress.Parse("198.51.100.7"));
        await silent.DescribeCommand.ExecuteAsync(null);
        silent.Note.ShouldBe("Служба не ответила — спрашивает она. Запустите службу и спросите еще раз.");
        silent.CanDescribe.ShouldBeTrue("службу можно запустить и спросить снова");

        var unknown = new PublicAddressViewModel(new SimulatedGatewayClient(Time), "network.public.exit", IPAddress.Parse("192.0.2.99"));
        await unknown.DescribeCommand.ExecuteAsync(null);
        unknown.Note.ShouldBe("Источник об этом адресе ничего не знает.");
        unknown.Rows.ShouldBeEmpty();
        unknown.CanDescribe.ShouldBeFalse();

        foreach (var failure in Enum.GetValues<AddressFailure>())
        {
            Loc.Instance.Has("network.public." + failure.ToString().ToLowerInvariant()).ShouldBeTrue($"у отказа {failure} есть текст");
        }
    }

    [AvaloniaFact]
    public void Without_a_service_the_page_says_why_it_is_empty()
    {
        var (window, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Network);
        WaitFor(() => shell.CurrentPage is NetworkPageViewModel { IsLoading: false });

        var page = (NetworkPageViewModel)shell.CurrentPage!;
        page.IsUnavailable.ShouldBeTrue();
        page.Adapters.ShouldBeEmpty();
        Find<Border>(window, "Unavailable").IsVisible.ShouldBeTrue();
        Text(window, "TakenAt").ShouldBeNullOrEmpty();
        Snapshot(window, "network-no-service");
    }
}
