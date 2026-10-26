using System.Net;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Пауза для входа в Wi-Fi в «Блокировке»: строгий режим, блок с загрузки или с прошлого запуска. Блок снимается на
/// время и возвращается тот же — без «Подключения» и ядер; строгий режим служба помнит в файле блока.
/// </summary>
public sealed partial class VpnModeTests
{
    private static readonly V1.CaptivePauseRequest FiveMinutes = new() { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) };

    private static readonly V1.CaptivePauseRequest Cancel = new() { Cancel = true };

    // Блок строгого режима, запомненный прошлым запуском службы: сервер по имени, адрес найден тогда.
    private static readonly HeldBlock StrictHeld = new(
        new KillSwitchPolicy("pgtun0", [new(IPAddress.Parse("198.51.100.20"), 443)], AllowLan: false, Persistent: true), "vpn.invalid", Boot: true, Strict: true);

    [Fact]
    public async Task Strict_block_pauses_for_wifi_sign_in_and_closes_the_network_again()
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(options: rig.Vpn);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        await service.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.StrictBlock);

        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull("в строгой «Блокировке» пауза есть");

        var paused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null))[^1].ToCore();
        (paused.State, paused.StrictBlock, paused.Protection).ShouldBe((ConnectionState.Blocked, true, ProtectionChecks.Paused));
        rig.KillSwitch.IsEnabled.ShouldBeFalse();
        (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля", "страница входа в сеть открывается");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 2), "VPN на паузе не включается");
        var enables = rig.KillSwitch.Enables;

        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null);
        states.Select(s => s.State).ShouldNotContain(V1.ConnectionState.Connecting, "после паузы — тот же блок, а не подключение");
        var closed = states[^1].ToCore();
        (closed.State, closed.StrictBlock, closed.Protection).ShouldBe((ConnectionState.Blocked, true, ProtectionChecks.For(ConnectionState.Blocked)));
        (rig.KillSwitch.IsEnabled, rig.KillSwitch.Enables).ShouldBe((true, enables + 1));
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "сеть снова закрыта");

        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)) }, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);
        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null))[^1].StrictBlock.ShouldBeTrue("пауза кончилась сама — сеть закрыта");
        rig.KillSwitch.IsEnabled.ShouldBeTrue();

        // Вошли в Wi-Fi — щит подключает прямо с паузы.
        await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);

        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        (connected.State, connected.StrictBlock, connected.CaptivePauseUntil).ShouldBe((V1.ConnectionState.Connected, false, null));
        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");

        // Строгий режим: другой сервер, за который ядро не возьмется, — подключение прежнее остановлено, а сеть закрыта.
        var hysteria = VpnRequest(XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password))
            with { Transport = TransportOptions.Quic });
        hysteria.Protection.StrictMode = true;
        (await service.Client.ConnectAsync(hysteria, cancellationToken: Ct)).Error.Code.ShouldBe(ErrorCodes.UnsupportedServer);

        var refused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Blocked))[^1];
        (refused.StrictBlock, refused.Error?.Code).ShouldBe((true, ErrorCodes.UnsupportedServer));
        rig.KillSwitch.IsEnabled.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "отказ в строгом режиме сеть не открывает");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strict_block_after_a_restart_stays_strict_and_pauses(bool rulesLeft)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pg-strict-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var (file, connect) = (Path.Combine(folder, "boot-block.json"), Path.Combine(folder, "boot-connect.sealed"));
        try
        {
            BootBlock.Save(file, StrictHeld);
            var (definition, _) = XrayKit.Pair("vless", 443);
            new BootConnection(connect, new PlainSealer()).Save(VpnRequest(definition), [IPAddress.Parse("198.51.100.20")]);

            // Перезагрузка (правил нет) или перезапуск службы (правила стоят: nftables без перезагрузки, фильтры WFP).
            // Сборки ядер — не запускаемые: служба, начав подключение, дошла бы до блока и туннеля.
            var killSwitch = new FakeKillSwitch(enabled: rulesLeft);
            var program = typeof(VpnModeTests).Assembly.Location;
            await using var service = await RunningService.StartAsync(options: o => o with
            {
                XrayPath = program, CanVpn = true, SingBoxPath = program, KillSwitch = killSwitch, BootBlock = file,
                BootConnect = new BootConnection(connect, new PlainSealer()),
            });

            var status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
            (status.State, status.StrictBlock).ShouldBe((V1.ConnectionState.Blocked, true), "строгий режим пережил перезапуск");
            (await service.Gateway.ConnectAtBootAsync(Ct)).ShouldBeFalse("защиту выключили вручную — служба сама не подключается");

            (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull("блок известен — паузе есть что вернуть");
            (killSwitch.IsEnabled, File.Exists(file)).ShouldBe((false, true), "блок снят на время, а файл помнит его для перезагрузки");

            await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

            status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
            (status.State, status.StrictBlock, status.CaptivePauseUntil).ShouldBe((V1.ConnectionState.Blocked, true, null));
            killSwitch.IsEnabled.ShouldBeTrue();
            killSwitch.Policy!.Servers.ShouldBe(StrictHeld.Policy.Servers, "тот же блок, что в файле");
            (killSwitch.Policy.TunnelInterface, killSwitch.Policy.Persistent).ShouldBe(((string?)null, true), "без туннеля и постоянный, как был");

            await service.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);

            status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
            (status.State, status.StrictBlock, killSwitch.IsEnabled).ShouldBe((V1.ConnectionState.Blocked, true, true), "строгий режим не обходится выключением");

            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

            (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off);
            (killSwitch.IsEnabled, File.Exists(file), File.Exists(connect)).ShouldBe((false, false, false), "строгий режим выключен — сеть открыта и забыта");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Pause_in_a_block_that_the_service_does_not_know_is_refused()
    {
        // Правила стоят, а файла блока нет: что вернуть после паузы — неизвестно.
        var killSwitch = new FakeKillSwitch(enabled: true);
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch });

        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldBeNull();

        var status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
        (status.State, status.CaptivePauseUntil, killSwitch.IsEnabled).ShouldBe((V1.ConnectionState.Blocked, null, true));

        // Строгий режим: «Выключить» оставляет и такой блок — вернуть его после паузы нечем, но и открывать нельзя.
        await service.Client.DisconnectAsync(new V1.DisconnectRequest { KeepBlock = true }, cancellationToken: Ct);

        status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
        (status.State, status.StrictBlock, killSwitch.IsEnabled).ShouldBe((V1.ConnectionState.Blocked, true, true));

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off);
        killSwitch.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Tunnel_only_rules_left_without_the_kill_switch_are_lifted_at_start()
    {
        // Служба упала в «Защищено» с выключенным блоком при обрыве: правила «только туннель» остались, туннеля нет.
        using var held = new HeldFile(StrictHeld with { Boot = false, Strict = false, Open = true });
        var killSwitch = new FakeKillSwitch(enabled: true);

        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off,
            "блок при обрыве был выключен — сеть открыта, а не «Блокировка»");
        (killSwitch.IsEnabled, killSwitch.Disabled, File.Exists(held.Path)).ShouldBe((false, 1, false));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Правила «только туннель» с прошлого запуска сняты: блок при обрыве был выключен");
    }

    [Fact]
    public async Task Block_left_from_a_protected_connection_is_set_again_without_the_tunnel_at_start()
    {
        // Служба упала в «Защищено»: фильтры стоят с разрешением адаптеру туннеля (на Windows — без фильтров пересылки),
        // а адаптера уже нет.
        using var held = new HeldFile(StrictHeld with { Boot = false, Strict = false });
        var killSwitch = new FakeKillSwitch(enabled: true);

        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Blocked);
        killSwitch.Enables.ShouldBe(1, "блок заново по файлу блока — одной заменой правил");
        (killSwitch.Policy!.TunnelInterface, killSwitch.Policy.Servers.ShouldHaveSingleItem().Port).ShouldBe(((string?)null, 443),
            "без разрешения адаптеру туннеля: на Windows так встают и фильтры пересылки (хот-спот, ВМ)");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.Contains("остался с прошлого запуска", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_that_fell_during_a_pause_closes_the_network_at_start()
    {
        // Без «Защиты при загрузке»: служба упала на паузе (правил нет), а не машина перезагрузилась — так говорит отметка паузы.
        using var held = new HeldFile(StrictHeld with { Boot = false, Paused = true });
        var killSwitch = new FakeKillSwitch();

        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });

        var status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
        (status.State, status.StrictBlock, killSwitch.IsEnabled).ShouldBe((V1.ConnectionState.Blocked, true, true), "пауза кончилась вместе со службой");
        killSwitch.Policy!.Persistent.ShouldBeFalse("без «Защиты при загрузке» фильтры не постоянные");
        BootBlock.Read(held.Path)!.Paused.ShouldBeFalse("блок снова стоит");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Служба остановилась на паузе для входа в Wi-Fi: блок при обрыве возвращен");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Error_under_the_block_pauses_and_a_refusal_in_strict_mode_keeps_the_network_closed(bool strict)
    {
        using var held = new HeldFile(StrictHeld);
        File.Delete(held.Path);
        var killSwitch = new FakeKillSwitch();
        // Сборки ядер не запускаются: туннель не поднимется — «Ошибка» с блоком.
        var program = typeof(VpnModeTests).Assembly.Location;
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            XrayPath = program, CanVpn = true, SingBoxPath = program, KillSwitch = killSwitch, BootBlock = held.Path,
        });
        var request = VpnRequest(XrayKit.Pair("vless", 443).Server);
        request.Protection.StrictMode = strict;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await WaitStateAsync(service, V1.ConnectionState.Error);
        killSwitch.IsEnabled.ShouldBeTrue("туннель не поднялся — блок держит трафик");

        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull("в «Ошибке» под блоком сеть закрыта — пауза есть");
        killSwitch.IsEnabled.ShouldBeFalse();
        BootBlock.Read(held.Path)!.Paused.ShouldBeTrue("упадет служба на паузе — вернет блок при запуске");

        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

        var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        (status.State, status.IsPaused, killSwitch.IsEnabled).ShouldBe((ConnectionState.Error, false, true), "после паузы — тот же блок, без подключения");
        status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Error));
        var journal = service.Journal.Tail(ServiceJournal.Capacity);
        journal.ShouldContain(e => e.Message == "Пауза для входа в Wi-Fi закончилась (по кнопке): сеть снова закрыта");
        journal.Count(e => e.Message.StartsWith("Подключение в режиме VPN", StringComparison.Ordinal)).ShouldBe(1, "подключения после паузы не было");

        var other = VpnRequest(XrayKit.Pair("vless", 443).Server with { Host = "other.invalid" });
        other.Protection.StrictMode = strict;
        (await service.Client.ConnectAsync(other, cancellationToken: Ct)).Error.Code.ShouldBe(ErrorCodes.ServerNotFound);

        status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        if (strict)
        {
            (status.State, status.StrictBlock, status.ErrorCode, killSwitch.IsEnabled).ShouldBe((ConnectionState.Blocked, true, ErrorCodes.ServerNotFound, true),
                "строгий режим: отказ сеть не открывает");
        }
        else
        {
            (status.State, killSwitch.IsEnabled).ShouldBe((ConnectionState.Error, false), "без строгого режима отказ, как прежде, снимает блок");
        }
    }

    [Fact]
    public async Task Block_that_does_not_open_gives_no_pause()
    {
        using var held = new HeldFile(StrictHeld);
        var killSwitch = new FakeKillSwitch { DisableFails = true };
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });

        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldBeNull("правила не снялись — сеть закрыта, паузы нет");

        var status = await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
        (status.State, status.StrictBlock, status.CaptivePauseUntil).ShouldBe((V1.ConnectionState.Blocked, true, null));
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Пауза для входа в Wi-Fi не началась: блок при обрыве не снялся");
    }

    [Fact]
    public async Task Block_that_does_not_come_back_after_the_pause_is_an_honest_error()
    {
        using var held = new HeldFile(StrictHeld);
        var killSwitch = new FakeKillSwitch();
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });
        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull();
        killSwitch.Fails = true;

        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

        var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        (status.State, status.ErrorCode, status.IsPaused).ShouldBe((ConnectionState.Error, ErrorCodes.KillSwitchFailed, false));
        status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off), "блока нет — «заблокировано» не обещать");
    }

    [Fact]
    public async Task Service_stopped_during_the_pause_closes_the_network()
    {
        using var held = new HeldFile(StrictHeld);
        var killSwitch = new FakeKillSwitch();
        var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });
        await using (service)
        {
            (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull();
            killSwitch.IsEnabled.ShouldBeFalse();
        }

        killSwitch.IsEnabled.ShouldBeTrue("защиту не выключали — остановка службы возвращает блок");
        BootBlock.Read(held.Path)!.Strict.ShouldBeTrue();
    }

    [Fact]
    public async Task Refused_connect_in_a_strict_block_keeps_the_network_closed()
    {
        using var held = new HeldFile(StrictHeld);
        var killSwitch = new FakeKillSwitch();
        // Сборки ядер не запускаются: подключение доходит до блока и туннеля, туннель не поднимается.
        var program = typeof(VpnModeTests).Assembly.Location;
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            XrayPath = program, CanVpn = true, SingBoxPath = program, KillSwitch = killSwitch, BootBlock = held.Path,
        });
        (await service.Client.PauseForCaptivePortalAsync(FiveMinutes, cancellationToken: Ct)).Until.ShouldNotBeNull();
        var other = XrayKit.Pair("vless", 443).Server with { Host = "other.invalid" };

        var reply = await service.Client.ConnectAsync(VpnRequest(other), cancellationToken: Ct);

        reply.Error.Code.ShouldBe(ErrorCodes.ServerNotFound, "другой сервер под блоком не найти");
        var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        (status.State, status.StrictBlock, status.ErrorCode, status.IsPaused).ShouldBe((ConnectionState.Blocked, true, ErrorCodes.ServerNotFound, false));
        status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Blocked));
        killSwitch.IsEnabled.ShouldBeTrue("отказ в строгом режиме сеть не открывает, а пауза кончилась — сеть снова закрыта");

        // Под блоком: другой сервер и тот же сервер на другом порту адреса прежнего подключения не получают.
        foreach (var server in new[] { other, XrayKit.Pair("vless", 8443).Server with { Host = StrictHeld.Host } })
        {
            (await service.Client.ConnectAsync(VpnRequest(server), cancellationToken: Ct)).Error.Code.ShouldBe(ErrorCodes.ServerNotFound);
        }

        var again = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        killSwitch.Policy!.Servers.ShouldBe(StrictHeld.Policy.Servers, "блок тот же");
        again.Since.ShouldBeGreaterThan(status.Since, "новый отказ — новое время: окно снова покажет причину");

        // Тот же сервер по имени: под блоком DNS закрыт, адрес — найденный при прежнем подключении.
        var same = XrayKit.Pair("vless", 443).Server with { Host = StrictHeld.Host };
        (await service.Client.ConnectAsync(VpnRequest(same), cancellationToken: Ct)).Accepted.ShouldBeTrue("сервер найден — подключение идет");

        killSwitch.Policy!.Servers.ShouldBe([new KillSwitchEndpoint(IPAddress.Parse("198.51.100.20"), 443)]);
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Адрес сервера под блоком не найден — взят найденный при прежнем подключении");
    }

    [Fact]
    public async Task Block_without_boot_protection_is_forgotten_after_a_reboot()
    {
        using var held = new HeldFile(StrictHeld with { Boot = false });
        var killSwitch = new FakeKillSwitch();

        // Перезагрузка: правил нет, а блок без «Защиты при загрузке» ее не переживает.
        await using var service = await RunningService.StartAsync(options: o => o with { CanVpn = true, KillSwitch = killSwitch, BootBlock = held.Path });

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(V1.ConnectionState.Off);
        (killSwitch.IsEnabled, File.Exists(held.Path)).ShouldBe((false, false), "прежний блок забыт — после следующего перезапуска его не вернуть");
    }

    [Fact]
    public void Block_file_keeps_the_strict_mode_and_reads_the_old_format()
    {
        using var held = new HeldFile(StrictHeld with { Boot = false });

        var read = BootBlock.Read(held.Path).ShouldNotBeNull();
        (read.Host, read.Boot, read.Strict, read.Policy.Persistent, read.Policy.TunnelInterface).ShouldBe((StrictHeld.Host, false, true, false, (string?)null));
        read.Policy.Servers.ShouldBe(StrictHeld.Policy.Servers);
        BootBlock.Load(held.Path).ShouldBeNull("блок без «Защиты при загрузке» при загрузке не ставится");
        if (OperatingSystem.IsWindows())
        {
            new FileInfo(held.Path).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue("права папки (чтение для всех) не наследуются");
        }
        else
        {
            File.GetUnixFileMode(held.Path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, "в файле сервер пользователя — читает только служба");
        }

        File.WriteAllText(held.Path, """{"servers":[{"address":"198.51.100.20","port":443}],"allowLan":true,"lanDiscovery":false,"blockOutsideDns":true}""");
        var old = BootBlock.Read(held.Path).ShouldNotBeNull();
        (old.Boot, old.Strict, old.Host, old.Policy.Persistent).ShouldBe((true, false, string.Empty, true), "прежний файл писался только с «Защитой при загрузке»");

        File.WriteAllText(held.Path, "{испорчен");
        BootBlock.Read(held.Path).ShouldBeNull();
    }

    private static async Task WaitStateAsync(RunningService service, V1.ConnectionState state)
    {
        for (var i = 0; i < 100 && (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State != state; i++)
        {
            await Task.Delay(100, Ct);
        }

        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State.ShouldBe(state);
    }

    /// <summary>Файл блока во временной папке; удаляется вместе с ней.</summary>
    private sealed class HeldFile : IDisposable
    {
        private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-held-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        public HeldFile(HeldBlock block)
        {
            Path = System.IO.Path.Combine(_folder, "boot-block.json");
            BootBlock.Save(Path, block);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    /// <summary>«Хранилище», которое не шифрует: тесту нужна сама запись подключения, а не ключ машины.</summary>
    private sealed class PlainSealer : ISecretSealer
    {
        public string Name => "без шифрования";

        public byte[] Seal(byte[] plain) => plain;

        public byte[]? Unseal(byte[] sealedData) => sealedData;
    }
}
