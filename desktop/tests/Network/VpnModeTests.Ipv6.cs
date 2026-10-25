using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// IPv6 в режиме VPN: через туннель, если сервер его открывает; иначе закрыт строгими маршрутами — и для сокетов,
/// привязанных к адресу, — или, если «Блокировать IPv6 вне туннеля» выключено, идет мимо туннеля, и это видно. Нужен
/// IPv6 в ядре машины: в контейнере облака его нет, тесты идут в CI от root.
/// </summary>
public sealed partial class VpnModeTests
{
    // Что называет проверочный адрес по IPv6 через сервер с IPv6.
    private const string TunnelIp6 = "2001:db8:24::24";

    [Fact]
    public async Task Server_with_ipv6_carries_ipv6_of_the_machine_through_the_tunnel()
    {
        NetworkNamespace.SkipUnlessIpv6();
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n", page6: $"fl=1\nip={TunnelIp6}\n");
        // У машины своя сеть IPv6 и маршрут уже маршрута по умолчанию — как от объявления роутера в чужом Wi-Fi.
        var (host6, _) = rig.Netns.LinkIpv6();
        rig.Netns.Run(null, "ip", "-6", "route", "add", "2606:4700::/32", "dev", "pgnet0");
        using var lan6 = new LocalSite("своя сеть IPv6", host6);
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Trace = Rig.Trace, Trace6 = Rig.Trace6, ExitIp = rig.ExitIpAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        (connected.Protection.Ipv6Tunnel, connected.Protection.Ipv6).ShouldBe((true, V1.CheckState.Ok));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер открывает IPv6 — IPv6 идет через туннель");
        (await rig.Netns.RunAsync("ip", "-6", "addr", "show", "dev", "pgtun0")).ShouldContain(SingBoxConfigBuilder.TunAddress6.Split('/')[0]);
        (await rig.Netns.CurlAsync(Rig.Trace6)).ShouldBe($"fl=1\nip={TunnelIp6}\n",
            "IPv6 машины — через TUN, Xray и сервер, даже с более узким маршрутом мимо туннеля");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(lan6.Url, seconds: 3),
            "своя сеть IPv6 не разрешена — она в туннеле, и Xray ее закрывает, как и IPv4");
        lan6.Requests.ShouldBe(0);
        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
        (row.State, row.Reason).ShouldBe((V1.CheckState.Ok, "tunnel"));

        // После паузы туннель поднимается снова — с выводом прошлой проверки, без новой.
        await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct);
        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null))[^1].Protection.Ipv6Tunnel.ShouldBeFalse("на паузе туннеля нет");
        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);
        var resumed = (await ServiceIpcTests.ReadUntilAsync(watch, s =>
            s.State == V1.ConnectionState.Connected && s.CaptivePauseUntil is null || s.State == V1.ConnectionState.Error))[^1];
        resumed.State.ShouldBe(V1.ConnectionState.Connected, resumed.Error?.Message);
        resumed.Protection.Ipv6Tunnel.ShouldBeTrue();
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message == "Сервер открывает IPv6 — IPv6 идет через туннель").ShouldBe(1);
        (await rig.Netns.CurlAsync(Rig.Trace6)).ShouldBe($"fl=1\nip={TunnelIp6}\n");
    }

    [Fact]
    public async Task Server_without_ipv6_closes_ipv6_outside_the_tunnel_unless_it_is_allowed()
    {
        NetworkNamespace.SkipUnlessIpv6();
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n", serverIpv6: false);
        var (host6, inside6) = rig.Netns.LinkIpv6();
        using var site6 = new LocalSite("IPv6 мимо туннеля", host6);
        (await rig.Netns.CurlAsync(site6.Url)).ShouldBe("IPv6 мимо туннеля", "без туннеля у машины есть IPv6");
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Trace = Rig.Trace, Trace6 = Rig.Trace6, ExitIp = rig.ExitIpAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        // Блок при обрыве и строгие маршруты выключены: IPv6 закрывает только «Блокировать IPv6 вне туннеля».
        var closed = await ConnectAsync(blockIpv6: true);

        (closed.Protection.Ipv6Tunnel, closed.Protection.Ipv6).ShouldBe((false, V1.CheckState.Ok));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не открывает IPv6 — IPv6 закрыт, мимо туннеля он не уйдет");
        (await rig.Netns.RunAsync("ip", "-6", "addr", "show", "dev", "pgtun0")).ShouldNotContain("inet6 fdfe", Case.Sensitive, "у TUN нет IPv6");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(site6.Url, seconds: 3), "IPv6 мимо туннеля закрыт");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchFromAsync(site6.Url, inside6),
            "и для сокета, привязанного к адресу IPv6 машины, как у WebRTC");
        site6.Requests.ShouldBe(1, "до сайта дошел только запрос без туннеля");
        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe($"fl=1\nip={TunnelIp}\n", "IPv4 — через туннель");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        var open = await ConnectAsync(blockIpv6: false);

        (open.Protection.Ipv6Tunnel, open.Protection.Ipv6).ShouldBe((false, V1.CheckState.Warning), "IPv6 мимо туннеля — не скрыт, и это видно");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не открывает IPv6 — «Блокировать IPv6 вне туннеля» выключено");
        (await rig.Netns.CurlAsync(site6.Url)).ShouldBe("IPv6 мимо туннеля", "IPv6 вне туннеля разрешен — идет напрямую");
        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
        (row.State, row.Reason).ShouldBe((V1.CheckState.Warning, "outside"));

        async Task<V1.Status> ConnectAsync(bool blockIpv6)
        {
            await service.Client.ConnectAsync(VpnRequest(rig.Definition, killSwitch: false, strictRoutes: false, blockIpv6: blockIpv6), cancellationToken: Ct);
            var status = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            status.State.ShouldBe(V1.ConnectionState.Connected, status.Error?.Message);
            return status;
        }
    }
}
