using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Долгий прогон режима прокси (<see cref="Soak"/>) на любой ОС: сбои по кругу — сервер пропал, смена сети, сон, ядро убито,
/// пауза, отключение. После каждого — снова «Защищено» и системный прокси на входе ядра; ресурсы службы не растут.
/// </summary>
public sealed partial class ReconnectTests
{
    [Fact]
    public async Task Soak_proxy_mode_survives_failures_in_a_loop()
    {
        var duration = Soak.Duration();
        var proxy = new FakeSystemProxy();
        var network = new FakeNetwork();
        var backup = Path.Combine(Path.GetTempPath(), "pg-soak-proxy-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            await using var rig = await ProxyRig.CreateAsync(Fast with { GiveUpAfter = TimeSpan.FromHours(1) }, network, systemProxy: (proxy, backup));
            var port = XrayKit.FreePort();
            var request = ServiceIpcTests.Request(rig.Definition, port, systemProxy: true);
            request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
            await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
            await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, "подключение");

            var started = Stopwatch.StartNew();
            ResourceSample? warm = null;
            var cycles = 0;
            while (cycles == 0 || started.Elapsed < duration)
            {
                await StepAsync("сервер пропал и вернулся", async () =>
                {
                    rig.StopServer();
                    await UntilAsync(s => s.State == ConnectionState.Reconnecting, "переподключение");
                    await Task.Delay(TimeSpan.FromSeconds(1), Ct);
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
                await StepAsync("Xray убит", async () =>
                {
                    var before = CoreProcessId(rig.Service);
                    Process.GetProcessById(before).Kill();
                    for (var waited = Stopwatch.StartNew(); CoreProcessId(rig.Service) == before; await Task.Delay(100, Ct))
                    {
                        waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "ядро поднято заново");
                    }
                });
                await StepAsync("пауза для входа в Wi-Fi", async () =>
                {
                    await rig.Service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) },
                        cancellationToken: Ct);
                    await UntilAsync(s => s.IsPaused, "пауза");
                    proxy.Port.ShouldBeNull("на паузе браузер идет напрямую");
                    await rig.Service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);
                });
                await StepAsync("отключение и подключение", async () =>
                {
                    await rig.Service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
                    await UntilAsync(s => s.State == ConnectionState.Off, "отключение");
                    proxy.Port.ShouldBeNull("отключено — системный прокси прежний");
                    await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
                });

                cycles++;
                warm ??= ResourceSample.Take();
            }

            ResourceSample.Take().ShouldNotGrowFrom(warm!);
            TestContext.Current.TestOutputHelper?.WriteLine($"Долгий прогон режима прокси: кругов — {cycles} за {started.Elapsed:hh\\:mm\\:ss}");

            async Task StepAsync(string what, Func<Task> act)
            {
                await act();
                await UntilAsync(s => s is { State: ConnectionState.Connected, IsPaused: false }, what);
                for (var waited = Stopwatch.StartNew(); proxy.Port != port; await Task.Delay(100, Ct))
                {
                    waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), $"{what}: системный прокси — снова на входе ядра");
                }
            }

            async Task UntilAsync(Func<ConnectionStatus, bool> done, string what)
            {
                for (var waited = Stopwatch.StartNew(); ; await Task.Delay(100, Ct))
                {
                    var status = (await rig.Service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
                    if (done(status))
                    {
                        return;
                    }

                    waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"{what}: {status.State}\nЖурнал службы:\n"
                        + string.Join('\n', rig.Service.Journal.Tail(40).Select(e => $"{e.Level} {e.Source}: {e.Message}")));
                }
            }
        }
        finally
        {
            File.Delete(backup);
        }
    }
}
