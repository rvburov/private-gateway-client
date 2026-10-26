using System.Diagnostics;
using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Долгий прогон режима VPN (<see cref="Soak"/>): в своем сетевом пространстве с настоящими nftables, sing-box и Xray сбои
/// идут по кругу, а сторож каждые 250 мс проверяет, не ушел ли трафик мимо туннеля. Критерий этапа 4 — «восстановление без
/// незапланированного обхода туннеля».
/// </summary>
public sealed partial class VpnModeTests
{
    [Fact]
    public async Task Soak_vpn_survives_failures_in_a_loop_without_traffic_around_the_tunnel()
    {
        var duration = Soak.Duration();
        await using var rig = await Rig.CreateAsync();
        var network = new FakeNetwork();
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            BlockWatch = TimeSpan.FromMilliseconds(500),
            Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromHours(1) },
            Probe = rig.ProbeAsync,
            Network = network,
        });
        var request = VpnRequest(rig.Definition);
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, "подключение");
        await using var sentinel = new LeakSentinel(rig.Netns, rig.Lan.Url, "мимо туннеля");

        var started = Stopwatch.StartNew();
        ResourceSample? warm = null;
        var tables = Tables();
        var rules = Rules();
        var cycles = 0;
        while (cycles == 0 || started.Elapsed < duration)
        {
            await StepAsync("сервер пропал и вернулся", async () =>
            {
                rig.StopServer();
                await UntilAsync(s => s.State == ConnectionState.Reconnecting, "переподключение");
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                await rig.StartServerAsync();
            });
            await StepAsync("смена сети", () =>
            {
                network.Raise();
                return Task.Delay(TimeSpan.FromSeconds(1), Ct);
            });
            await StepAsync("сон и пробуждение", () =>
            {
                network.Wake(TimeSpan.FromMinutes(10));
                return Task.Delay(TimeSpan.FromSeconds(1), Ct);
            });
            await StepAsync("sing-box убит", () => KillAsync(Tunnel().SingBox));
            await StepAsync("Xray убит", () => KillAsync(Tunnel().Xray));
            await StepAsync("правила сняла другая программа", async () =>
            {
                // Пока сторож блока не поставил правила снова, мимо туннеля может уйти что угодно: это сделала другая
                // программа. Окно — только до возврата блока.
                using (sentinel.Allow())
                {
                    rig.Netns.Run(null, "nft", "flush", "ruleset");
                    for (var waited = Stopwatch.StartNew(); !rig.KillSwitch.IsIntact; await Task.Delay(100, Ct))
                    {
                        waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "сторож ставит блок снова за несколько своих тактов");
                    }
                }

                // Правила туннеля sing-box сняты тоже: без туннеля — переподключение.
                await KillAsync(Tunnel().SingBox);
            });
            await StepAsync("пауза для входа в Wi-Fi", async () =>
            {
                using (sentinel.Allow())
                {
                    await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) },
                        cancellationToken: Ct);
                    await UntilAsync(s => s.IsPaused, "пауза");
                    await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);
                    await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, "конец паузы");
                }
            });
            await StepAsync("отключение и подключение", async () =>
            {
                using (sentinel.Allow())
                {
                    await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
                    await UntilAsync(s => s.State == ConnectionState.Off, "отключение");
                    await service.Client.ConnectAsync(request, cancellationToken: Ct);
                    await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, "подключение");
                }
            });

            cycles++;
            warm ??= ResourceSample.Take();
            sentinel.Leaks.ShouldBeEmpty($"круг {cycles}: трафик мимо туннеля вне разрешенных окон");
        }

        sentinel.Activity = "конец";
        ResourceSample.Take().ShouldNotGrowFrom(warm!);
        (Tables(), Rules()).ShouldBe((tables, rules), "таблицы nftables и правила маршрутов пространства — те же, что после первого подключения");
        sentinel.Probes.ShouldBeGreaterThan(cycles * 20, "сторож проверял все время");
        sentinel.Leaks.ShouldBeEmpty();
        TestContext.Current.TestOutputHelper?.WriteLine($"Долгий прогон VPN: кругов — {cycles} за {started.Elapsed:hh\\:mm\\:ss}, запросов сторожа — {sentinel.Probes}, утечек нет");

        // Шаг: сбой, затем снова «Защищено» и трафик через туннель.
        async Task StepAsync(string what, Func<Task> act)
        {
            sentinel.Activity = what;
            await act();
            await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, what);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 5)).ShouldBe("через туннель", what);
                    break;
                }
                catch (InvalidOperationException) when (attempt < 3)
                {
                    // «Защищено» пришло, а маршруты туннеля встают еще долю секунды.
                    await Task.Delay(500, Ct);
                }
            }
        }

        async Task UntilAsync(Func<ConnectionStatus, bool> done, string what)
        {
            for (var waited = Stopwatch.StartNew(); ; await Task.Delay(100, Ct))
            {
                var status = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
                if (done(status))
                {
                    return;
                }

                waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"{what}: {status.State}\nЖурнал службы:\n"
                    + string.Join('\n', service.Journal.Tail(40).Select(e => $"{e.Level} {e.Source}: {e.Message}")));
            }
        }

        // Ядро убито — служба поднимает туннель заново: ждать нового процесса, а не прежнего «Защищено».
        async Task KillAsync(int pid)
        {
            var before = Tunnel();
            Process.GetProcessById(pid).Kill();
            for (var waited = Stopwatch.StartNew(); Tunnel() == before; await Task.Delay(100, Ct))
            {
                waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "туннель поднят заново новыми ядрами");
            }
        }

        (int Xray, int SingBox) Tunnel()
        {
            const string Prefix = "Туннель поднят: Xray — процесс ";
            const string Marker = ", sing-box — процесс ";
            var line = service.Journal.Tail(ServiceJournal.Capacity).Last(e => e.Message.StartsWith(Prefix, StringComparison.Ordinal)).Message;
            var at = line.IndexOf(Marker, StringComparison.Ordinal);
            return (int.Parse(line[Prefix.Length..at], CultureInfo.InvariantCulture), int.Parse(line[(at + Marker.Length)..], CultureInfo.InvariantCulture));
        }

        string Tables() => string.Join('\n', rig.Netns.Run(null, "nft", "list", "tables").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order());

        int Rules() => rig.Netns.Run(null, "ip", "rule").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
