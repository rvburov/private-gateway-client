using System.Net;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Network.SingBox;
using Shouldly;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Покрытие режима VPN: любая программа, любой пользователь и протокол машины идут через туннель, а на провод пространства
/// (то, что ушло бы в сеть) выходит только соединение Xray с сервером — с блоком при обрыве и без него.
/// </summary>
public sealed partial class VpnModeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_program_user_and_protocol_leaves_the_machine_only_through_the_tunnel(bool killSwitch)
    {
        NetworkNamespace.SkipUnlessAvailable("python3", "git", "getent", "setpriv");
        await using var rig = await Rig.CreateAsync(services: true);
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o));
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition, killSwitch: killSwitch);
        // DNS Xray — обычный, по UDP через сервер: на сервере теста его отвечает свой DNS.
        request.Protection.Dns.Servers.Clear();
        request.Protection.Dns.Servers.Add("8.8.8.8");
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        var router = rig.Lan.Url.Host;

        var (sites, git) = (rig.Site.Requests, 0);
        using (var tap = await WireTap.StartAsync(rig.Netns))
        {
            // Программы root и другого пользователя (nobody) — HTTP через туннель и сервер.
            (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель");
            (await rig.Netns.RunAsync("setpriv", "--reuid=65534", "--regid=65534", "--clear-groups", "curl", "-sS", "-m", "10", Rig.Trace.ToString()))
                .ShouldBe("через туннель", "процесс другого пользователя");

            // Разрешитель ОС (getaddrinfo): DNS ОС — адрес DNS туннеля (так его ставит sing-box в systemd-resolved и адаптеру
            // Windows) или роутер из resolv.conf. Ответ — DNS теста через сервер, роутер не спрошен.
            (await rig.Netns.ResolveWithSystemAsync("example.org", IPAddress.Parse(SingBoxConfigBuilder.TunDns))).ShouldStartWith("1.1.1.1");
            (await rig.Netns.ResolveWithSystemAsync("example.com", IPAddress.Parse(router))).ShouldStartWith("1.1.1.1");
            rig.Dns!.Queries.ShouldBe(["example.org", "example.com"], ignoreOrder: true);

            // git по HTTP: сайт за сервером получил его запросы (репозитория там нет — чем кончится git, не важно).
            git = rig.Site.Requests;
            try
            {
                await rig.Netns.RunAsync("env", "HOME=/tmp", "GIT_TERMINAL_PROMPT=0", "git", "ls-remote", "http://1.1.1.1/pg.git");
            }
            catch (InvalidOperationException)
            {
                // Ответ сайта — не репозиторий.
            }

            git = rig.Site.Requests - git;

            // UDP на 443, как QUIC, — через сервер: эхо вернулось.
            (await rig.Netns.UdpEchoAsync(IPAddress.Parse("1.1.1.1"), 443)).ShouldBe("echo");
            rig.Udp!.Datagrams.ShouldBe(1);

            // Сокет с привязкой к адресу карты (кандидаты WebRTC, curl --interface): маршруты TUN по адресу источника его не отпускают.
            var card = IPAddress.Parse(rig.Definition.Host).GetAddressBytes();
            card[3] = 2;
            (await rig.Netns.FetchFromAsync(Rig.Trace, new IPAddress(card))).ShouldBe("через туннель", "сокет с адресом карты — тоже через туннель");

            // ICMP и программа с привязкой к карте мимо маршрутов TUN: вышли ли они на провод, решает провод, а не их ответ.
            await rig.Netns.PingAsync(rig.Public);
            try
            {
                await rig.Netns.FetchAsync(rig.Lan.Url, seconds: 2, device: "pgnet0");
            }
            catch (InvalidOperationException)
            {
                // Закрыто правилами.
            }

            var wire = tap.Stop();
            var server = $"4 6 {rig.Definition.Host} {rig.Definition.Port}";
            wire.ShouldContain(server, "провод слушает: соединение Xray с сервером на нем видно");
            wire.Where(line => line != server && !WireTap.IsControl(line)).ShouldBeEmpty("на проводе — только соединение Xray с сервером");
        }

        git.ShouldBeGreaterThan(0, "запросы git дошли через туннель до сайта за сервером");
        (rig.Site.Requests - sites).ShouldBeGreaterThanOrEqualTo(3 + git);
        rig.Lan.Requests.ShouldBe(0, "мимо туннеля в сеть — ничего");
    }

    [Fact]
    public async Task Dns_of_a_socket_bound_to_the_card_stays_off_the_wire_after_the_redirect()
    {
        NetworkNamespace.SkipUnlessAvailable("python3");
        await using var rig = await Rig.CreateAsync();
        // Маршрут по умолчанию — через шлюз, как в настоящей сети: без него пакет к 172.19.0.2 на карте ждал бы ARP самого адреса
        // и не уходил, и утечку было бы не увидеть.
        rig.Netns.Run(null, "ip", "route", "replace", "default", "via", rig.Definition.Host, "dev", "pgnet0");
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o));
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        // Локальная сеть разрешена, DNS провайдера закрыт: DNS к роутеру перенаправляется в туннель (DNAT).
        await service.Client.ConnectAsync(VpnRequest(rig.Definition, allowLan: true), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        using var tap = await WireTap.StartAsync(rig.Netns);
        try
        {
            // Сокет с привязкой к карте (как dnsmasq «сервер@интерфейс»): после перенаправления он все равно уходил бы в карту.
            await rig.Netns.ResolveAsync("example.com", IPAddress.Parse(rig.Lan.Url.Host), seconds: 2, device: "pgnet0");
        }
        catch (InvalidOperationException)
        {
            // Закрыто — так и должно быть.
        }

        tap.Stop().Where(line => line.Split(' ') is [_, "6" or "17", _, "53"]).ShouldBeEmpty("DNS мимо туннеля на провод не вышел");
    }
}
