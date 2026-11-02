using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class ReconnectTests
{
    private static readonly NetworkInfo Wifi = new(NetworkKind.WiFi, null, false, false);
    private static readonly ReconnectTiming OfflineTiming = new(TimeSpan.FromHours(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

    [Fact]
    public async Task An_initial_connect_without_a_physical_network_waits_before_starting_any_core()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = NetworkInfo.Unknown;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: () => network, time: time);
        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeTrue("успешное начальное чтение None подтверждает отсутствие сети");

        (await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct)).Accepted.ShouldBeTrue();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting);
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50, Ct);
        ProxyStarts(rig.Service).ShouldBe(0);
        Attempts(rig.Service).ShouldBe(0);

        network = Wifi;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);
        ProxyStarts(rig.Service).ShouldBe(1);
        rig.Service.Gateway.Status.ServerId.ShouldBe("srv-1");
    }

    [Fact]
    public async Task A_gateway_appearing_without_an_os_event_is_found_by_the_local_recheck_and_skips_backoff()
    {
        var time = new OfflineClock();
        var events = new FakeNetwork();
        var network = Wifi;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time.Clock);
        network = NetworkInfo.Unknown;
        events.Raise();
        var polling = await time.Polls.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Свойства адаптера изменились, но ОС больше не прислала Changed: именно этот случай нельзя ждать вечно.
        network = Wifi;
        time.Clock.Advance(TimeSpan.FromSeconds(5));
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time.Clock);

        polling.IsDisposed.ShouldBeTrue();
        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeFalse();
        Attempts(rig.Service).ShouldBe(1, "локальный замер подгоняет попытку, не оставляет прежнюю часовую паузу");
        ProxyStarts(rig.Service).ShouldBe(2);
    }

    [Fact]
    public async Task Disconnect_disposes_the_local_recheck_timer_and_restoration_cannot_restart_the_old_intent()
    {
        var time = new OfflineClock();
        var events = new FakeNetwork();
        var network = Wifi;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time.Clock);
        network = NetworkInfo.Unknown;
        events.Raise();
        var polling = await time.Polls.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await rig.Service.Gateway.DisconnectAsync(cancellationToken: Ct);
        await WaitOfflineStateAsync(rig.Service.Gateway, _ => polling.IsDisposed);
        network = Wifi;
        time.Clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50, Ct);

        rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Off);
        ProxyStarts(rig.Service).ShouldBe(1);
        time.Polls.Reader.TryRead(out _).ShouldBeFalse("законченный цикл не оставляет очередного периодического ожидания");
    }

    [Fact]
    public async Task Offline_wait_does_not_probe_or_restart_and_restoration_resumes_the_same_profile_at_once()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var probes = 0;
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probes);
            return LatencyProbe.ReachesAsync(probe, cancellationToken);
        }

        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, probe: Probe, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time);
        probes.ShouldBe(1);
        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting && s.NetworkUnavailable);
        var attempt = rig.Service.Gateway.Status.Attempt;

        time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(50, Ct);

        rig.Service.Gateway.Status.Attempt.ShouldBe(attempt, "ожидание физической сети не является попытками VPN");
        Attempts(rig.Service).ShouldBe(0);
        ProxyStarts(rig.Service).ShouldBe(1);
        probes.ShouldBe(1, "при известном отсутствии сети не нужны даже две диагностические HTTP-пробы");
        (await rig.Service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).NetworkUnavailable.ShouldBeTrue();

        network = Wifi;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);

        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeFalse();
        rig.Service.Gateway.Status.ServerId.ShouldBe("srv-1");
        ProxyStarts(rig.Service).ShouldBe(2);
        Attempts(rig.Service).ShouldBe(1, "восстановление не ждет часовую паузу и не меняет профиль");
    }

    [Fact]
    public async Task Five_minute_policy_expires_offline_without_a_probe_and_keeps_the_reason_after_network_returns()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time, V1.ReconnectPolicy.UpTo5Minutes);

        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting);
        time.Advance(TimeSpan.FromMinutes(4));
        events.Raise();
        time.Advance(TimeSpan.FromSeconds(59));
        rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Reconnecting);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Error);

        rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkUnavailable);
        Attempts(rig.Service).ShouldBe(0, "лимит истекает в ожидании сети, не после новой попытки");
        ProxyStarts(rig.Service).ShouldBe(1);

        network = Wifi;
        events.Raise();
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50, Ct);
        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeFalse();
        rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Error);
        rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkUnavailable, "восстановление сети не делает прежний отказ ошибкой сервера");
        ProxyStarts(rig.Service).ShouldBe(1, "исчерпанная политика требует ручного повтора");
    }

    [Fact]
    public async Task Offline_deadline_keeps_its_reason_when_the_last_local_read_already_sees_the_restored_network()
    {
        var time = new OfflineClock();
        var events = new FakeNetwork();
        var network = Wifi;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time.Clock, V1.ReconnectPolicy.UpTo5Minutes);
        rig.StopServer();
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
            e.Message == "Сеть сменилась, сервер не отвечает — переподключение к тому же серверу");

        network = NetworkInfo.Unknown;
        events.Raise();
        await time.Polls.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        network = Wifi;
        time.Clock.Advance(TimeSpan.FromMinutes(5));
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Error);

        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeFalse("последний локальный замер уже увидел сеть");
        rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkUnavailable,
            "истекшее ожидание сети сохраняет причину, а не возвращает прежнее обвинение сервера");
        Attempts(rig.Service).ShouldBe(0, "сеть вернулась после срока — лишняя попытка не начинается");
    }

    [Fact]
    public async Task Never_policy_reports_offline_at_once_instead_of_waiting_or_checking_the_server()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var probes = 0;
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probes);
            return LatencyProbe.ReachesAsync(probe, cancellationToken);
        }
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, probe: Probe, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time, V1.ReconnectPolicy.Never);

        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Error);

        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeTrue();
        rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkUnavailable);
        Attempts(rig.Service).ShouldBe(0);
        probes.ShouldBe(1);
    }

    [Fact]
    public async Task A_never_policy_confirmation_rejected_during_proxy_apply_rolls_back_the_new_proxy()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var proxy = new NetworkDroppingProxy(() =>
        {
            network = NetworkInfo.Unknown;
            events.Raise();
        });
        var backup = Path.Combine(Path.GetTempPath(), "pg-offline-confirm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, systemProxy: (proxy, backup), currentNetwork: () => network, time: time);
            var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort(), systemProxy: true);
            request.Connection.Reconnect = V1.ReconnectPolicy.Never;
            (await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct)).Accepted.ShouldBeTrue();
            await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Error, time);

            rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeTrue();
            rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkUnavailable);
            proxy.Enables.ShouldBe(1, "событие приходит в узком промежутке после ответа ядра и до публикации Connected");
            proxy.Port.ShouldBeNull("впервые установленный прокси отвергнутой попытки возвращается к прежнему состоянию");
            File.Exists(backup).ShouldBeFalse();
            rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e =>
                e.Message == "Подключено: прокси на 127.0.0.1 готов");
            network = Wifi;
            events.Raise();
            time.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(50, Ct);
            proxy.Port.ShouldBeNull("после возврата сети браузер не остается направленным в остановленное ядро");
            Attempts(rig.Service).ShouldBe(0);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public async Task Failure_to_read_the_network_releases_offline_wait_instead_of_claiming_a_confirmed_outage()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var unreadable = false;
        NetworkInfo Read() => unreadable ? throw new PlatformNotSupportedException("test network state is unknown") : network;
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, currentNetwork: Read, time: time);
        await ConnectOfflineRigAsync(rig, time);

        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting && s.NetworkUnavailable);
        unreadable = true;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);

        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeFalse("неизвестная сеть не подтверждает отсутствие сети и не блокирует восстановление навсегда");
        ProxyStarts(rig.Service).ShouldBe(2);
    }

    [Fact]
    public async Task A_successful_probe_completed_after_network_loss_cannot_publish_connected()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var answers = Channel.CreateUnbounded<TaskCompletionSource<bool>>();
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answers.Writer.TryWrite(answer);
            return answer.Task;
        }
        async Task<TaskCompletionSource<bool>> Next() => await answers.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, probe: Probe, currentNetwork: () => network, time: time);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        var initial = Next();
        await WaitOfflineStateAsync(rig.Service.Gateway, _ => initial.IsCompleted, time);
        (await initial).SetResult(true);
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);

        events.Raise();
        (await Next()).SetResult(false);
        (await Next()).SetResult(false);
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting);
        events.Raise();
        var waiting = Next();
        await WaitOfflineStateAsync(rig.Service.Gateway, _ => waiting.IsCompleted, time);
        var late = await waiting;
        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.NetworkUnavailable);
        late.SetResult(true);
        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50, Ct);

        rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Reconnecting);
        rig.Service.Gateway.Status.NetworkUnavailable.ShouldBeTrue();
        answers.Reader.TryRead(out _).ShouldBeFalse("в ожидании сети новые пробы не запускаются");
    }

    [Fact]
    public async Task Network_loss_cancels_the_first_health_probe_without_starting_the_second_one()
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return LatencyProbe.ReachesAsync(probe, cancellationToken);
            }
            checking.TrySetResult();
            return answer.Task;
        }
        await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, probe: Probe, currentNetwork: () => network, time: time);
        await ConnectOfflineRigAsync(rig, time);
        events.Raise();
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        network = NetworkInfo.Unknown;
        events.Raise();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting && s.NetworkUnavailable);
        answer.TrySetResult(false);
        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50, Ct);

        calls.ShouldBe(2, "после отмены первой пробы повторная проба на подтвержденно отключенной сети не нужна");
        Attempts(rig.Service).ShouldBe(0);
    }

    [Fact]
    public async Task Losing_the_network_during_vpn_start_cancels_and_drains_it_before_any_tun_is_created()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var events = new FakeNetwork();
        var network = Wifi;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        async Task<string?> Hold(ServerProbe probe, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readers);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException error)
            {
                cancelled.TrySetResult();
                await cleanup.Task;
                throw new IOException("test startup cancelled before TUN", error);
            }
            finally
            {
                Interlocked.Decrement(ref readers);
            }
        }

        var firewall = new FakeFirewall();
        var journal = new ServiceJournal(TimeProvider.System);
        await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, new Uri("https://example.com/"),
            SingBoxPath: XrayKit.Xray, KillSwitch: firewall, Reconnect: OfflineTiming, Network: events, CurrentNetwork: () => network,
            Trace: new Uri("https://example.com/trace"), Trace6: new Uri("https://example.com/trace6"), ExitIp: Hold,
            Preflight: (_, _, _) => Task.FromResult(ProbeReachability.Responded(HttpStatusCode.NoContent)), TrafficWatch: Timeout.InfiniteTimeSpan),
            TimeProvider.System, journal);
        var definition = new ServerDefinition("test", string.Empty, ProxyProtocol.Vless, "198.51.100.20", 443,
            TransportOptions.Tcp, new SecurityOptions(TransportSecurity.Tls, Sni: "example.com"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: XrayKit.Uuid));
        var settings = AppSettings.Default with
        {
            Protection = ProtectionSettings.Default with { CheckLeaksAfterConnect = false, BootProtection = false },
            Connection = ConnectionSettings.Default with { ConnectTimeoutSeconds = 30, ProxyPort = XrayKit.FreePort() },
        };
        var request = new V1.ConnectRequest
        {
            Server = definition.ToContract("offline-start"), Mode = V1.Mode.Vpn,
            Protection = settings.ToContract(), Connection = settings.ToConnection(), Routing = settings.ToRouting(),
        };
        Task? disconnect = null;
        try
        {
            (await gateway.ConnectAsync(request, Ct)).Accepted.ShouldBeTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            network = NetworkInfo.Unknown;
            events.Raise();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            gateway.Status.State.ShouldBe(ConnectionState.Reconnecting);
            gateway.Status.NetworkUnavailable.ShouldBeTrue();
            firewall.IsEnabled.ShouldBeTrue("потеря физической сети не снимает выбранную защиту");

            disconnect = gateway.DisconnectAsync(cancellationToken: Ct);
            await Task.Delay(50, Ct);
            disconnect.IsCompleted.ShouldBeFalse("обработка None должна дожидаться очистки незавершенного запуска");
            cleanup.TrySetResult();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            readers.ShouldBe(0);
            gateway.Status.State.ShouldBe(ConnectionState.Off);
            firewall.IsEnabled.ShouldBeFalse();
            journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Source == "sing-box",
                "вместо sing-box указан Xray: тест не создает TUN даже при регрессии, а запуск TUN не должен быть достигнут");
        }
        finally
        {
            cleanup.TrySetResult();
            if (disconnect is not null)
            {
                await disconnect.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("pause")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public async Task A_changed_intent_cannot_be_revived_by_restoring_the_network(string change)
    {
        var time = new FakeTimeProvider();
        var events = new FakeNetwork();
        var network = Wifi;
        var proxy = new FakeSystemProxy();
        var backup = Path.Combine(Path.GetTempPath(), "pg-offline-proxy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using var rig = await ProxyRig.CreateAsync(OfflineTiming, events, systemProxy: (proxy, backup), currentNetwork: () => network, time: time);
            await ConnectOfflineRigAsync(rig, time, systemProxy: true);
            network = NetworkInfo.Unknown;
            events.Raise();
            await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Reconnecting && s.NetworkUnavailable);
            if (change == "disconnect")
            {
                await rig.Service.Gateway.DisconnectAsync(cancellationToken: Ct);
            }
            else if (change == "pause")
            {
                (await rig.Service.Gateway.PauseAsync(TimeSpan.FromMinutes(5), Ct)).ShouldNotBeNull();
            }
            else if (change == "replace")
            {
                var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
                request.Server.Id = "replacement";
                (await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct)).Accepted.ShouldBeTrue();
                await WaitOfflineStateAsync(rig.Service.Gateway, s => s.ServerId == "replacement" && s.State == ConnectionState.Reconnecting);
            }
            else
            {
                await rig.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            }

            network = Wifi;
            events.Raise();
            if (change == "replace")
            {
                await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);
                rig.Service.Gateway.Status.ServerId.ShouldBe("replacement");
                ProxyStarts(rig.Service).ShouldBe(2, "после восстановления запускается только новое намерение");
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(30));
                await Task.Delay(50, Ct);
                ProxyStarts(rig.Service).ShouldBe(1, "ни один ожидавший старый цикл не должен ожить");
                if (change == "disconnect")
                {
                    rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Off);
                }
                else if (change == "pause")
                {
                    rig.Service.Gateway.Status.IsPaused.ShouldBeTrue();
                }
            }
        }
        finally
        {
            File.Delete(backup);
        }
    }

    private static async Task ConnectOfflineRigAsync(ProxyRig rig, FakeTimeProvider time, V1.ReconnectPolicy? policy = null, bool systemProxy = false)
    {
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort(), systemProxy);
        if (policy is { } value)
        {
            request.Connection.Reconnect = value;
        }
        (await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct)).Accepted.ShouldBeTrue();
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);
    }

    private static async Task WaitOfflineStateAsync(Gateway gateway, Func<ConnectionStatus, bool> condition, FakeTimeProvider? time = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition(gateway.Status))
        {
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), gateway.Status.ToString());
            time?.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10, Ct);
        }
    }

    private static int ProxyStarts(RunningService service) => service.Journal.Tail(ServiceJournal.Capacity)
        .Count(e => e.Message.StartsWith("Ядро Xray запущено, процесс ", StringComparison.Ordinal));

    private sealed class FakeFirewall : IKillSwitch
    {
        public string Name => "fake";
        public bool IsEnabled { get; private set; }
        public bool IsIntact => IsEnabled;
        public void Enable(KillSwitchPolicy policy) => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }

    private sealed class NetworkDroppingProxy(Action dropNetwork) : ISystemProxy
    {
        private readonly FakeSystemProxy _proxy = new();
        public string Name => "test proxy with an in-flight network event";
        public int Enables { get; private set; }
        public int? Port => _proxy.Port;
        public SystemProxySnapshot Read() => _proxy.Read();
        public void Restore(SystemProxySnapshot previous) => _proxy.Restore(previous);
        public void Enable(int port)
        {
            _proxy.Enable(port);
            Enables++;
            dropNetwork();
        }
    }

    /// <summary>Фиксирует создание и снятие именно локального таймера ожидания сети, без реальных задержек в тесте.</summary>
    private sealed class OfflineClock : TimeProvider
    {
        public FakeTimeProvider Clock { get; } = new();
        public Channel<TrackedTimer> Polls { get; } = Channel.CreateUnbounded<TrackedTimer>();
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override long GetTimestamp() => Clock.GetTimestamp();
        public override long TimestampFrequency => Clock.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => Clock.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TrackedTimer(Clock.CreateTimer(callback, state, dueTime, period));
            if (dueTime == TimeSpan.FromSeconds(5) && period == Timeout.InfiniteTimeSpan)
            {
                Polls.Writer.TryWrite(timer);
            }
            return timer;
        }
    }

    private sealed class TrackedTimer(ITimer timer) : ITimer
    {
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            timer.Dispose();
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await timer.DisposeAsync();
        }
    }
}
