using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Режим VPN в службе: блок при обрыве до запуска ядер, туннель, блок при сбое и после остановки
/// службы, снятие при отключении и отказе. Логика — с подделкой блока; весь путь — в своем сетевом
/// пространстве Linux с настоящими nftables, sing-box и Xray.
/// </summary>
public sealed class VpnModeTests
{
    // Что называет проверочный адрес: через туннель — IP сервера, напрямую — реальный.
    private const string TunnelIp = "203.0.113.24";
    private const string RealIp = "198.51.100.7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vpn_without_a_kill_switch_on_this_os_is_not_ready()
    {
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, SingBoxPath = XrayKit.SingBox });
        var (definition, _) = XrayKit.Pair("vless", 443);

        var reply = await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, ErrorCodes.VpnNotReady), "VPN без блока при обрыве не обещается");
        reply.Error.Message.ShouldContain("нет блока при обрыве");
    }

    [Fact]
    public async Task Block_left_after_a_crash_is_shown_and_lifted_by_turning_off()
    {
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch });

        var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();

        (status.State, status.Protection).ShouldBe((ConnectionState.Blocked, ProtectionChecks.For(ConnectionState.Blocked)),
            "правила остались — трафик заблокирован, и окно должно это сказать");
        service.Journal.Tail(1).ShouldHaveSingleItem().Message.ShouldContain("остался с прошлого запуска");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off);
        (killSwitch.IsEnabled, killSwitch.Disabled).ShouldBe((false, 1));
    }

    [Fact]
    public async Task Proxy_mode_lifts_a_block_left_by_vpn()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch });
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, XrayKit.FreePort()), cancellationToken: Ct);

        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected))[^1].Mode.ShouldBe(V1.Mode.Proxy);
        killSwitch.IsEnabled.ShouldBeFalse("ядро прокси соединяется с сервером мимо туннеля — блок VPN ему мешает");
    }

    [Fact]
    public async Task Server_name_that_is_not_found_is_refused_and_lifts_the_block()
    {
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = killSwitch });
        var (definition, _) = XrayKit.Pair("vless", 443);

        // .invalid не разрешается никогда (RFC 2606).
        var reply = await service.Client.ConnectAsync(VpnRequest(definition with { Host = "server.invalid" }), cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, ErrorCodes.ServerNotFound));
        killSwitch.IsEnabled.ShouldBeFalse("отказ снимает блок: под ним имя сервера не найти, а следующее подключение найдет");
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore().Protection
            .ShouldBe(ProtectionChecks.For(ConnectionState.Off));
    }

    [Fact]
    public async Task Block_that_cannot_be_set_is_an_error_without_promises()
    {
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch { Fails = true };
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = killSwitch });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var (definition, _) = XrayKit.Pair("vless", 443);

        await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

        var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error))[^1];
        error.Error.Code.ShouldBe(ErrorCodes.KillSwitchFailed);
        error.ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off), "блока нет — трафик идет напрямую, проверки этого не скрывают");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Source == "xray", "без блока ядра не запускаются");
    }

    [Fact]
    public async Task Block_comes_before_the_cores_and_stays_when_they_fail()
    {
        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch();
        // Вместо Xray — sing-box: аргументов Xray он не знает и сразу выходит, до TUN дело не доходит.
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            XrayPath = XrayKit.SingBox,
            CanVpn = true,
            SingBoxPath = XrayKit.SingBox,
            KillSwitch = killSwitch,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var (definition, _) = XrayKit.Pair("vless", 8443);

        await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

        var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error))[^1];
        (error.Error.Code, error.ToCore().Protection).ShouldBe((ErrorCodes.CoreFailed, ProtectionChecks.For(ConnectionState.Error)),
            "ядро не поднялось — трафик остановлен блоком");
        killSwitch.IsEnabled.ShouldBeTrue();
        var policy = killSwitch.Policy.ShouldNotBeNull();
        (policy.TunnelInterface, policy.AllowLan, policy.LanDiscovery, policy.BlockOutsideDns)
            .ShouldBe((SingBoxConfigBuilder.InterfaceName is { Length: > 0 } name ? name : null, false, false, true));
        policy.Servers.ShouldBe([new KillSwitchEndpoint(IPAddress.Loopback, 8443)], "мимо туннеля — только адрес и порт сервера");
        var journal = service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message).ToList();
        journal.FindIndex(m => m.StartsWith("Блок при обрыве включен", StringComparison.Ordinal))
            .ShouldBeLessThan(journal.FindIndex(m => m.Contains("вышел при запуске", StringComparison.Ordinal)), "блок — до запуска ядер");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        killSwitch.IsEnabled.ShouldBeFalse("отключение из «Ошибки» снимает блок");
    }

    [Fact]
    public async Task Vpn_with_the_block_turned_off_lifts_the_old_block()
    {
        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            XrayPath = XrayKit.SingBox,
            CanVpn = true,
            SingBoxPath = XrayKit.SingBox,
            KillSwitch = killSwitch,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var (definition, _) = XrayKit.Pair("vless", 8443);

        await service.Client.ConnectAsync(VpnRequest(definition, killSwitch: false), cancellationToken: Ct);

        var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error))[^1];
        killSwitch.IsEnabled.ShouldBeFalse("блок выключен в настройках — оставшиеся правила снимаются");
        error.ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off), "без блока ядро упало — трафик идет напрямую");
    }

    [Fact]
    public void Server_addresses_follow_the_ipv6_settings()
    {
        IPAddress[] found = [IPAddress.Parse("2001:db8::20"), IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::21")];

        Gateway.PickAddresses(found, ProtectionSettings.Default).ShouldBe([IPAddress.Parse("198.51.100.20")], "IPv6 закрыт по умолчанию");
        Gateway.PickAddresses(found, ProtectionSettings.Default with { BlockIpv6 = false })
            .ShouldBe([IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20"), IPAddress.Parse("2001:db8::21")], "первым — IPv4");
        Gateway.PickAddresses(found, ProtectionSettings.Default with { BlockIpv6 = false, Addresses = AddressPreference.PreferIpv6 })
            .ShouldBe([IPAddress.Parse("2001:db8::20"), IPAddress.Parse("2001:db8::21"), IPAddress.Parse("198.51.100.20")]);
        Gateway.PickAddresses(found, ProtectionSettings.Default with { BlockIpv6 = false, Addresses = AddressPreference.Ipv4Only })
            .ShouldBe([IPAddress.Parse("198.51.100.20")]);
    }

    [Fact]
    public async Task Pause_opens_the_network_without_the_tunnel_and_brings_the_tunnel_and_the_block_back()
    {
        await using var rig = await Rig.CreateAsync();

        var (netns, lan, definition, killSwitch) = (rig.Netns, rig.Lan, rig.Definition, rig.KillSwitch);
        await using var service = await RunningService.StartAsync(options: rig.Vpn);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var hour = new V1.CaptivePauseRequest { Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromHours(1)) };

        (await service.Client.PauseForCaptivePortalAsync(hour, cancellationToken: Ct)).Until.ShouldNotBeNull("в режиме VPN пауза есть");

        var paused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null))[^1].ToCore();
        (paused.State, paused.Protection).ShouldBe((ConnectionState.Connected, ProtectionChecks.Paused));
        killSwitch.IsEnabled.ShouldBeFalse("на паузе блок снят — иначе страница входа в сеть не откроется");
        (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля", "страница входа в сеть открывается напрямую");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 3), "туннель на паузе стоит");

        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error && s.CaptivePauseUntil is null);
        states.Select(s => s.State).ShouldContain(V1.ConnectionState.Connecting, "туннель поднимается заново, через «Подключение»");
        states[^1].State.ShouldBe(V1.ConnectionState.Connected, states[^1].Error?.Message);
        (killSwitch.IsEnabled, killSwitch.Enables).ShouldBe((true, 4), "защита вернулась с блоком — снова до ядер и после подъема адаптера");
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3));

        await service.Client.PauseForCaptivePortalAsync(
            new V1.CaptivePauseRequest { Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(1)) }, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);

        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected && s.CaptivePauseUntil is null))[^1]
            .ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Connected), "пауза кончилась сама — защита снова полная");
        killSwitch.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Vpn_goes_through_the_tunnel_behind_the_block_and_the_block_outlives_failures()
    {
        await using var rig = await Rig.CreateAsync();

        var (netns, lan, definition, killSwitch) = (rig.Netns, rig.Lan, rig.Definition, rig.KillSwitch);
        GatewayOptions Vpn(GatewayOptions o) => rig.Vpn(o);

        await using (var service = await RunningService.StartAsync(options: Vpn))
        {
            using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

            var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
            connected.ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Connected));
            (killSwitch.IsEnabled, killSwitch.Enables).ShouldBe((true, 2), "блок — до ядер и еще раз, когда адаптер туннеля появился (WFP открывает его по LUID)");
            (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
            await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3), "мимо туннеля — только сервер");

            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

            killSwitch.IsEnabled.ShouldBeFalse("отключение снимает блок");
            (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля");

            await service.Client.ConnectAsync(VpnRequest(definition, killSwitch: false), cancellationToken: Ct);

            var unguarded = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            unguarded.State.ShouldBe(V1.ConnectionState.Connected, unguarded.Error?.Message);
            unguarded.ToCore().Protection.KillSwitch.ShouldBe(CheckState.Off, "блок выключен в настройках — проверка не делает вид, что он есть");
            killSwitch.IsEnabled.ShouldBeFalse();
            (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        }

        // Вместо Xray — sing-box: аргументов Xray он не знает и сразу выходит, туннель не поднимается.
        await using (var failing = await RunningService.StartAsync(options: o => Vpn(o) with { XrayPath = XrayKit.SingBox }))
        {
            using var watch = failing.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            await failing.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

            var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error))[^1];
            (error.Error.Code, error.ToCore().Protection).ShouldBe((ErrorCodes.CoreFailed, ProtectionChecks.For(ConnectionState.Error)),
                "ядро не поднялось — трафик остановлен блоком, а не пошел напрямую");
            await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3));
        }

        killSwitch.IsEnabled.ShouldBeTrue("служба остановилась с включенной защитой — блок остается");
        await using (var restarted = await RunningService.StartAsync(options: Vpn))
        {
            (await restarted.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Blocked);

            await restarted.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

            killSwitch.IsEnabled.ShouldBeFalse();
            (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля");
        }
    }

    [Fact]
    public async Task Split_mode_sends_chosen_programs_around_the_tunnel_and_nothing_else()
    {
        await using var rig = await Rig.CreateAsync();
        var (netns, lan, definition) = (rig.Netns, rig.Lan, rig.Definition);
        var curl = VpnTunnel.RealPath((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':')
            .Select(dir => Path.Combine(dir, "curl")).First(File.Exists));
        var routing = new RoutingSettings(RoutingMode.Split, [new BypassApp(curl, "curl")], []);
        await using var service = await RunningService.StartAsync(options: rig.Vpn);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(definition, routing: routing), cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        connected.ToCore().Protection.AllTraffic.ShouldBe(CheckState.Warning, "раздельный режим — «Весь трафик» частично");
        (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.AllTraffic.ToContractId()).Reason.ShouldBe("split", "«Проверка» объясняет, почему частично");
        (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля", "программа из списка идет напрямую — и блок при обрыве ее пропускает");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.FetchAsync(lan.Url, seconds: 3),
            "другая программа — через туннель, а там локальная сеть закрыта");
        (await netns.FetchAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "остальное — через туннель и сервер");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.EndsWith("только сервер и прямой выход sing-box", StringComparison.Ordinal));

        await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

        // Проверка прислала прежнее «Защищено» со временем проверки — новое подключение идет после «Подключения».
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting);
        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1]
            .ToCore().Protection.AllTraffic.ShouldBe(CheckState.Ok, "«Весь трафик» — снова весь");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3), "без раздельного режима обхода нет");
    }

    [Fact]
    public async Task Direct_exit_of_xray_leaves_through_sing_box_instead_of_looping_into_the_tunnel()
    {
        await using var rig = await Rig.CreateAsync();

        // Адрес документации Xray считает локальным (geoip:private), а в исключениях TUN его нет: прямой
        // выход Xray к нему идет в TUN — и только правило sing-box по процессу Xray выводит его наружу.
        using var outside = new LocalSite("напрямую", rig.Public);
        await using var service = await RunningService.StartAsync(options: rig.Vpn);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition, allowLan: true), cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        (await rig.Netns.FetchAsync(outside.Url)).ShouldBe("напрямую", "локальная сеть разрешена — Xray отвечает напрямую, без петли через TUN");
        (await rig.Netns.FetchAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
        outside.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Vpn_keeps_the_block_while_reconnecting_and_blocks_when_the_server_is_gone()
    {
        await using var rig = await Rig.CreateAsync();
        var (netns, lan, killSwitch) = (rig.Netns, rig.Lan, rig.KillSwitch);

        // Проверочный адрес — по HTTP: сервер теста отвечает своим сайтом на любой адрес.
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"),
            options: o => rig.Vpn(o) with { Reconnect = ReconnectTests.Fast, Probe = rig.ProbeAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition);
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        rig.StopServer();

        var reconnecting = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting))[^1].ToCore();
        (reconnecting.ServerId, reconnecting.Protection.AllTraffic).ShouldBe(("srv-1", CheckState.Waiting), "к тому же серверу, трафик на паузе");
        killSwitch.IsEnabled.ShouldBeTrue("пока служба ищет связь, блок держит трафик");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 2), "мимо туннеля — ничего");

        var enables = killSwitch.Enables;
        await rig.StartServerAsync();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "связь вернулась — снова через туннель");
        killSwitch.Enables.ShouldBeGreaterThan(enables, "новый адаптер туннеля — блок обновлен (на Windows разрешение по LUID)");

        rig.StopServer();

        var blocked = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Blocked or V1.ConnectionState.Error))[^1];
        blocked.State.ShouldBe(V1.ConnectionState.Blocked, "попытки кончились — «Блокировка», другой сервер сам не выбирается");
        killSwitch.IsEnabled.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 2));

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        killSwitch.IsEnabled.ShouldBeFalse("защиту выключил пользователь — блок снят");
        (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля");
    }

    [Fact]
    public async Task Other_vpn_switched_on_with_a_network_change_stops_the_connection_and_keeps_the_block()
    {
        await using var rig = await Rig.CreateAsync();
        var network = new FakeNetwork();

        // Пробы раз в час: чужой VPN успевает заметить только смена сети.
        var hour = TimeSpan.FromHours(1);
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            Routes = rig.Routes, Network = network, Probe = rig.ProbeAsync, Reconnect = new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        // Чужой VPN — как WireGuard: свой адаптер и маршрут ко всему интернету в своей таблице.
        rig.StartOtherVpn();
        network.Raise();

        var stopped = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Off))[^1];
        (stopped.State, stopped.Error.Code).ShouldBe((V1.ConnectionState.Error, ErrorCodes.OtherVpn), "окно откроет «Работает другой VPN»");
        rig.KillSwitch.IsEnabled.ShouldBeTrue("трафик стоит, пока пользователь не решит");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2));
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 2), "ядра остановлены");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
            e.Message == "PGC-NET-010: включился другой VPN (pgother0) — подключение остановлено, блок при обрыве держит трафик");

        // «Продолжить (не рекомендуется)»: выбор держится и на ходу.
        var despite = VpnRequest(rig.Definition);
        despite.AllowOtherVpn = true;
        await service.Client.ConnectAsync(despite, cancellationToken: Ct);
        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        network.Raise();
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State
            .ShouldBe(V1.ConnectionState.Connected, "пользователь разрешил другой VPN — до отключения");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сеть машины сменилась — проверка связи с сервером",
            "смена сети дошла до службы — другой VPN пропущен по выбору, сервер проверен");
    }

    [Fact]
    public async Task Other_vpn_that_only_adds_routes_is_found_by_the_regular_check()
    {
        await using var rig = await Rig.CreateAsync();

        // Адаптер чужого VPN уже есть (как tailscale0), выход через него включается одним маршрутом — адреса
        // не меняются, и смены сети нет: находит проба раз в секунду.
        rig.Netns.Run(null, "ip", "tuntap", "add", "dev", "pgother0", "mode", "tun");
        rig.Netns.Run(null, "ip", "link", "set", "pgother0", "up");
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            Routes = rig.Routes, Probe = rig.ProbeAsync,
            Reconnect = new ReconnectTiming(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, "адаптер без маршрута ко всему интернету — не VPN");
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Connected);

        rig.StartOtherVpn();

        var stopped = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Off))[^1];
        (stopped.State, stopped.Error.Code).ShouldBe((V1.ConnectionState.Error, ErrorCodes.OtherVpn));
    }

    [Fact]
    public async Task Rules_left_by_a_killed_sing_box_are_removed_when_the_tunnel_stops_and_when_the_service_starts()
    {
        await using var rig = await Rig.CreateAsync();
        await using (var service = await RunningService.StartAsync(options: rig.Vpn))
        {
            using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            var request = VpnRequest(rig.Definition);
            request.Connection.Reconnect = V1.ReconnectPolicy.Never;
            await service.Client.ConnectAsync(request, cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
            rig.Netns.Run(null, "ip", "rule", "show").ShouldContain("lookup 20551", Case.Sensitive, "у туннеля своя таблица, а не 2022 sing-box по умолчанию");

            // Убитый sing-box за собой не убирает.
            using (var singBox = Process.GetProcessById(SingBoxProcessId(service)))
            {
                singBox.Kill();
            }

            var failed = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Off))[^1];
            failed.Error.Code.ShouldBe(ErrorCodes.CoreFailed);

            // «Ошибка» видна сразу, ядра служба останавливает следом — и убирает правила.
            var started = Stopwatch.StartNew();
            while (rig.Netns.Run(null, "ip", "rule", "show").Contains("20551", StringComparison.Ordinal) && started.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
            }

            rig.Netns.Run(null, "ip", "rule", "show").ShouldNotContain("20551", Case.Sensitive, "туннель остановлен — его правила убраны");
            service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.StartsWith("Убраны правила маршрутизации, оставшиеся от sing-box", StringComparison.Ordinal));
        }

        // Служба упала вместе с sing-box — правила остались до следующего запуска.
        rig.Netns.Run(null, "ip", "rule", "add", "priority", "9048", "lookup", "20551");

        await using var next = await RunningService.StartAsync(options: rig.Vpn);

        rig.Netns.Run(null, "ip", "rule", "show").ShouldNotContain("20551");
        next.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Убраны правила маршрутизации, оставшиеся от sing-box: 1");
    }

    [Fact]
    public async Task Boot_protection_blocks_from_the_service_start_until_the_user_connects_or_turns_it_off()
    {
        var file = Path.Combine(Path.GetTempPath(), "pg-boot-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            BootBlock.Save(file, new KillSwitchPolicy("pgtun0", [new(IPAddress.Parse("198.51.100.20"), 443)], AllowLan: true, BlockOutsideDns: false,
                Direct: new DirectExit("/opt/pg/sing-box", 0x5047)));
            var killSwitch = new FakeKillSwitch();

            // Как после перезагрузки: правил нет, служба только что запустилась.
            await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = file });

            (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Blocked);
            killSwitch.Policy.ShouldNotBeNull();
            (killSwitch.Policy.TunnelInterface, killSwitch.Policy.Direct, killSwitch.Policy.Persistent).ShouldBe((null, null, true),
                "туннеля и sing-box при запуске нет; блок постоянный, как был");
            killSwitch.Policy.Servers.ShouldBe([new KillSwitchEndpoint(IPAddress.Parse("198.51.100.20"), 443)], "к серверу — можно: подключиться есть к чему");
            (killSwitch.Policy.AllowLan, killSwitch.Policy.BlockOutsideDns).ShouldBe((true, false), "локальная сеть и DNS — по настройкам, с которыми выключилась машина");
            service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.StartsWith("Защита при загрузке: защита была включена", StringComparison.Ordinal));

            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

            (killSwitch.IsEnabled, File.Exists(file)).ShouldBe((false, false), "защиту выключили — после следующей перезагрузки блока нет");
        }
        finally
        {
            File.Delete(file);
        }

        File.WriteAllText(file, "{испорчен");
        try
        {
            var killSwitch = new FakeKillSwitch();
            await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = file });

            (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off, "файл не прочитать — блока нет");
            killSwitch.IsEnabled.ShouldBeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Vpn_with_boot_protection_keeps_the_block_through_a_reboot()
    {
        await using var rig = await Rig.CreateAsync();
        var file = Path.Combine(Path.GetTempPath(), "pg-boot-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            await using (var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { BootBlock = file }))
            {
                using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
                var off = VpnRequest(rig.Definition);
                off.Protection.BootProtection = false;
                await service.Client.ConnectAsync(off, cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

                File.Exists(file).ShouldBeFalse("«Защита при загрузке» выключена — блок перезагрузку не переживет");
                rig.KillSwitch.Last!.Persistent.ShouldBeFalse("и фильтры WFP — не постоянные");

                await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

                BootBlock.Load(file)!.Servers.ShouldHaveSingleItem().Port.ShouldBe(rig.Definition.Port, "запомнен сервер этого подключения");
                rig.KillSwitch.Last!.Persistent.ShouldBeTrue("на Windows — постоянные фильтры: стоят с загрузки, до старта службы");

                (await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) }, cancellationToken: Ct))
                    .Until.ShouldNotBeNull();
                File.Exists(file).ShouldBeTrue("пауза для входа в Wi-Fi защиту не выключает — после перезагрузки блок вернется");
                await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected && s.CaptivePauseUntil is null);
            }

            // Перезагрузка: служба остановилась с защитой, правила nftables ушли вместе с машиной.
            rig.KillSwitch.Disable();
            (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля", "до запуска службы правил нет (nftables не переживает перезагрузку)");

            await using var next = await RunningService.StartAsync(options: o => rig.Vpn(o) with { BootBlock = file });

            (await next.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Blocked);
            rig.KillSwitch.IsEnabled.ShouldBeTrue("служба при запуске ставит блок раньше сети");
            await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");

            await next.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

            (rig.KillSwitch.IsEnabled, File.Exists(file)).ShouldBe((false, false));
            (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Strict_mode_disconnect_keeps_the_network_closed_until_the_user_connects_or_drops_it()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(options: rig.Vpn);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        await service.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);

        var strict = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
        (strict.State, strict.StrictBlock).ShouldBe((V1.ConnectionState.Blocked, true), "защита выключена вручную, а сеть закрыта");
        rig.KillSwitch.IsEnabled.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 2), "туннеля тоже нет");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Строгий режим: защита выключена вручную, сеть закрыта до подключения");

        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected))[^1];

        connected.StrictBlock.ShouldBeFalse();
        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off, "строгий режим выключен — сеть открыта");
        rig.KillSwitch.IsEnabled.ShouldBeFalse();
        (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля");
    }

    [Fact]
    public async Task Leak_check_sees_the_server_ip_through_the_tunnel_and_the_real_ip_before_it()
    {
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n");
        using var direct = rig.DirectTrace($"fl=1\nip={RealIp}\n");
        var network = new FakeNetwork();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Trace = Rig.Trace, ExitIp = rig.ExitIpAsync, Network = network,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        var measured = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.Ip == TunnelIp || s.State == V1.ConnectionState.Error))[^1];
        measured.State.ShouldBe(V1.ConnectionState.Connected, measured.Error?.Message);
        measured.Exit.DirectIp.ShouldBe(RealIp, "реальный IP — напрямую, до блока и туннеля");
        (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId()).State.ShouldBe(V1.CheckState.Ok, "запрос без прокси ушел в туннель");
        direct.Requests.ShouldBe(1);

        // Реальный IP мерится один раз на сеть: следующее подключение его не спрашивает.
        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        await ReconnectAsync();
        direct.Requests.ShouldBe(1);

        // Другая сеть — реальный IP забыт. Новое подключение при поднятом туннеле его не мерит: ответ пришел бы через туннель.
        network.Raise();
        await ServiceIpcTests.ReadUntilAsync(watch, s => string.IsNullOrEmpty(s.Exit?.DirectIp));
        await ReconnectAsync();

        (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId()).State.ShouldBe(V1.CheckState.Ok, "сравнить не с чем — но и ложной утечки нет");
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).Exit.DirectIp.ShouldBeEmpty();
        direct.Requests.ShouldBe(1, "напрямую проверочный адрес спрошен один раз — до первого туннеля");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Проверка утечек: IP для сайтов измерен, реальный IP неизвестен — сравнить не с чем");

        // Подключение без блока при обрыве: блока нет, а туннель прежнего подключения, если был, еще стоит.
        async Task ReconnectAsync()
        {
            await service.Client.ConnectAsync(VpnRequest(rig.Definition, killSwitch: false), cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting);
            var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s =>
                s.State == V1.ConnectionState.Connected && s.Exit?.Ip == TunnelIp || s.State == V1.ConnectionState.Error))[^1];
            connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        }
    }

    [Fact]
    public async Task Without_the_setting_connect_does_not_ask_the_check_address()
    {
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n");
        using var direct = rig.DirectTrace($"fl=1\nip={RealIp}\n");
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { Trace = Rig.Trace, ExitIp = rig.ExitIpAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition);
        request.Protection.CheckLeaksAfterConnect = false;

        await service.Client.ConnectAsync(request, cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        direct.Requests.ShouldBe(0, "«Проверять утечки после подключения» выключено — проверочный адрес не спрашивается");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Message.StartsWith("Проверка утечек", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leak_check_sees_the_real_ip_when_traffic_goes_around_the_tunnel()
    {
        await using var rig = await Rig.CreateAsync(page: $"fl=1\nip={TunnelIp}\n");
        using var direct = rig.DirectTrace($"fl=1\nip={RealIp}\n");
        // Запрос проверки идет от curl — его раздельный режим пускает мимо туннеля: так выглядит утечка.
        var curl = VpnTunnel.RealPath((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':')
            .Select(dir => Path.Combine(dir, "curl")).First(File.Exists));
        var routing = new RoutingSettings(RoutingMode.Split, [new BypassApp(curl, "curl")], []);
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { Trace = Rig.Trace, ExitIp = rig.ExitIpAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition, routing: routing), cancellationToken: Ct);

        var measured = (await ServiceIpcTests.ReadUntilAsync(watch, s => !string.IsNullOrEmpty(s.Exit?.Ip) || s.State == V1.ConnectionState.Error))[^1];
        measured.State.ShouldBe(V1.ConnectionState.Connected, measured.Error?.Message);
        measured.Exit.Ip.ShouldBe(RealIp, "сайты видят реальный IP");
        var exit = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId());
        (exit.State, exit.Reason).ShouldBe((V1.CheckState.Warning, "same_ip"));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
            e.Level == JournalLevel.Error && e.Message == "Проверка утечек: IP для сайтов совпадает с реальным — трафик идет мимо туннеля");
    }

    /// <summary>
    /// Стенд режима VPN в своем сетевом пространстве: пара veth к «локальной сети» машины, на адресе
    /// машины — свой сервер Xray с выходом на свой сайт и сайт «мимо туннеля»; ядра работают в
    /// пространстве, блок nftables — настоящий, тоже там. Нет прав или сборок — тест пропускается.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly IDisposable[] _owned;
        private readonly JsonObject _serverConfig;
        private readonly int _serverPort;
        private CoreProcess? _server;

        private Rig(NetworkNamespace netns, LocalSite lan, IPAddress @public, ServerDefinition definition, CountingKillSwitch killSwitch,
            (JsonObject Config, int Port, CoreProcess Process) server, params IDisposable[] owned)
        {
            (Netns, Lan, Public, Definition, KillSwitch, _owned) = (netns, lan, @public, definition, killSwitch, owned);
            (_serverConfig, _serverPort, _server) = server;
        }

        public NetworkNamespace Netns { get; }

        /// <summary>Адрес машины из диапазона документации — как сайт в интернете.</summary>
        public IPAddress Public { get; }

        public LocalSite Lan { get; }

        public ServerDefinition Definition { get; }

        public CountingKillSwitch KillSwitch { get; }

        /// <summary>Маршруты пространства — для поиска чужого VPN.</summary>
        public IRouteTable Routes => new IpRouteTable(arguments => Netns.Run(null, ["ip", .. arguments]));

        /// <summary>
        /// Включить «чужой VPN»: адаптер TUN (если его еще нет) и маршрут ко всему интернету через него в своей
        /// таблице, как у WireGuard. На трафик пространства не влияет — правила на таблицу не ведут.
        /// </summary>
        public void StartOtherVpn()
        {
            if (!Netns.Run(null, "ip", "-o", "link", "show").Contains("pgother0", StringComparison.Ordinal))
            {
                Netns.Run(null, "ip", "tuntap", "add", "dev", "pgother0", "mode", "tun");
                Netns.Run(null, "ip", "link", "set", "pgother0", "up");
            }

            Netns.Run(null, "ip", "route", "add", "default", "dev", "pgother0", "table", "51820");
        }

        public GatewayOptions Vpn(GatewayOptions options) =>
            options with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = KillSwitch, TunnelNetns = Netns.Path };

        /// <summary>Сервер перестал отвечать.</summary>
        public void StopServer()
        {
            _server?.Dispose();
            _server = null;
        }

        /// <summary>Сервер вернулся — на том же адресе и порту.</summary>
        public async Task StartServerAsync() => _server = await XrayKit.Start(_serverConfig, _serverPort);

        /// <summary>
        /// Проба связи изнутри пространства: вход проверки Xray там, и из сети машины его не видно. curl через
        /// SOCKS5 с паролем подключения.
        /// </summary>
        public async Task<bool> ProbeAsync(ServerProbe probe, CancellationToken cancellationToken)
        {
            var user = probe.Credentials!;
            try
            {
                await Netns.RunAsync("curl", "-sS", "-o", "/dev/null", "-m", ((int)Math.Ceiling(probe.Timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                    "-x", $"socks5h://{user.UserName}:{user.Password}@127.0.0.1:{probe.Port}", probe.Target.ToString());
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Проверочный адрес внешнего IP в тестах стенда: через туннель он приходит на сайт сервера.</summary>
        public static Uri Trace { get; } = new("http://1.1.1.1/");

        /// <summary>
        /// Проверочный адрес напрямую, мимо туннеля, — сайт на адресе машины: правило nftables пространства ведет туда
        /// запросы к <see cref="Trace"/>, которые уходят в сеть машины. В туннель они идут как были — к серверу.
        /// </summary>
        public LocalSite DirectTrace(string page)
        {
            var site = new LocalSite(page, Public);
            Netns.Run($$"""
                table ip pgtest {
                    chain output {
                        type nat hook output priority -100; policy accept;
                        oifname "pgnet0" ip daddr {{Trace.Host}} tcp dport {{Trace.Port}} dnat to {{Public}}:{{site.Url.Port}}
                    }
                }
                """, "nft", "-f", "-");
            return site;
        }

        /// <summary>Внешний IP — запросом изнутри пространства, как у службы: без прокси (порт 0) или через вход проверки.</summary>
        public async Task<string?> ExitIpAsync(ServerProbe probe, CancellationToken cancellationToken)
        {
            var seconds = ((int)Math.Ceiling(probe.Timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            var proxy = probe.Credentials is { } user ? ["-x", $"socks5h://{user.UserName}:{user.Password}@127.0.0.1:{probe.Port}"] : Array.Empty<string>();
            try
            {
                return LatencyProbe.TraceIp(await Netns.RunAsync(["curl", "-sS", "-m", seconds, .. proxy, probe.Target.ToString()]));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static async Task<Rig> CreateAsync(string page = "через туннель")
        {
            NetworkNamespace.SkipUnlessAvailable("nft", "ip", "curl");
            Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
            // Не собрался — убрать уже созданное: пространство, оставленное тестом, мешает следующим.
            var owned = new List<IDisposable>();
            try
            {
                var netns = NetworkNamespace.Create();
                owned.Add(netns);
                var (host, neighbor, @public) = netns.LinkAsMachine();
                var lan = new LocalSite("мимо туннеля", neighbor);
                owned.Add(lan);
                var site = new LocalSite(page);
                owned.Add(site);
                var serverPort = XrayKit.FreePort();
                var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host);
                // Выход сервера теста — на свой сайт: из машины ничего не уходит.
                serverConfig["outbounds"] = new JsonArray(
                    new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
                var server = await XrayKit.Start(serverConfig, serverPort);
                var killSwitch = new CountingKillSwitch(new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments])));

                // Убирать — в обратном порядке: сайты, потом пространство; сервер — раньше всего.
                owned.Reverse();
                return new Rig(netns, lan, @public, definition, killSwitch, (serverConfig, serverPort, server), [.. owned]);
            }
            catch
            {
                foreach (var created in Enumerable.Reverse(owned))
                {
                    created.Dispose();
                }

                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            StopServer();
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Процесс sing-box, который служба подняла последним, — из ее журнала.</summary>
    private static int SingBoxProcessId(RunningService service)
    {
        const string Marker = ", sing-box — процесс ";
        var line = service.Journal.Tail(ServiceJournal.Capacity).Last(e => e.Message.StartsWith("Туннель поднят:", StringComparison.Ordinal)).Message;
        return int.Parse(line[(line.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..], CultureInfo.InvariantCulture);
    }

    /// <summary>Подключение в режиме VPN; локальная сеть закрыта, если не сказано иначе; строгие маршруты — если в ядре ОС есть IPv6.</summary>
    private static V1.ConnectRequest VpnRequest(ServerDefinition server, bool killSwitch = true, RoutingSettings? routing = null, bool allowLan = false)
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Vpn,
            KillSwitch = killSwitch,
            Routing = routing ?? RoutingSettings.Default,
            Protection = ProtectionSettings.Default with { AllowLan = allowLan },
            Connection = ConnectionSettings.Default with { ConnectTimeoutSeconds = 20, StrictRoutes = Directory.Exists("/proc/sys/net/ipv6") },
        };
        return new V1.ConnectRequest
        {
            Server = server.ToContract("srv-1"),
            Mode = V1.Mode.Vpn,
            Protection = settings.ToContract(),
            Connection = settings.ToConnection(),
            Routing = settings.ToRouting(),
        };
    }

    /// <summary>Настоящий блок при обрыве, который считает, сколько раз его ставили, и помнит последнюю политику.</summary>
    private sealed class CountingKillSwitch(IKillSwitch inner) : IKillSwitch
    {
        public int Enables { get; private set; }

        public KillSwitchPolicy? Last { get; private set; }

        public string Name => inner.Name;

        public bool IsEnabled => inner.IsEnabled;

        public void Enable(KillSwitchPolicy policy)
        {
            Enables++;
            Last = policy;
            inner.Enable(policy);
        }

        public void Disable() => inner.Disable();
    }

    /// <summary>Блок при обрыве без брандмауэра: помнит политику и сколько раз его снимали.</summary>
    private sealed class FakeKillSwitch(bool enabled = false) : IKillSwitch
    {
        public string Name => "подделка";

        public bool IsEnabled { get; private set; } = enabled;

        public KillSwitchPolicy? Policy { get; private set; }

        public bool Fails { get; init; }

        public int Disabled { get; private set; }

        public void Enable(KillSwitchPolicy policy)
        {
            if (Fails)
            {
                throw new IOException("nft: правила не приняты");
            }

            IsEnabled = true;
            Policy = policy;
        }

        public void Disable()
        {
            Disabled++;
            IsEnabled = false;
        }
    }
}
