using System.Diagnostics;
using System.Runtime.Versioning;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// «Отель»: сервер за порталом не отвечает, пока не войти в сеть, а политика по умолчанию переподключается без конца. Пауза для
/// входа в Wi-Fi бывает и в «Переподключении», и в «Подключении»: попытки и ядра встают до конца паузы, сеть открыта без блока и
/// без туннеля, потом подключение начинается заново.
/// </summary>
public sealed partial class VpnModeTests
{
    private static readonly V1.CaptivePauseRequest Hour = new() { Duration = Duration.FromTimeSpan(TimeSpan.FromHours(1)) };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pause_while_reconnecting_stops_the_attempts_and_connects_again_after(bool killSwitch)
    {
        await using var rig = await Rig.CreateAsync();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"),
            options: o => rig.Vpn(o) with { Reconnect = ReconnectTests.Fast, Probe = rig.ProbeAsync });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        // Политика по умолчанию — переподключаться без конца.
        await service.Client.ConnectAsync(VpnRequest(rig.Definition, killSwitch: killSwitch), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        // Сервер за порталом.
        rig.StopServer();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting && s.Attempt >= 2, service.Journal);

        (await service.Client.PauseForCaptivePortalAsync(Hour, cancellationToken: Ct)).Until.ShouldNotBeNull("в «Переподключении» пауза есть");

        var paused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null, service.Journal))[^1].ToCore();
        (paused.State, paused.Protection).ShouldBe((ConnectionState.Reconnecting, ProtectionChecks.Paused));
        rig.KillSwitch.IsEnabled.ShouldBeFalse("на паузе ни блока, ни правил «только туннель»");
        var attempts = Attempts(service);
        (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля", "страница входа в сеть открывается напрямую");

        // Попытка — каждые 1–3 с (пауза до 0,8 с, туннель, проба до 2 с): за 4 с на паузе не было бы ни одной только без попыток.
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        Attempts(service).ShouldBe(attempts, "на паузе попыток нет — туннель к серверу закрыл бы страницу входа");
        (await rig.Netns.CurlAsync(rig.Lan.Url, seconds: 3)).ShouldBe("мимо туннеля", "и туннель прерванной попытки не встал");
        rig.KillSwitch.IsEnabled.ShouldBeFalse();

        // «Вернуть защиту», а сервер все еще не отвечает (в сеть не вошли): подключение заново и снова попытки.
        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);
        var again = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting && s.CaptivePauseUntil is null, service.Journal);
        again.Select(s => s.State).ShouldContain(V1.ConnectionState.Connecting, "после паузы — подключение заново, через «Подключение»");
        if (killSwitch)
        {
            rig.KillSwitch.IsEnabled.ShouldBeTrue("попытки идут снова — и блок при обрыве с ними");
            await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.CurlAsync(rig.Lan.Url, seconds: 2), "мимо туннеля — ничего");
        }

        // Вошли в сеть: сервер отвечает.
        (await service.Client.PauseForCaptivePortalAsync(Hour, cancellationToken: Ct)).Until.ShouldNotBeNull();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null, service.Journal);
        await rig.StartServerAsync();
        await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

        var back = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal))[^1].ToCore();
        (back.IsPaused, back.Protection.KillSwitch).ShouldBe((false, killSwitch ? CheckState.Ok : CheckState.Off));
        (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель");
        rig.KillSwitch.IsEnabled.ShouldBeTrue(killSwitch ? "блок при обрыве вернулся" : "правила «только туннель» — с туннелем");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.FetchAsync(rig.Lan.Url, seconds: 2, device: "pgnet0"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Pause_while_connecting_drops_the_tunnel_being_started_and_connects_after()
    {
        await using var rig = await Rig.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("pg-slow-");
        try
        {
            // sing-box поднимается на 2 с позже: «Подключение» длится, как с медленной сетью за порталом.
            var slow = Path.Combine(directory.FullName, "sing-box");
            await File.WriteAllTextAsync(slow, $"#!/bin/sh\nsleep 2\nexec '{XrayKit.SingBox}' \"$@\"\n", Ct);
            File.SetUnixFileMode(slow, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var service = await RunningService.StartAsync(options: o => rig.Vpn(o) with { SingBoxPath = slow });
            using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            await service.Client.ConnectAsync(VpnRequest(rig.Definition), cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting, service.Journal);

            (await service.Client.PauseForCaptivePortalAsync(Hour, cancellationToken: Ct)).Until.ShouldNotBeNull("в «Подключении» пауза есть");

            var paused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null, service.Journal))[^1].ToCore();
            (paused.State, paused.Protection).ShouldBe((ConnectionState.Connecting, ProtectionChecks.Paused));
            rig.KillSwitch.IsEnabled.ShouldBeFalse();

            // Запуск, начатый до паузы, прерван: адаптер туннеля не появлялся и тогда, когда sing-box поднялся бы, — страница входа
            // не уходила в туннель к серверу.
            for (var watched = Stopwatch.StartNew(); watched.Elapsed < TimeSpan.FromSeconds(3.5); await Task.Delay(100, Ct))
            {
                Should.Throw<InvalidOperationException>(() => rig.Netns.Run(null, "ip", "link", "show", SingBoxConfigBuilder.InterfaceName),
                    $"адаптер туннеля появился через {watched.Elapsed.TotalSeconds:0.0} с паузы");
            }

            service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Message.StartsWith("Туннель поднят", StringComparison.Ordinal));
            (await rig.Netns.CurlAsync(rig.Lan.Url)).ShouldBe("мимо туннеля");

            await service.Client.PauseForCaptivePortalAsync(Cancel, cancellationToken: Ct);

            // Конец паузы — то же «Подключение», уже без паузы: подключение заново.
            var resumed = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null, service.Journal))[^1].ToCore();
            (resumed.State, resumed.Protection).ShouldBe((ConnectionState.Connecting, ProtectionChecks.For(ConnectionState.Connecting)));
            var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error, service.Journal))[^1];
            connected.State.ShouldBe(V1.ConnectionState.Connected, connected.Error?.Message);
            (await rig.Netns.CurlAsync(Rig.Trace)).ShouldBe("через туннель");
            rig.KillSwitch.IsEnabled.ShouldBeTrue();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Сколько попыток переподключения служба начала.</summary>
    private static int Attempts(RunningService service) =>
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message.StartsWith("Переподключение, попытка", StringComparison.Ordinal));
}
