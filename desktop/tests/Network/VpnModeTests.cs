using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
[Collection(NamespaceSerial.Name)]
public sealed partial class VpnModeTests
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
    public async Task Vpn_without_a_tun_device_is_not_ready_and_says_why()
    {
        // Сборка sing-box здесь — любой существующий файл: до запуска не дойдет.
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var killSwitch = new FakeKillSwitch(enabled: false);
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-tun-" + Guid.NewGuid().ToString("N")[..8], "tun");
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            CanVpn = true,
            SingBoxPath = XrayKit.Xray,
            KillSwitch = killSwitch,
            TunDevice = missing,
        });
        var (definition, _) = XrayKit.Pair("vless", 443);

        var reply = await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, ErrorCodes.VpnNotReady), "контейнер без /dev/net/tun — VPN не обещается");
        reply.Error.Message.ShouldContain("Нет устройства TUN");
        killSwitch.IsEnabled.ShouldBeFalse("до блока и ядер не дошло");
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Refusal_keeps_a_standing_block_only_when_a_vpn_with_the_kill_switch_is_asked_for(bool killSwitchOn, bool vpn)
    {
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = killSwitch });
        var (definition, _) = XrayKit.Pair("vless", 443);

        // .invalid не разрешается никогда (RFC 2606); за VLESS через QUIC ядро прокси не возьмется.
        var request = vpn
            ? VpnRequest(definition with { Host = "server.invalid" }, killSwitch: killSwitchOn)
            : ServiceIpcTests.Request(XrayKit.Refused, XrayKit.FreePort());
        var reply = await service.Client.ConnectAsync(request, cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, vpn ? ErrorCodes.ServerNotFound : ErrorCodes.UnsupportedServer));
        var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        if (killSwitchOn && vpn)
        {
            (status.State, status.StrictBlock, status.ErrorCode, killSwitch.IsEnabled).ShouldBe(
                (ConnectionState.Blocked, false, ErrorCodes.ServerNotFound, true), "блок при обрыве стоит — отказ сеть не открывает");
            status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Blocked));
            service.Journal.Tail(ServiceJournal.Capacity)
                .ShouldContain(e => e.Message == "Блок при обрыве: подключиться не удалось — трафик заблокирован до подключения или отключения");

            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
            killSwitch.IsEnabled.ShouldBeFalse("отключение снимает блок — следующее подключение найдет адрес");
        }
        else
        {
            (status.State, killSwitch.IsEnabled).ShouldBe((ConnectionState.Error, false),
                vpn ? "блок при обрыве выключен — сеть открыта, как выбрал пользователь" : "режиму прокси блок VPN не нужен");
            status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off));
        }
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
        (policy.TunnelInterface, policy.TunnelDns, policy.AllowLan, policy.LanDiscovery, policy.BlockOutsideDns).ShouldBe(((string?)null, (IPAddress?)null, false, false, true),
            "блок до ядер — без разрешения адаптеру туннеля, его еще нет: на Windows так закрыта и пересылка (хот-спот, ВМ)");
        policy.Servers.ShouldBe([new KillSwitchEndpoint(IPAddress.Loopback, 8443)], "мимо туннеля — только адрес и порт сервера");
        var journal = service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message).ToList();
        // Ядро вышло раньше, чем прочитало конфиг (macOS), — «Broken pipe» вместо «вышел при запуске»: сверять по коду ошибки.
        journal.FindIndex(m => m.StartsWith("Блок при обрыве включен", StringComparison.Ordinal))
            .ShouldBeLessThan(journal.FindIndex(m => m.StartsWith(ErrorCodes.CoreFailed + ":", StringComparison.Ordinal)), "блок — до запуска ядер");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        killSwitch.IsEnabled.ShouldBeFalse("отключение из «Ошибки» снимает блок");
    }

    [Fact]
    public async Task What_the_block_cannot_close_goes_to_the_journal_once()
    {
        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        var killSwitch = new FakeKillSwitch { Warning = "ВМ без туннеля могут выйти мимо блока" };
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
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error);
        await service.Client.ConnectAsync(VpnRequest(definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error);

        killSwitch.Enables.ShouldBe(2, "блок ставился при каждом подключении");
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Level == JournalLevel.Warning && e.Message == killSwitch.Warning)
            .ShouldBe(1, "одно и то же — один раз, а не при каждой смене блока");
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

        IPAddress[] all = [IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20"), IPAddress.Parse("2001:db8::21")];

        Gateway.PickAddresses(found, ProtectionSettings.Default).ShouldBe(all,
            "первым — IPv4: к нему соединяется Xray, а IPv6 туннеля станет известен позже; адреса IPv6 — мимо туннеля и блока");
        Gateway.PickAddresses(found, ProtectionSettings.Default with { Addresses = AddressPreference.PreferIpv6 }).ShouldBe(all,
            "«Приоритет адресов» — для сайтов, а не для сервера");
        Gateway.PickAddresses(found, ProtectionSettings.Default with { BlockIpv6 = false, Addresses = AddressPreference.Ipv4Only })
            .ShouldBe([IPAddress.Parse("198.51.100.20")]);
        Gateway.PickAddresses([IPAddress.Parse("2001:db8::20")], ProtectionSettings.Default).ShouldBe([IPAddress.Parse("2001:db8::20")],
            "сервер только с IPv6");
        Gateway.PickAddresses([IPAddress.Parse("2001:db8::20")], ProtectionSettings.Default with { Addresses = AddressPreference.Ipv4Only }).ShouldBeEmpty();
        IPAddress[] many = [.. Enumerable.Range(1, 12).Select(i => IPAddress.Parse($"2001:db8::{i:x}")), .. Enumerable.Range(1, 12).Select(i => IPAddress.Parse($"198.51.100.{i}"))];
        var picked = Gateway.PickAddresses(many, ProtectionSettings.Default);
        picked.Count.ShouldBe(KillSwitchPolicy.MaxServers, "больше блок при обрыве не держит — подключение не должно падать из-за длинного списка");
        picked.Take(12).ShouldAllBe(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork, "IPv4 — первыми");
        Gateway.PickAddresses([IPAddress.Parse("198.18.0.7")], ProtectionSettings.Default).ShouldBe([IPAddress.Parse("198.18.0.7")],
            "сервер в 198.18.0.0/15 (сеть лаборатории, fake-ip роутера) не отбрасывается: адрес пула FakeDNS — только в ответе DNS ОС через наш туннель");
    }

    [Fact]
    public async Task Server_by_name_is_found_past_fake_dns_while_the_tunnel_is_up()
    {
        await using var rig = await Rig.CreateAsync();
        var real = IPAddress.Parse(rig.Definition.Host);
        var resolved = 0;
        var lookups = new List<string>();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            // DNS ОС: до туннеля — настоящий адрес, в туннеле с FakeDNS — адрес из пула для A и настоящий AAAA (так отвечает Xray).
            Resolve = (_, _) => Task.FromResult<IPAddress[]>(Interlocked.Increment(ref resolved) == 1
                ? [real] : [IPAddress.Parse("198.18.0.7"), IPAddress.Parse("2001:db8::7")]),
            Doh = new Uri("https://1.1.1.1/dns-query"),
            Lookup = (_, name, _) =>
            {
                lock (lookups)
                {
                    lookups.Add(name);
                }

                return Task.FromResult<IReadOnlyList<IPAddress>>(name == "other.example.com" ? [real] : []);
            },
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        // Без блока при обрыве: прежних адресов в блоке нет — тот же сервер берется по адресам подключения.
        foreach (var (host, killSwitch) in new[] { ("vpn.example.com", false), ("vpn.example.com", false), ("other.example.com", true) })
        {
            await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = host }, killSwitch, fakeDns: true), cancellationToken: Ct);
            var end = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            end.State.ShouldBe(V1.ConnectionState.Connected, $"{host}: {end.Error?.Message} — с адресом из пула Xray до сервера не дошел бы");
        }

        rig.KillSwitch.Last!.Servers.ShouldBe([new KillSwitchEndpoint(real, rig.Definition.Port)], "настоящий адрес, не из пула");
        lookups.ShouldBe(["vpn.example.com", "other.example.com"],
            "пока туннель поднят, имя ищется DoH через сервер; тот же сервер, не найденный так, — по нынешним адресам");
    }

    [Fact]
    public async Task New_server_by_name_is_found_without_the_tunnel_when_the_current_server_is_dead()
    {
        await using var rig = await Rig.CreateAsync();
        var real = IPAddress.Parse(rig.Definition.Host);
        var resolved = 0;
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            // Прежний сервер не отвечает: DoH через него пуст, DNS ОС в туннеле — адрес пула; без туннеля — настоящий адрес.
            Resolve = (_, _) => Task.FromResult<IPAddress[]>(Interlocked.Increment(ref resolved) == 2 ? [IPAddress.Parse("198.18.0.9")] : [real]),
            Doh = new Uri("https://1.1.1.1/dns-query"),
            Lookup = (_, _, _) => Task.FromResult<IReadOnlyList<IPAddress>>([]),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "vpn.example.com" }, killSwitch: false, fakeDns: true), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "other.example.com" }, killSwitch: false, fakeDns: true), cancellationToken: Ct);

        var end = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error, service.Journal))[^1];
        end.State.ShouldBe(V1.ConnectionState.Connected, end.Error?.Message);
        resolved.ShouldBe(3, "в туннеле — адрес пула, DoH пуст, туннель остановлен — и снова через систему");
        service.Journal.Tail(ServiceJournal.Capacity)
            .ShouldContain(e => e.Message == "Адрес нового сервера через прежнее подключение не найден — оно остановлено, адрес ищется без туннеля");
    }

    [Fact]
    public async Task Connect_canceled_after_the_old_tunnel_stopped_still_ends_in_an_honest_state()
    {
        await using var rig = await Rig.CreateAsync();
        var real = IPAddress.Parse(rig.Definition.Host);
        var asked = 0;
        var stopped = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Resolve = async (name, _) =>
            {
                // Второй поиск нового имени — уже без прежнего туннеля: тут вызов и отменяется.
                if (name == "b.example.com" && Interlocked.Increment(ref asked) == 2)
                {
                    stopped.TrySetResult();
                    await release.Task;
                }

                return name == "b.example.com" ? [] : [real];
            },
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        using var cancel = new CancellationTokenSource();
        var call = service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "b.example.com" }), cancellationToken: cancel.Token).ResponseAsync;
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State
            .ShouldBe(V1.ConnectionState.Connecting, "прежнего туннеля уже нет — не «Защищено»");
        await cancel.CancelAsync();
        await Should.ThrowAsync<Grpc.Core.RpcException>(() => call);
        release.TrySetResult();

        // Отмена вызова не оставляет «Подключение» без ядер: имени нет — отказ, блок стоит без туннеля.
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Blocked, service.Journal);
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.TunnelInterface).ShouldBe((true, null));
    }

    [Fact]
    public async Task Addresses_of_a_vpn_server_are_not_taken_for_another_server_after_proxy_mode()
    {
        await using var rig = await Rig.CreateAsync();
        var real = IPAddress.Parse(rig.Definition.Host);
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Resolve = (name, _) => name == "a.example.com" ? Task.FromResult<IPAddress[]>([real]) : throw new SocketException(11001),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "a.example.com" }), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        // Сервер b — в частной зоне имен (.test): VLESS без TLS к открытому имени служба не подключает и в режиме прокси.
        (await service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition with { Host = "b.test" }, XrayKit.FreePort()), cancellationToken: Ct))
            .Accepted.ShouldBeTrue("режим прокси для сервера b подключился — иначе тест ничего не проверяет");

        var reply = await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "b.test" }), cancellationToken: Ct);

        (reply.Accepted, reply.Error?.Code).ShouldBe((false, ErrorCodes.ServerNotFound), "адреса прежнего подключения VPN — сервера a, а не b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusal_while_protected_keeps_the_block_without_the_tunnel(bool byName)
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Resolve = (name, _) => name == "b.example.com" ? throw new SocketException(11001) : Task.FromResult<IPAddress[]>([IPAddress.Parse(rig.Definition.Host)]),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        // Имя не нашлось (туннель остановлен еще при поиске) или за сервер ядро не возьмется (туннель стоит до отказа).
        var code = byName ? ErrorCodes.ServerNotFound : ErrorCodes.UnsupportedServer;
        var reply = await service.Client.ConnectAsync(byName ? VpnRequest(rig.Definition with { Host = "b.example.com" }) : Refused(), cancellationToken: Ct);

        reply.Error.Code.ShouldBe(code);
        var refused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Blocked, service.Journal))[^1];
        (refused.StrictBlock, refused.Error?.Code).ShouldBe((false, code));
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.TunnelInterface).ShouldBe((true, null), "туннель остановлен — блок без разрешения ему");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 2),
            "смена сервера не удалась — сеть закрыта, а не открыта напрямую");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchAsync(rig.Lan.Url, seconds: 2, device: "pgnet0"));

        // Пауза для входа в Wi-Fi и ее конец: прежний сервер сам не подключается — «Блокировка» до подключения или отключения.
        await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null, service.Journal);
        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);
        var resumed = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null, service.Journal))[^1];
        (resumed.State, rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.TunnelInterface).ShouldBe((V1.ConnectionState.Blocked, true, null));
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message.StartsWith("Подключение в режиме VPN", StringComparison.Ordinal))
            .ShouldBe(1, "к прежнему серверу после отказа не подключались");

        // Из этой «Блокировки» — снова к рабочему серверу.
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task Dns_row_is_partial_only_when_dns_of_the_allowed_local_network_goes_around_the_tunnel(bool allowLan, bool blockOutsideDns, bool partial)
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { Trace = Rig.Trace, ExitIp = rig.ExitIpAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(VpnRequest(rig.Definition, allowLan: allowLan, blockOutsideDns: blockOutsideDns), cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal))[^1];
        connected.Protection.Dns.ShouldBe(partial ? V1.CheckState.Warning : V1.CheckState.Ok,
            "DNS роутера мимо туннеля правила пропускают только в разрешенную локальную сеть без «Блокировать DNS провайдера»");
        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Dns.ToContractId());
        (row.State, row.Reason).ShouldBe(partial ? (V1.CheckState.Warning, "lan_dns") : (V1.CheckState.Ok, string.Empty));
    }

    [Fact]
    public async Task Dns_past_the_tunnel_is_measured_through_the_card_and_rules_taken_away_show_up()
    {
        NetworkNamespace.SkipUnlessAvailable("python3");
        await using var rig = await Rig.CreateAsync();
        // DNS в интернете — на адресе машины: из пространства до него только через карту, мимо туннеля.
        using var outside = new TestDns(IPAddress.Parse("192.0.2.53"), new IPEndPoint(rig.Public, 53));
        var rulesBack = false;
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            OutsideDns = new IPEndPoint(rig.Public, 53),
            DnsPastTunnel = (resolver, _) => Volatile.Read(ref rulesBack) ? Task.FromResult<bool?>(false) : rig.DnsPastTunnelAsync(resolver),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        // Блок стоит: запрос через карту не прошел — строка по состоянию.
        (await DnsRowAsync()).ShouldBe((V1.CheckState.Ok, string.Empty));
        outside.Queries.ShouldBeEmpty("запрос через карту закрыт блоком");

        // Правила сняла другая программа: запрос через карту проходит — замер это видит.
        rig.Netns.Run(null, "nft", "delete", "table", "inet", "private_gateway");
        (await DnsRowAsync()).ShouldBe((V1.CheckState.Warning, "dns_open"));
        outside.Queries.ShouldNotBeEmpty();
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).Protection.Dns
            .ShouldBe(V1.CheckState.Warning, "и чип DNS на главной — «частично»");
        service.Journal.Tail(ServiceJournal.Capacity)
            .ShouldContain(e => e.Message == "Проверка утечек: запрос DNS через сетевую карту прошел мимо туннеля — DNS мимо туннеля открыт");

        // Правила вернулись (замер снова не проходит) — строка и чип снова по состоянию.
        Volatile.Write(ref rulesBack, true);
        (await DnsRowAsync()).ShouldBe((V1.CheckState.Ok, string.Empty));
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).Protection.Dns.ShouldBe(V1.CheckState.Ok);

        async Task<(V1.CheckState, string)> DnsRowAsync()
        {
            var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
                .Single(r => r.Id == DiagnosticCheck.Dns.ToContractId());
            return (row.State, row.Reason);
        }
    }

    [Fact]
    public async Task Dns_measurement_goes_through_the_card_of_the_default_route_and_not_the_tunnel()
    {
        await using var rig = await Rig.CreateAsync();
        // Карта маршрута по умолчанию у службы — петля машины (служба работает вне пространства теста), DNS «в интернете» — на ней.
        var loopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name;
        using var outside = new TestDns(IPAddress.Parse("192.0.2.53"));
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            OutsideDns = new IPEndPoint(IPAddress.Loopback, outside.Port),
            Routes = new FakeRoutes { Routes = [new DefaultRoute(SingBoxConfigBuilder.InterfaceName, IsTunnel: true), new DefaultRoute(loopback, IsTunnel: false)] },
            Trace = Rig.Trace,
            ExitIp = rig.ExitIpAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        // Проверка утечек после подключения меряет и DNS.
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected && s.Protection.Dns == V1.CheckState.Warning, service.Journal);

        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Dns.ToContractId());

        (row.State, row.Reason).ShouldBe((V1.CheckState.Warning, "dns_open"), "запрос ушел через карту маршрута по умолчанию, а не туннеля, и получил ответ");
        outside.Queries.ShouldContain(string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_dns_in_the_local_network_answers_while_the_rest_of_the_network_follows_the_setting(bool allowLan)
    {
        NetworkNamespace.SkipUnlessAvailable("getent");
        await using var rig = await Rig.CreateAsync(services: true);
        // «Pi-hole» — на адресе соседа в сети, как DNS в локальной сети.
        var lanDns = IPAddress.Parse(rig.Lan.Url.Host);
        using var pihole = new TestDns(IPAddress.Parse("192.0.2.77"), new IPEndPoint(lanDns, 53));
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { Trace = Rig.Trace, ExitIp = rig.ExitIpAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition, allowLan: allowLan);
        request.Protection.Dns.Servers.Clear();
        request.Protection.Dns.Servers.Add(lanDns.ToString());
        request.Connection.CoreLog = V1.CoreLogLevel.Debug;

        await service.Client.ConnectAsync(request, cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal))[^1];
        // DNS ОС — в туннель, Xray спрашивает свой DNS в сети напрямую.
        string answer;
        try
        {
            answer = await rig.Netns.ResolveWithSystemAsync("example.com", IPAddress.Parse(SingBoxConfigBuilder.TunDns));
        }
        catch (InvalidOperationException e)
        {
            // Однажды в CI (master, прогон 284) имя не нашлось; здесь не повторялось ни отдельно, ни под нагрузкой. Кто получил
            // запрос и журнал ядер покажут, на каком звене он потерялся.
            throw new InvalidOperationException(
                $"{e.Message}\nзапросы своего DNS: [{string.Join(", ", pihole.Queries)}]; DNS сервера: [{string.Join(", ", rig.Dns!.Queries)}]\n" +
                string.Join('\n', service.Journal.Tail(ServiceJournal.Capacity).TakeLast(120).Select(x => x.Message)), e);
        }

        answer.ShouldStartWith("192.0.2.77");
        pihole.Queries.ShouldContain("example.com");
        rig.Dns!.Queries.ShouldBeEmpty("через сервер DNS не спрашивали");
        if (allowLan)
        {
            (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля", "локальная сеть разрешена");
        }
        else
        {
            await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchAsync(rig.Lan.Url, seconds: 2, device: "pgnet0"),
                "к адресу своего DNS открыт только DNS: локальная сеть закрыта");
            rig.Lan.Requests.ShouldBe(0);
        }

        connected.Protection.Dns.ShouldBe(V1.CheckState.Warning, "запросы имен идут мимо туннеля — к своему DNS");
        var row = (await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Dns.ToContractId());
        (row.State, row.Reason).ShouldBe((V1.CheckState.Warning, "own_dns"));
    }

    [Fact]
    public async Task New_server_by_name_is_found_past_the_block_by_the_service_alone()
    {
        await using var rig = await Rig.CreateAsync();
        var server = rig.Definition.Host;
        // DoH — сайт на адресе машины из диапазона документации, как 1.1.1.1 в интернете.
        using var doh = new LocalSite($$"""{"Status":0,"Answer":[{"name":"srv.example.com.","type":1,"TTL":60,"data":"{{server}}"}]}""", rig.Public);
        var dohUri = new Uri($"http://{rig.Public}:{doh.Url.Port}/dns-query");
        var others = new List<bool>();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Doh = dohUri,
            Resolve = (_, _) => throw new SocketException(11001),
            LookupPastBlock = async (uri, host, mark, _) =>
            {
                // Пока исключение стоит, сокет без метки службы к DoH не проходит.
                try
                {
                    await rig.Netns.DohAsync(uri, host, 0);
                    others.Add(true);
                }
                catch (InvalidOperationException)
                {
                    others.Add(false);
                }

                return host == "srv.example.com" ? await rig.Netns.DohAsync(uri, host, mark) : [];
            },
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        // Строгий режим: защиту выключили, а сеть закрыта — туннеля нет, DNS мимо него закрыт.
        await service.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Blocked, service.Journal);
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.DohAsync(dohUri, "srv.example.com", 0), "без метки службы к DoH — закрыто");

        // Имя не нашлось и через DoH службы — отказ, блок тот же, без исключения.
        (await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "nosuch.example.com" }), cancellationToken: Ct)).Error.Code
            .ShouldBe(ErrorCodes.ServerNotFound);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.Error?.Code == ErrorCodes.ServerNotFound, service.Journal);
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Last!.Lookup).ShouldBe((true, null), "после поиска исключение снято");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.DohAsync(dohUri, "srv.example.com", Gateway.LookupMark));

        await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "srv.example.com" }), cancellationToken: Ct);

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        doh.Requests.ShouldBe(2, "A и AAAA — через исключение для службы");
        others.ShouldBe([false, false], "исключение — только для сокета службы");
        var lookups = rig.KillSwitch.Policies.Where(p => p.Lookup is not null).ToList();
        lookups.Count.ShouldBe(2);
        lookups.ShouldAllBe(p => p.Lookup!.Endpoints.Single() == new KillSwitchEndpoint(rig.Public, doh.Url.Port) && p.TunnelInterface == null,
            "на время поиска — тот же блок без туннеля и одно исключение");
        rig.KillSwitch.Last!.Lookup.ShouldBeNull("после поиска исключения нет");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == $"Адрес сервера под блоком найден через DoH службы ({rig.Public})");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.DohAsync(dohUri, "srv.example.com", Gateway.LookupMark),
            "в «Защищено» исключения нет и для метки службы");
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
        (await service.Client.PauseForCaptivePortalAsync(hour, cancellationToken: Ct)).Until.ShouldBeNull("пауза уже идет — повтором ее не продлить");

        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error && s.CaptivePauseUntil is null);
        states.Select(s => s.State).ShouldContain(V1.ConnectionState.Connecting, "туннель поднимается заново, через «Подключение»");
        states[^1].State.ShouldBe(V1.ConnectionState.Connected, states[^1].Error?.Message);
        (killSwitch.IsEnabled, killSwitch.Enables).ShouldBe((true, 4), "защита вернулась с блоком — снова до ядер и после подъема адаптера");
        killSwitch.Tunnels.ShouldBe([null, SingBoxConfigBuilder.InterfaceName, null, SingBoxConfigBuilder.InterfaceName],
            "до ядер — без разрешения туннелю (его еще нет), после подъема адаптера — с ним");
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

            var open = VpnRequest(definition, killSwitch: false);
            open.Protection.BootProtection = true;
            await service.Client.ConnectAsync(open, cancellationToken: Ct);

            var unguarded = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            unguarded.State.ShouldBe(V1.ConnectionState.Connected, unguarded.Error?.Message);
            unguarded.ToCore().Protection.KillSwitch.ShouldBe(CheckState.Off, "блок выключен в настройках — проверка не делает вид, что он есть");
            (killSwitch.IsEnabled, killSwitch.Last!.TunnelInterface, killSwitch.Last.Persistent, killSwitch.Last.Ipv6Outside)
                .ShouldBe((true, SingBoxConfigBuilder.InterfaceName, false, false),
                "блок при обрыве выключен, а правила «только туннель» стоят, пока туннель поднят; после перезагрузки их нет — и с «Защитой при загрузке»");
            (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
            await Should.ThrowAsync<InvalidOperationException>(() => netns.FetchAsync(lan.Url, seconds: 3, device: "pgnet0"),
                "программа с привязкой к карте мимо маршрутов TUN — закрыта правилами");
            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
            killSwitch.IsEnabled.ShouldBeFalse("отключение снимает и правила «только туннель»");
            (await netns.FetchAsync(lan.Url, seconds: 3, device: "pgnet0")).ShouldBe("мимо туннеля");
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

        // Адрес документации Xray считает локальным (XrayConfigBuilder.LocalNetworks), а в исключениях TUN его нет: прямой
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

        // Проверочный адрес — по HTTP: сервер теста отвечает своим сайтом на любой адрес. Сдаться — через 8 с: сервер
        // возвращается через ~2,5 с после обрыва (тест проверяет блок), а попытка, начатая до его возврата, длится еще
        // время пробы — при 3 с служба успевала сдаться раньше, чем вернуть связь (CI, проверка IPv6 сервера).
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"),
            options: o => rig.Vpn(o) with { Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromSeconds(8) }, Probe = rig.ProbeAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition);
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        rig.StopServer();

        var reconnecting = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting, service.Journal))[^1].ToCore();
        (reconnecting.ServerId, reconnecting.Protection.AllTraffic).ShouldBe(("srv-1", CheckState.Waiting), "к тому же серверу, трафик на паузе");
        killSwitch.IsEnabled.ShouldBeTrue("пока служба ищет связь, блок держит трафик");
        killSwitch.Tunnels.Take(3).ShouldBe([null, SingBoxConfigBuilder.InterfaceName, null],
            "блок до ядер — без туннеля, с туннелем — с разрешением ему, туннель пропал — заново без него: на Windows так закрыта и пересылка (хот-спот, ВМ)");
        var lost = service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message).ToList();
        lost.FindIndex(m => m.StartsWith("Блок при обрыве обновлен: туннеля нет", StringComparison.Ordinal))
            .ShouldBeInRange(0, lost.FindIndex(m => m == "Переподключение, попытка 1") is var first and >= 0 ? first : int.MaxValue,
                "сразу при обрыве, а не с первой попыткой");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 2), "мимо туннеля — ничего");

        await rig.StartServerAsync();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "связь вернулась — снова через туннель");
        killSwitch.Last!.TunnelInterface.ShouldBe(SingBoxConfigBuilder.InterfaceName,
            "новый адаптер туннеля — блок обновлен с разрешением ему (на Windows — по LUID)");
        killSwitch.Last.TunnelDns.ShouldBe(IPAddress.Parse(SingBoxConfigBuilder.TunDns), "DNS к разрешенной локальной сети — в туннель (Linux)");

        rig.StopServer();

        var blocked = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Blocked or V1.ConnectionState.Error, service.Journal))[^1];
        blocked.State.ShouldBe(V1.ConnectionState.Blocked, "попытки кончились — «Блокировка», другой сервер сам не выбирается");
        killSwitch.IsEnabled.ShouldBeTrue();
        killSwitch.Last!.TunnelInterface.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 2));

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        killSwitch.IsEnabled.ShouldBeFalse("защиту выключил пользователь — блок снят");
        (await netns.CurlAsync(lan.Url)).ShouldBe("мимо туннеля");
    }

    [Fact]
    public async Task Server_that_does_not_answer_at_connect_keeps_the_block_and_is_retried()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromHours(1) },
            Probe = rig.ProbeAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        rig.StopServer();

        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        // Туннель поднялся, а сервер не ответил на пробу: не «Защищено», а «Переподключение» под блоком.
        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s is { State: V1.ConnectionState.Reconnecting, Attempt: >= 2 } or { State: V1.ConnectionState.Error },
            service.Journal);
        states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected, "сервер не ответил — «Защищено» не показано ни разу");
        states[^1].State.ShouldBe(V1.ConnectionState.Reconnecting, states[^1].Error?.Message);
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не ответил при подключении — переподключение к тому же серверу");
        rig.KillSwitch.IsEnabled.ShouldBeTrue("блок держит трафик, пока служба пробует снова");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");

        await rig.StartServerAsync();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель");
    }

    [Fact]
    public async Task Block_taken_away_by_another_program_comes_back_by_itself()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            BlockWatch = TimeSpan.FromMilliseconds(300),
            Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromHours(1) },
            Probe = rig.ProbeAsync,
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Removals().ShouldBe(0, "цел блок — сторож молчит");

        // «Защищено»: другая программа сняла таблицу блока — без него DNS, IPv6 и сокеты с привязкой к карте шли бы мимо туннеля.
        rig.Netns.Run(null, "nft", "delete", "table", "inet", "private_gateway");
        await BackAsync();
        Removals().ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchAsync(rig.Lan.Url, seconds: 2, device: "pgnet0"), "блок снова закрывает карту");

        // «Переподключение»: туннеля нет — без блока трафик шел бы напрямую.
        rig.StopServer();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting, service.Journal);
        rig.Netns.Run(null, "nft", "flush", "ruleset");
        await BackAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Блок при обрыве поставлен снова (nftables)");

        int Removals() => service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message.StartsWith(ErrorCodes.BlockRemoved, StringComparison.Ordinal));

        async Task BackAsync()
        {
            for (var waited = Stopwatch.StartNew(); !rig.KillSwitch.IsIntact; await Task.Delay(100, Ct))
            {
                waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "сторож ставит блок снова за несколько своих тактов");
            }
        }
    }

    [Fact]
    public async Task Block_that_cannot_drop_the_tunnel_does_not_stop_reconnecting_and_is_not_shown_as_blocked()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"),
            options: o => rig.Vpn(o) with { Reconnect = ReconnectTests.Fast, Probe = rig.ProbeAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition);
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        // Блок без туннеля брандмауэр не принимает (сбой WFP или nft): прежний блок остается и держит трафик машины.
        rig.KillSwitch.Refuses = policy => policy.TunnelInterface is null;
        rig.StopServer();

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Blocked);
        states.ShouldContain(s => s.State == V1.ConnectionState.Reconnecting, "сбой блока при обрыве не отменяет переподключение");
        var end = states[^1];
        (end.State, end.Error?.Code).ShouldBe((V1.ConnectionState.Error, ErrorCodes.KillSwitchFailed),
            "попытки кончились, а блок с разрешением исчезнувшему адаптеру остался — не «Блокировка»");
        rig.KillSwitch.IsEnabled.ShouldBeTrue("прежний блок не снят");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");
    }

    [Fact]
    public async Task Without_the_kill_switch_tunnel_only_rules_go_away_with_the_tunnel_and_come_back_with_it()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"),
            options: o => rig.Vpn(o) with { Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromSeconds(30) }, Probe = rig.ProbeAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = VpnRequest(rig.Definition, killSwitch: false, blockIpv6: false);
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        rig.KillSwitch.Tunnels.ShouldBe([SingBoxConfigBuilder.InterfaceName], "до туннеля правил нет — блок при обрыве выключен; с туннелем — «только туннель»");

        rig.StopServer();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting, service.Journal);
        rig.KillSwitch.IsEnabled.ShouldBeFalse("связь пропала, блок при обрыве выключен — сеть открыта, как выбрал пользователь");
        (await rig.Netns.FetchAsync(rig.Lan.Url, seconds: 3, device: "pgnet0")).ShouldBe("мимо туннеля");
        service.Journal.Tail(ServiceJournal.Capacity)
            .ShouldContain(e => e.Message == "Туннеля нет, блок при обрыве выключен — правила «только туннель» сняты, сеть открыта");

        await rig.StartServerAsync();

        var back = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal))[^1];
        back.ToCore().Protection.KillSwitch.ShouldBe(CheckState.Off);
        rig.KillSwitch.IsEnabled.ShouldBeTrue("туннель вернулся — и правила «только туннель»");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchAsync(rig.Lan.Url, seconds: 3, device: "pgnet0"));

        // Отказ при поднятом туннеле: правила «только туннель» — не блок, отказ их снимает и «Блокировкой» не прикидывается.
        (await service.Client.ConnectAsync(Refused(), cancellationToken: Ct)).Error.Code.ShouldBe(ErrorCodes.UnsupportedServer);
        var refused = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        (refused.State, rig.KillSwitch.IsEnabled).ShouldBe((ConnectionState.Error, false));
        (await rig.Netns.FetchAsync(rig.Lan.Url, seconds: 3, device: "pgnet0")).ShouldBe("мимо туннеля");
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
            Routes = rig.Routes,
            Network = network,
            Probe = rig.ProbeAsync,
            Reconnect = new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour),
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

        // Ядра останавливаются после «Ошибки», блок без туннеля встает следом.
        for (var i = 0; i < 100 && rig.KillSwitch.Last!.TunnelInterface is not null; i++)
        {
            await Task.Delay(100, Ct);
        }

        rig.KillSwitch.Last!.TunnelInterface.ShouldBeNull("туннеля нет — блок без разрешения ему: на Windows так закрыта и пересылка");

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
    public async Task System_dns_points_at_the_tunnel_while_it_is_up_and_follows_a_network_change()
    {
        await using var rig = await Rig.CreateAsync();
        var network = new FakeNetwork();
        var dns = new FakeSystemDns();
        var hour = TimeSpan.FromHours(1);
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            Network = network,
            SystemDns = dns,
            Probe = rig.ProbeAsync,
            Reconnect = new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        dns.Calls.ShouldBe(["apply 172.19.0.2"], "туннель поднят — DNS ОС спрашивает DNS туннеля");
        network.Raise();
        for (var i = 0; i < 50 && dns.Calls.Count < 2; i++)
        {
            await Task.Delay(100, Ct);
        }

        dns.Calls.ShouldBe(["apply 172.19.0.2", "apply 172.19.0.2"], "другая основная сеть — и ее DNS на туннель");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        dns.Calls[^1].ShouldBe("restore", "туннеля нет — DNS ОС как был");
    }

    /// <summary>
    /// VLESS без TLS по имени в режиме VPN: Xray соединяется с найденным адресом, и решает он. Открытый адрес — отказ
    /// PGC-SVC-005 до блока и ядер (адрес в своей сети пускают тесты с сервером по имени выше).
    /// </summary>
    [Fact]
    public async Task Server_without_encryption_by_name_is_refused_when_its_address_is_open()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with
        {
            Resolve = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("2001:db8::20")]),
        });

        var reply = await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "vpn.example.com" }), cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, ErrorCodes.UnsupportedServer));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.Contains("без TLS на открытом адресе", StringComparison.Ordinal));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Source == "xray", "ядра не запускались");
        rig.KillSwitch.IsEnabled.ShouldBeFalse("отказ — до блока");
    }

    /// <summary>
    /// WireGuard в режиме VPN за блоком при обрыве: Xray службы идет к серверу своим стеком TCP/IP (noKernelTun), а не
    /// адаптером wgN ОС — служба работает от root, и с CAP_NET_ADMIN Xray поднял бы в системе свой адаптер с правилами
    /// маршрутов, а трафик через него блок не пропускает.
    /// </summary>
    [Fact]
    public async Task Wireguard_server_carries_the_tunnel_behind_the_block_without_an_adapter_of_its_own()
    {
        await using var rig = await Rig.CreateAsync(kind: "wireguard");
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o));
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель", "через TUN, Xray и сервер WireGuard");
        rig.KillSwitch.IsEnabled.ShouldBeTrue();
        (await rig.Netns.RunAsync("ip", "-o", "link", "show")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 3)[1].Trim().Split('@')[0])
            .ShouldNotContain(name => name.StartsWith("wg", StringComparison.Ordinal), "своего адаптера WireGuard в системе Xray не ставит");
    }

    /// <summary>
    /// 172.19.0.1 держит другой адаптер машины (soak #4, Windows CI: «set ipv4 address: The object already exists»): служба
    /// поднимает туннель на свободной подсети, и с ней же — DNS ОС и перенаправление DNS роутера в туннель правилами блока.
    /// </summary>
    [Fact]
    public async Task Tunnel_address_taken_on_the_machine_moves_the_tunnel_with_its_dns()
    {
        NetworkNamespace.SkipUnlessAvailable("getent");
        await using var rig = await Rig.CreateAsync(services: true);
        rig.Netns.Run(null, "ip", "addr", "add", "172.19.0.1/20", "dev", "pgnet0");
        var dns = new FakeSystemDns();
        await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { SystemDns = dns });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        // Локальная сеть разрешена: DNS к роутеру блок перенаправляет на DNS туннеля — на новый адрес.
        var request = VpnRequest(rig.Definition, allowLan: true);
        request.Protection.Dns.Servers.Clear();
        request.Protection.Dns.Servers.Add("8.8.8.8");
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        var tunnelDns = IPAddress.Parse("10.255.253.2");
        service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message)
            .ShouldContain("Адрес TUN 172.19.0.1/30 занят другим адаптером — туннель на 10.255.253.1/30");
        (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель");
        dns.Calls.ShouldBe(["apply 10.255.253.2"], "DNS ОС — на DNS туннеля новой подсети");
        rig.KillSwitch.Last!.TunnelDns.ShouldBe(tunnelDns, "блок пропускает и перенаправляет DNS на адрес этого туннеля");
        (await rig.Netns.ResolveWithSystemAsync("example.org", tunnelDns)).ShouldStartWith("1.1.1.1", customMessage: "DNS туннеля отвечает Xray через сервер");
        (await rig.Netns.ResolveWithSystemAsync("example.com", IPAddress.Parse(rig.Lan.Url.Host))).ShouldStartWith("1.1.1.1",
            customMessage: "DNS к роутеру разрешенной сети — в туннель, на его новый адрес");
        rig.Dns!.Queries.ShouldBe(["example.org", "example.com"], ignoreOrder: true);
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
            Routes = rig.Routes,
            Probe = rig.ProbeAsync,
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
            BootBlock.Save(file, new HeldBlock(new KillSwitchPolicy("pgtun0", [new(IPAddress.Parse("198.51.100.20"), 443)], AllowLan: true,
                BlockOutsideDns: false, Direct: new DirectExit("/opt/pg/sing-box", 0x5047)), "vpn.example.com", Boot: true, Strict: false));
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

                BootBlock.Load(file).ShouldBeNull("«Защита при загрузке» выключена — блок перезагрузку не переживет");
                BootBlock.Read(file).ShouldNotBeNull("а перезапуск службы — переживет: файл блока помнит его");
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

            rig.KillSwitch.Last!.TunnelInterface.ShouldBeNull(
                "служба остановилась в «Защищено» — туннеля нет, блок без разрешения ему: на Windows так закрыта и пересылка");

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
    public async Task Vpn_with_boot_protection_connects_by_itself_after_a_reboot()
    {
        await using var rig = await Rig.CreateAsync();
        var sealer = new CountingSealer(BootConnectionTests.OsSealer());
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pg-boot-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var (block, connect) = (Path.Combine(folder, "boot-block.json"), Path.Combine(folder, "boot-connect.sealed"));
        GatewayOptions Boot(GatewayOptions o) => rig.Vpn(o) with { BootBlock = block, BootConnect = new BootConnection(connect, sealer) };
        try
        {
            await using (var service = await RunningService.StartAsync(options: Boot))
            {
                using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
                var off = VpnRequest(rig.Definition);
                off.Protection.BootProtection = false;
                await service.Client.ConnectAsync(off, cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
                (File.Exists(connect), sealer.Seals).ShouldBe((false, 0), "без «Защиты при загрузке» ключи не шифруются и на диск не попадают ни на миг");

                await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

                File.Exists(connect).ShouldBeTrue("подключение запомнено — зашифрованным хранилищем ОС");
            }

            // При загрузке имя сервера не найти (сети еще нет): служба подключается по запомненным адресам.
            var (stored, addresses) = new BootConnection(connect, sealer).Load().ShouldNotBeNull();
            stored.Server.Host = "server.invalid";
            new BootConnection(connect, sealer).Save(stored, addresses);

            // Перезагрузка: правил нет, служба запускается раньше сети и до входа пользователя.
            rig.KillSwitch.Disable();
            await using var next = await RunningService.StartAsync(options: Boot);
            using var updates = next.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

            (await next.Gateway.ConnectAtBootAsync(Ct)).ShouldBeTrue();

            var connected = (await ServiceIpcTests.ReadUntilAsync(updates, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
            connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
            connected.ServerId.ShouldBe("srv-1");
            (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "окна нет, а защита работает");
            rig.KillSwitch.IsEnabled.ShouldBeTrue();

            // «Защиту при загрузке» выключили на ходу (окно подключается заново) — ключи служба забывает.
            var bootOff = VpnRequest(rig.Definition);
            bootOff.Protection.BootProtection = false;
            await next.Client.ConnectAsync(bootOff, cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(updates, s => s.State == V1.ConnectionState.Connecting);
            await ServiceIpcTests.ReadUntilAsync(updates, s => s.State == V1.ConnectionState.Connected);
            (BootBlock.Load(block) is null, File.Exists(connect)).ShouldBe((true, false));
            await next.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(updates, s => s.State == V1.ConnectionState.Connecting);
            await ServiceIpcTests.ReadUntilAsync(updates, s => s.State == V1.ConnectionState.Connected);

            // Строгий режим: защиту выключили вручную — после перезагрузки блок, а не подключение.
            await next.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);
            (File.Exists(block), File.Exists(connect)).ShouldBe((true, false));
            BootBlock.Read(block)!.Strict.ShouldBeTrue("строгий режим помнит и перезагрузка");

            await next.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
            (File.Exists(block), File.Exists(connect), rig.KillSwitch.IsEnabled).ShouldBe((false, false, false));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_boot_block_the_service_does_not_connect_by_itself()
    {
        // Здесь важна логика службы, а не хранилище ОС (его проверяют BootConnectionTests): тест идет на всех ОС.
        var sealer = new PlainSealer();
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pg-boot-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var connect = Path.Combine(folder, "boot-connect.sealed");
        try
        {
            // Подключение запомнено, а блока при запуске нет: защиту выключили или блок не встал — без защиты не подключаться самой.
            var (definition, _) = XrayKit.Pair("vless", 443);
            new BootConnection(connect, sealer).Save(VpnRequest(definition), [IPAddress.Parse("198.51.100.20")]);
            var killSwitch = new FakeKillSwitch();
            await using var service = await RunningService.StartAsync(options: o => o with
            {
                CanVpn = true,
                KillSwitch = killSwitch,
                BootConnect = new BootConnection(connect, sealer),
            });

            (await service.Gateway.ConnectAtBootAsync(Ct)).ShouldBeFalse();
            (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off);
            killSwitch.Policy.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Boot_connect_that_is_refused_keeps_the_block()
    {
        await using var rig = await Rig.CreateAsync();
        var sealer = BootConnectionTests.OsSealer();
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pg-boot-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var (block, connect) = (Path.Combine(folder, "boot-block.json"), Path.Combine(folder, "boot-connect.sealed"));
        GatewayOptions Boot(GatewayOptions o) => rig.Vpn(o) with { Routes = rig.Routes, BootBlock = block, BootConnect = new BootConnection(connect, sealer) };
        try
        {
            await using (var service = await RunningService.StartAsync(options: Boot))
            {
                using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
                await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
            }

            // После перезагрузки другой VPN поднялся раньше: подключение служба не начинает, а блок остается.
            rig.KillSwitch.Disable();
            rig.StartOtherVpn();
            await using var next = await RunningService.StartAsync(options: Boot);

            (await next.Gateway.ConnectAtBootAsync(Ct)).ShouldBeFalse();

            var status = await next.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
            (status.State, status.Error?.Code).ShouldBe((V1.ConnectionState.Blocked, ErrorCodes.OtherVpn));
            rig.KillSwitch.IsEnabled.ShouldBeTrue("отказ при загрузке блок не снимает");
            await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");
            next.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
                e.Message == "Защита при загрузке: подключиться не удалось — трафик заблокирован до подключения или выключения защиты");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
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
        rig.KillSwitch.Last!.TunnelInterface.ShouldBeNull("туннеля нет — и разрешения ему нет: на Windows закрыта и пересылка");
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
            Trace = Rig.Trace,
            ExitIp = rig.ExitIpAsync,
            Network = network,
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

        /// <summary>Сайт за сервером: туда сервер ведет соединения TCP.</summary>
        public LocalSite Site { get; private init; } = null!;

        /// <summary>DNS за сервером (запросы на порт 53) — со <c>services</c>, иначе null.</summary>
        public TestDns? Dns { get; private init; }

        /// <summary>
        /// Эхо UDP на <see cref="Public"/>:443, как QUIC сайта, — со <c>services</c>, иначе null. Сервер шлет UDP по адресу
        /// назначения, без переадресации: ответ должен прийти с того же адреса, иначе TUN его не примет.
        /// </summary>
        public UdpEcho? Udp { get; private init; }

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
            options with { CanVpn = true, SingBoxPath = XrayKit.SingBox, KillSwitch = KillSwitch, TunnelNetns = Netns.Path, TunnelUdp = Netns.OpenSocksUdpAsync };

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
        /// Он же по IPv6: адрес не из частных сетей (их закрывает правило локальной сети Xray), через сервер — на его сайт
        /// IPv6, а у сервера без IPv6 — никуда.
        /// </summary>
        public static Uri Trace6 { get; } = new("http://[2606:4700:4700::1111]/");

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

        /// <summary>
        /// Запрос DNS к <paramref name="resolver"/> из сокета, привязанного к карте пространства, — как его шлет DNS ОС по каждой
        /// карте; ответ пришел — true.
        /// </summary>
        public async Task<bool?> DnsPastTunnelAsync(IPEndPoint resolver)
        {
            try
            {
                await Netns.ResolveAsync("example.com", resolver.Address, seconds: 2, device: "pgnet0");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Проверочный адрес по IPv6 напрямую — сайт на адресе машины <paramref name="host6"/>: правило nftables пространства ведет
        /// туда запросы к <see cref="Trace6"/>, которые уходят в сеть машины.
        /// </summary>
        public LocalSite DirectTrace6(string page, IPAddress host6)
        {
            var site = new LocalSite(page, host6);
            Netns.Run($$"""
                table ip6 pgtest6 {
                    chain output {
                        type nat hook output priority -100; policy accept;
                        oifname "pgnet0" ip6 daddr {{Trace6.Host.Trim('[', ']')}} tcp dport {{Trace6.Port}} dnat to [{{host6}}]:{{site.Url.Port}}
                    }
                }
                """, "nft", "-f", "-");
            return site;
        }

        /// <summary>
        /// Внешний IP — запросом изнутри пространства, как у службы: без прокси (порт 0) или через вход проверки. Ждать
        /// не меньше 5 с: запуск nsenter и curl на нагруженной машине (тесты окна рядом) съедал 2 с замера напрямую.
        /// </summary>
        public async Task<string?> ExitIpAsync(ServerProbe probe, CancellationToken cancellationToken)
        {
            var seconds = ((int)Math.Ceiling(Math.Max(probe.Timeout.TotalSeconds, 5))).ToString(CultureInfo.InvariantCulture);
            var proxy = probe.Credentials is { } user ? ["-x", $"socks5h://{user.UserName}:{user.Password}@127.0.0.1:{probe.Port}"] : Array.Empty<string>();
            try
            {
                return LatencyProbe.TraceIp(await Netns.RunAsync(["curl", "-sS", "-g", "-m", seconds, .. proxy, probe.Target.ToString()]));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <param name="page">Страница сайта сервера.</param>
        /// <param name="page6">Страница сайта сервера для адресов IPv6; null — тот же сайт.</param>
        /// <param name="serverIpv6">Сервер открывает IPv6; false — соединения с адресами IPv6 он закрывает.</param>
        /// <param name="services">За сервером еще DNS (порт 53, ответ — 1.1.1.1) и эхо UDP на <see cref="Public"/>:443.</param>
        /// <param name="kind">Протокол сервера (<see cref="XrayKit.Pair(string, int)"/>).</param>
        public static async Task<Rig> CreateAsync(
            string page = "через туннель", string? page6 = null, bool serverIpv6 = true, bool services = false, string kind = "vless")
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
                var (definition, serverConfig) = XrayKit.Pair(kind, serverPort, host);
                // Выход сервера теста — на свой сайт: из машины ничего не уходит.
                serverConfig["outbounds"] = new JsonArray(
                    new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
                if (page6 is not null || !serverIpv6)
                {
                    // Адреса IPv6 — на свой сайт или, у сервера без IPv6, в никуда.
                    var site6 = new LocalSite(page6 ?? page);
                    owned.Add(site6);
                    serverConfig["outbounds"]!.AsArray().Add(serverIpv6
                        ? new JsonObject { ["tag"] = "v6", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site6.Url.Port}" } }
                        : new JsonObject { ["tag"] = "v6", ["protocol"] = "blackhole" });
                    serverConfig["routing"] = new JsonObject
                    {
                        ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("::/0"), ["outboundTag"] = "v6" }),
                    };
                }

                TestDns? dns = null;
                UdpEcho? udp = null;
                if (services)
                {
                    dns = new TestDns(IPAddress.Parse("1.1.1.1"));
                    owned.Add(dns);
                    udp = new UdpEcho();
                    owned.Add(udp);
                    serverConfig["outbounds"]!.AsArray().Add(
                        new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dns.Port}" } });
                    serverConfig["outbounds"]!.AsArray().Add(
                        new JsonObject { ["tag"] = "udp", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{udp.Port}" } });
                    var routing = serverConfig["routing"] as JsonObject ?? new JsonObject { ["rules"] = new JsonArray() };
                    serverConfig["routing"] = routing;
                    routing["rules"]!.AsArray().Insert(0, new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" });
                    routing["rules"]!.AsArray().Insert(1, new JsonObject { ["type"] = "field", ["network"] = "udp", ["outboundTag"] = "udp" });
                }

                var server = await XrayKit.Start(serverConfig, serverPort);
                var killSwitch = new CountingKillSwitch(new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments])));

                // Убирать — в обратном порядке: сайты, потом пространство; сервер — раньше всего.
                owned.Reverse();
                return new Rig(netns, lan, @public, definition, killSwitch, (serverConfig, serverPort, server), [.. owned]) { Site = site, Dns = dns, Udp = udp };
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

    /// <summary>
    /// Подключение в режиме VPN; локальная сеть закрыта, если не сказано иначе. Строгие маршруты служба ставит, только если в
    /// ядре ОС есть IPv6.
    /// </summary>
    /// <summary>Режим VPN с блоком к серверу, за который ядро Xray не возьмется (<see cref="XrayKit.Refused"/>).</summary>
    private static V1.ConnectRequest Refused() => VpnRequest(XrayKit.Refused);

    private static V1.ConnectRequest VpnRequest(ServerDefinition server, bool killSwitch = true, RoutingSettings? routing = null, bool allowLan = false,
        bool strictRoutes = true, bool blockIpv6 = true, bool fakeDns = false, bool blockOutsideDns = true)
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Vpn,
            KillSwitch = killSwitch,
            Routing = routing ?? RoutingSettings.Default,
            Protection = ProtectionSettings.Default with { AllowLan = allowLan, BlockIpv6 = blockIpv6, FakeDns = fakeDns, BlockOutsideDns = blockOutsideDns },
            Connection = ConnectionSettings.Default with { ConnectTimeoutSeconds = 20, StrictRoutes = strictRoutes },
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
        private readonly List<KillSwitchPolicy> _policies = [];

        public int Enables { get; private set; }

        public KillSwitchPolicy? Last { get; private set; }

        /// <summary>Адаптер туннеля в каждом поставленном блоке, по порядку.</summary>
        public IReadOnlyList<string?> Tunnels
        {
            get
            {
                lock (_policies)
                {
                    return [.. _policies.Select(p => p.TunnelInterface)];
                }
            }
        }

        /// <summary>Все поставленные блоки, по порядку.</summary>
        public IReadOnlyList<KillSwitchPolicy> Policies
        {
            get
            {
                lock (_policies)
                {
                    return [.. _policies];
                }
            }
        }

        /// <summary>Какие блоки брандмауэр не принимает; null — все принимает.</summary>
        public Func<KillSwitchPolicy, bool>? Refuses { get; set; }

        public string Name => inner.Name;

        public bool IsEnabled => inner.IsEnabled;

        public bool IsIntact => inner.IsIntact;

        public void Enable(KillSwitchPolicy policy)
        {
            if (Refuses?.Invoke(policy) == true)
            {
                throw new IOException("nft: правила не приняты");
            }

            Enables++;
            Last = policy;
            lock (_policies)
            {
                _policies.Add(policy);
            }

            inner.Enable(policy);
        }

        public void Disable() => inner.Disable();
    }

    /// <summary>Хранилище ОС, которое считает, сколько раз в него шифровали.</summary>
    private sealed class CountingSealer(ISecretSealer inner) : ISecretSealer
    {
        public int Seals { get; private set; }

        public string Name => inner.Name;

        public byte[] Seal(byte[] plain)
        {
            Seals++;
            return inner.Seal(plain);
        }

        public byte[]? Unseal(byte[] sealedData) => inner.Unseal(sealedData);
    }

    /// <summary>Блок при обрыве без брандмауэра: помнит политику и сколько раз его снимали.</summary>
    private sealed class FakeKillSwitch(bool enabled = false) : IKillSwitch
    {
        public string Name => "подделка";

        public bool IsEnabled { get; private set; } = enabled;

        public KillSwitchPolicy? Policy { get; private set; }

        public bool Fails { get; set; }

        public bool DisableFails { get; set; }

        public string? Warning { get; set; }

        public int Enables { get; private set; }

        public int Disabled { get; private set; }

        public void Enable(KillSwitchPolicy policy)
        {
            if (Fails)
            {
                throw new IOException("nft: правила не приняты");
            }

            Enables++;
            IsEnabled = true;
            Policy = policy;
        }

        public void Disable()
        {
            if (DisableFails)
            {
                throw new IOException("nft: таблица занята");
            }

            Disabled++;
            IsEnabled = false;
        }
    }
}
