using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Долгий прогон режима VPN на Windows (<see cref="Soak"/>): служба с настоящими блоком WFP и TUN sing-box, сбои по кругу, а
/// сторож каждые 250 мс проверяет, не ушел ли трафик мимо туннеля. Машина CI остается в сети: в TUN — только адрес теста и
/// пул FakeDNS (<see cref="Routes"/>), блок при обрыве — только для curl.exe сторожа (решение 438). Проверочный адрес —
/// настоящий 1.1.1.1: через туннель на него отвечает сайт теста за сервером, а настоящий ответ приходит только напрямую.
/// </summary>
public sealed partial class WindowsTunnelTests
{
    private const string SoakPage = "через туннель Windows, долгий прогон";

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Soak_vpn_on_windows_survives_failures_in_a_loop_without_traffic_around_the_tunnel()
    {
        var duration = Soak.Duration();
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        Assert.SkipUnless(File.Exists(curl), "нет curl.exe");
        var trace = new Uri($"http://{Public}/");
        var direct = await GetWithCurlAsync(curl, trace, seconds: 5, Ct);
        direct.ShouldNotBeNull("без туннеля проверочный адрес отвечает напрямую — иначе сторож ничего не увидит");
        direct.ShouldNotBe(SoakPage);

        using var site = new LocalSite(SoakPage);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        // Сервер теста: проверочный адрес — на сайт теста, DNS — настоящий (DNS адаптера туннеля Windows спрашивает и для
        // себя: ответы должны быть верными, иначе машина CI потеряет связь), остальное — блок.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom" });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" },
                new JsonObject { ["type"] = "field", ["ip"] = new JsonArray(Public + "/32"), ["outboundTag"] = "site" }),
        };
        var server = await XrayKit.Start(serverConfig, serverPort);
        var killSwitch = new WfpKillSwitch(onlyProgram: curl);
        var network = new FakeNetwork();
        try
        {
            await using var service = await RunningService.StartAsync(trace, options: o => o with
            {
                CanVpn = true,
                SingBoxPath = XrayKit.SingBox,
                KillSwitch = killSwitch,
                TunnelRoutes = Routes,
                BlockWatch = TimeSpan.FromMilliseconds(500),
                Reconnect = ReconnectTests.Fast with { GiveUpAfter = TimeSpan.FromHours(1) },
                Network = network,
            });
            var request = Request(definition);
            await service.Client.ConnectAsync(request, cancellationToken: Ct);
            await UntilAsync(service, s => s is { State: ConnectionState.Connected, IsPaused: false }, "подключение");
            await using var sentinel = new LeakSentinel(async cancellationToken =>
                await GetWithCurlAsync(curl, trace, seconds: 1, cancellationToken) is { } page && page != SoakPage);

            var started = Stopwatch.StartNew();
            ResourceSample? warm = null;
            var cycles = 0;
            while (cycles == 0 || started.Elapsed < duration)
            {
                await StepAsync("сервер пропал и вернулся", async () =>
                {
                    server.Dispose();
                    await UntilAsync(service, s => s.State == ConnectionState.Reconnecting, "переподключение");
                    await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                    server = await XrayKit.Start(serverConfig, serverPort);
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
                await StepAsync("sing-box убит", () => KillAsync(Tunnel(service).SingBox));
                await StepAsync("Xray убит", () => KillAsync(Tunnel(service).Xray));
                await StepAsync("фильтр сняла другая программа", async () =>
                {
                    // Пока сторож блока не поставил фильтры снова, мимо туннеля может уйти что угодно: это сделала другая
                    // программа. Окно — только до возврата блока.
                    using (sentinel.Allow())
                    {
                        WfpKillSwitchTests.RemoveFilter(WfpRules.FilterKey(1));
                        for (var waited = Stopwatch.StartNew(); !killSwitch.IsIntact; await Task.Delay(100, Ct))
                        {
                            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "сторож ставит блок снова за несколько своих тактов");
                        }
                    }
                });
                await StepAsync("пауза для входа в Wi-Fi", async () =>
                {
                    using (sentinel.Allow())
                    {
                        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) },
                            cancellationToken: Ct);
                        await UntilAsync(service, s => s.IsPaused, "пауза");
                        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);
                        await UntilAsync(service, s => s is { State: ConnectionState.Connected, IsPaused: false }, "конец паузы");
                    }
                });
                await StepAsync("отключение и подключение", async () =>
                {
                    using (sentinel.Allow())
                    {
                        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
                        await UntilAsync(service, s => s.State == ConnectionState.Off, "отключение");
                        killSwitch.IsEnabled.ShouldBeFalse("отключение снимает блок");
                        await service.Client.ConnectAsync(request, cancellationToken: Ct);
                        await UntilAsync(service, s => s is { State: ConnectionState.Connected, IsPaused: false }, "подключение");
                    }
                });

                cycles++;
                warm ??= ResourceSample.Take();
                sentinel.Leaks.ShouldBeEmpty($"круг {cycles}: трафик мимо туннеля вне разрешенных окон");
            }

            sentinel.Activity = "конец";
            ResourceSample.Take().ShouldNotGrowFrom(warm!);
            sentinel.Probes.ShouldBeGreaterThan(cycles * 20, "сторож проверял все время");
            sentinel.Leaks.ShouldBeEmpty();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Долгий прогон VPN на Windows: кругов — {cycles} за {started.Elapsed:hh\\:mm\\:ss}, запросов сторожа — {sentinel.Probes}, утечек нет");

            await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
            await UntilAsync(service, s => s.State == ConnectionState.Off, "отключение в конце");
            await WaitAsync(() => Adapter() is null, "адаптер убран после отключения", new CoreOutput());

            // Шаг: сбой, затем снова «Защищено» и проверочный адрес — через туннель.
            async Task StepAsync(string what, Func<Task> act)
            {
                sentinel.Activity = what;
                await act();
                await UntilAsync(service, s => s is { State: ConnectionState.Connected, IsPaused: false }, what);
                for (var attempt = 0; ; attempt++)
                {
                    var page = await GetWithCurlAsync(curl, trace, seconds: 5, Ct);
                    if (page == SoakPage)
                    {
                        break;
                    }

                    // «Защищено» пришло, а маршрут адаптера встает еще долю секунды.
                    attempt.ShouldBeLessThan(5, $"{what}: проверочный адрес через туннель ответил «{page}»");
                    await Task.Delay(500, Ct);
                }
            }

            // Ядро убито — служба поднимает туннель заново: ждать нового процесса, а не прежнего «Защищено».
            async Task KillAsync(int pid)
            {
                var before = Tunnel(service);
                Process.GetProcessById(pid).Kill();
                for (var waited = Stopwatch.StartNew(); Tunnel(service) == before; await Task.Delay(100, Ct))
                {
                    waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "туннель поднят заново новыми ядрами");
                }
            }
        }
        finally
        {
            server.Dispose();
            killSwitch.Disable();
        }
    }

    private static V1.ConnectRequest Request(ServerDefinition server)
    {
        // DNS Xray — обычный, по UDP через сервер к настоящему 8.8.8.8: DoH к 1.1.1.1 сервер теста отдал бы своему сайту.
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Vpn,
            KillSwitch = true,
            Protection = ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            Connection = ConnectionSettings.Default with { ConnectTimeoutSeconds = 20, Reconnect = ReconnectPolicy.UpTo5Minutes },
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

    private static async Task UntilAsync(RunningService service, Func<ConnectionStatus, bool> done, string what)
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

    /// <summary>Номера процессов ядер нынешнего туннеля — из журнала службы.</summary>
    private static (int Xray, int SingBox) Tunnel(RunningService service)
    {
        const string Prefix = "Туннель поднят: Xray — процесс ";
        const string Marker = ", sing-box — процесс ";
        var line = service.Journal.Tail(ServiceJournal.Capacity).Last(e => e.Message.StartsWith(Prefix, StringComparison.Ordinal)).Message;
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        return (int.Parse(line[Prefix.Length..at], CultureInfo.InvariantCulture), int.Parse(line[(at + Marker.Length)..], CultureInfo.InvariantCulture));
    }

    /// <summary>Страница через curl.exe (его одного держит блок теста); не ответила за <paramref name="seconds"/> — null.</summary>
    private static async Task<string?> GetWithCurlAsync(string curl, Uri page, int seconds, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(curl) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-sS", "-m", seconds.ToString(CultureInfo.InvariantCulture), page.ToString() })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? await output : null;
    }
}
