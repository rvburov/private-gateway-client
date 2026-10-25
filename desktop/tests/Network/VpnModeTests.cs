using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Режим VPN в службе: блок при обрыве до запуска ядер, туннель, блок при сбое и после остановки
/// службы, снятие при отключении и отказе. Логика — с подделкой блока; весь путь — в своем сетевом
/// пространстве Linux с настоящими nftables, sing-box и Xray.
/// </summary>
public sealed class VpnModeTests
{
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
    public async Task Vpn_goes_through_the_tunnel_behind_the_block_and_the_block_outlives_failures()
    {
        NetworkNamespace.SkipUnlessAvailable("nft", "ip", "curl");
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        using var netns = NetworkNamespace.Create();
        var octet = Random.Shared.Next(1, 250);
        var host = IPAddress.Parse($"10.252.{octet}.1");
        netns.Link("pgnet0", host, IPAddress.Parse($"10.252.{octet}.2"));
        using var lan = new LocalSite("мимо туннеля", host);
        using var site = new LocalSite("через туннель");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        // Выход сервера теста — на свой сайт: из машины ничего не уходит.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var killSwitch = new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments]));
        GatewayOptions Vpn(GatewayOptions o) => o with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = killSwitch, TunnelNetns = netns.Path };

        await using (var service = await RunningService.StartAsync(options: Vpn))
        {
            using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

            var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
            connected.ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Connected));
            killSwitch.IsEnabled.ShouldBeTrue();
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

    /// <summary>Подключение в режиме VPN; локальная сеть закрыта, строгие маршруты — если в ядре ОС есть IPv6.</summary>
    private static V1.ConnectRequest VpnRequest(ServerDefinition server, bool killSwitch = true)
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Vpn,
            KillSwitch = killSwitch,
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
