using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
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
        // У машины своя сеть IPv6 (глобальный адрес на линии) и маршрут уже маршрута по умолчанию — как от объявления роутера
        // в чужом Wi-Fi.
        var (host6, inside6) = rig.Netns.LinkIpv6();
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
        (await rig.Netns.CurlAsync(lan6.Url)).ShouldBe($"fl=1\nip={TunnelIp6}\n",
            "своя сеть IPv6 с глобальным адресом (как от провайдера) — тоже через туннель и сервер, а не напрямую");
        (await rig.Netns.FetchFromAsync(Rig.Trace6, inside6)).ShouldBe($"fl=1\nip={TunnelIp6}\n",
            "сокет с адресом IPv6 карты (кандидаты WebRTC) — тоже через туннель, правила по адресу источника его не отпускают");
        lan6.Requests.ShouldBe(0);
        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
        (row.State, row.Reason).ShouldBe((V1.CheckState.Ok, "tunnel"));

        // После паузы туннель поднимается снова — с выводом прошлой проверки, без новой.
        await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);
        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);
        var resumed = (await ServiceIpcTests.ReadUntilAsync(watch, s =>
            s.State == V1.ConnectionState.Connected && s.CaptivePauseUntil is null || s.State == V1.ConnectionState.Error))[^1];
        resumed.State.ShouldBe(V1.ConnectionState.Connected, resumed.Error?.Message);
        resumed.Protection.Ipv6Tunnel.ShouldBeTrue();
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message == "Сервер открывает IPv6 — IPv6 идет через туннель").ShouldBe(1);
        (await rig.Netns.CurlAsync(Rig.Trace6)).ShouldBe($"fl=1\nip={TunnelIp6}\n");

        // Реальный адрес IPv6, измеренный до туннеля, — тот же, что сайты видят через туннель: так замер видит IPv6 мимо туннеля
        // с IPv6 в туннеле. Реальный адрес мерится при подключении — переподключение.
        using var trace6 = rig.DirectTrace6($"fl=1\nip={TunnelIp6}\n", host6);
        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error);
        var leak = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
        (leak.State, leak.Reason).ShouldBe((V1.CheckState.Warning, "leak"), "сайты видят реальный адрес IPv6, хотя IPv6 идет в туннель");
        trace6.Requests.ShouldBe(1, "реальный адрес IPv6 измерен напрямую один раз — до туннеля");
    }

    [Fact]
    public async Task Server_without_ipv6_closes_ipv6_outside_the_tunnel_unless_it_is_allowed()
    {
        NetworkNamespace.SkipUnlessIpv6();
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n", serverIpv6: false);
        var (host6, inside6) = rig.Netns.LinkIpv6();
        using var site6 = new LocalSite("IPv6 мимо туннеля", host6);
        (await rig.Netns.CurlAsync(site6.Url)).ShouldBe("IPv6 мимо туннеля", "без туннеля у машины есть IPv6");
        // Проверочный адрес по IPv6 мимо туннеля — сайт на адресе машины, он называет адрес IPv6 пространства, как Cloudflare
        // называет реальный адрес: замер IPv6 службы — настоящий.
        using var trace6 = rig.DirectTrace6($"fl=1\nip={inside6}\n", host6);
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Trace = Rig.Trace, Trace6 = Rig.Trace6, ExitIp = rig.ExitIpAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        // Блок при обрыве и строгие маршруты выключены: IPv6 закрывает только «Блокировать IPv6 вне туннеля».
        var closed = await ConnectAsync(blockIpv6: true);

        (closed.Protection.Ipv6Tunnel, closed.Protection.Ipv6).ShouldBe((false, V1.CheckState.Ok));
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.Ipv6Outside).ShouldBe((true, false), "правила «только туннель» держат и IPv6");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не открывает IPv6 — IPv6 закрыт, мимо туннеля он не уйдет");
        (await rig.Netns.RunAsync("ip", "-6", "addr", "show", "dev", "pgtun0")).ShouldNotContain("inet6 fdfe", Case.Sensitive, "у TUN нет IPv6");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(site6.Url, seconds: 3), "IPv6 мимо туннеля закрыт");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchFromAsync(site6.Url, inside6),
            "и для сокета, привязанного к адресу IPv6 машины, как у WebRTC");
        site6.Requests.ShouldBe(1, "до сайта дошел только запрос без туннеля");
        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe($"fl=1\nip={TunnelIp}\n", "IPv4 — через туннель");
        (await Ipv6RowAsync()).ShouldBe((V1.CheckState.Ok, string.Empty), "замер: проверочный адрес по IPv6 не ответил — IPv6 закрыт");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Message.Contains("ваш адрес IPv6", StringComparison.Ordinal),
            "IPv6 закрыт — замер утечки не находит");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        var open = await ConnectAsync(blockIpv6: false);

        (open.Protection.Ipv6Tunnel, open.Protection.Ipv6).ShouldBe((false, V1.CheckState.Warning), "IPv6 мимо туннеля — не скрыт, и это видно");
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.Ipv6Outside).ShouldBe((true, true), "правила «только туннель» IPv6 мимо туннеля не закрывают");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не открывает IPv6 — «Блокировать IPv6 вне туннеля» выключено");
        (await rig.Netns.CurlAsync(site6.Url)).ShouldBe("IPv6 мимо туннеля", "IPv6 вне туннеля разрешен — идет напрямую");
        (await Ipv6RowAsync()).ShouldBe((V1.CheckState.Warning, "outside"), "замер видит реальный адрес IPv6, а причина — настройка, она и названа");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Проверка утечек: сайты видят ваш адрес IPv6 — IPv6 идет мимо туннеля");

        // С блоком при обрыве IPv6 мимо туннеля закрыт и так — предупреждения нет.
        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        var blocked = await ConnectAsync(blockIpv6: false, killSwitch: true);

        (blocked.Protection.Ipv6Tunnel, blocked.Protection.Ipv6).ShouldBe((false, V1.CheckState.Ok), "блок при обрыве держит IPv6 мимо туннеля");
        rig.KillSwitch.Last!.Ipv6Outside.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(site6.Url, seconds: 3));
        (await Ipv6RowAsync()).ShouldBe((V1.CheckState.Ok, string.Empty));
        trace6.Requests.ShouldBeGreaterThan(0, "проверочный адрес мимо туннеля отвечал, пока IPv6 был открыт");

        async Task<(V1.CheckState, string)> Ipv6RowAsync()
        {
            var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
                .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
            return (row.State, row.Reason);
        }

        async Task<V1.Status> ConnectAsync(bool blockIpv6, bool killSwitch = false)
        {
            await service.Client.ConnectAsync(VpnRequest(rig.Definition, killSwitch: killSwitch, strictRoutes: false, blockIpv6: blockIpv6), cancellationToken: Ct);
            var status = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            status.State.ShouldBe(V1.ConnectionState.Connected, status.Error?.Message);
            return status;
        }
    }

    [Fact]
    public async Task Ipv6_seen_by_sites_outside_the_tunnel_is_a_leak_on_the_check()
    {
        // IPv6 в ядре не нужен: ответ проверочного адреса по IPv6 без прокси — подделка, как если бы IPv6 машины уходил мимо
        // туннеля с реальным адресом, хотя «Блокировать IPv6 вне туннеля» включено.
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n");
        string? seen6 = "2001:db8::7";
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Trace = Rig.Trace,
            Trace6 = Rig.Trace6,
            ExitIp = (probe, ct) => probe.Target == Rig.Trace6 && probe.Credentials is null ? Task.FromResult<string?>(Volatile.Read(ref seen6)) : rig.ExitIpAsync(probe, ct),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        // Проверка утечек после подключения: реальный адрес IPv6 измерен до туннеля, и его же сайты видят после.
        var leaked = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected && s.Protection.Ipv6 == V1.CheckState.Warning,
            service.Journal))[^1];
        leaked.Protection.Ipv6Tunnel.ShouldBeFalse("IPv6 идет мимо туннеля — и строка на главной так и говорит");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Проверка утечек: сайты видят ваш адрес IPv6 — IPv6 идет мимо туннеля");
        (await Ipv6RowAsync()).ShouldBe((V1.CheckState.Warning, "leak"), "настройки IPv6 закрывают, а замер видит реальный адрес");

        // IPv6 закрылся (проверочный адрес не отвечает) — строка снова по состоянию.
        Volatile.Write(ref seen6, null);
        var row = await Ipv6RowAsync();
        row.Item1.ShouldBe(V1.CheckState.Ok);
        row.Item2.ShouldNotBe("leak");
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).Protection.Ipv6.ShouldBe(V1.CheckState.Ok);

        async Task<(V1.CheckState, string)> Ipv6RowAsync()
        {
            var result = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
                .Single(r => r.Id == DiagnosticCheck.Ipv6.ToContractId());
            return (result.State, result.Reason);
        }
    }

    [Theory]
    [InlineData("2001:db8::7", "2001:db8::7", true, true)]
    [InlineData("2001:0db8:0:0::7", "2001:db8::7", true, true)]
    [InlineData("2001:db8::7", TunnelIp6, true, false)]
    [InlineData(null, TunnelIp6, true, false)]
    [InlineData(null, "2001:db8::9", false, true)]
    [InlineData("2001:db8::7", null, false, null)]
    [InlineData("2001:db8::7", TunnelIp, false, null)]
    public void Ipv6_leaks_when_sites_see_the_real_address_or_any_address_without_ipv6_in_the_tunnel(string? direct6, string? seen6, bool ipv6Tunnel, bool? leaks) =>
        Gateway.Ipv6Leaks(direct6, seen6, ipv6Tunnel).ShouldBe(leaks,
            "у туннеля без IPv6 любой ответ по IPv6 пришел мимо него; через туннель с IPv6 — утечка, только если виден реальный адрес");

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void Rules_open_ipv6_outside_only_when_the_user_let_it_and_the_tunnel_does(bool killSwitch, bool blockIpv6, bool tunnelLets, bool open) =>
        Gateway.Ipv6OutsideOpen(killSwitch, ProtectionSettings.Default with { BlockIpv6 = blockIpv6 }, tunnelLets).ShouldBe(open,
            "сервер по IPv6 при адаптере без IPv6: туннель пропускает IPv6 мимо себя, но «Блокировать IPv6 вне туннеля» включено — закрыт");

    [Theory]
    [InlineData(false, false, true, "ipv6_dns")]
    [InlineData(false, true, true, null)]
    [InlineData(true, false, true, null)]
    [InlineData(false, false, false, null)]
    public void Dns_row_tells_when_dns_over_open_ipv6_goes_around_the_tunnel(bool blockOutsideDns, bool blockIpv6, bool ipv6Open, string? reason) =>
        Gateway.DnsOutside(ProtectionSettings.Default with { BlockOutsideDns = blockOutsideDns, BlockIpv6 = blockIpv6 }, ipv6Open).ShouldBe(reason,
            "IPv6 мимо туннеля открыт, а DNS провайдера не закрыт — запросы по IPv6 могут уйти к нему");
}
