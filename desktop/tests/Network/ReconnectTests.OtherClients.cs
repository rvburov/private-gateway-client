using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Другой VPN или прокси-клиент, включенный во время подключения, служба замечает своей частой проверкой (решение 525), а не
/// только с пробой сервера раз в минуту.
/// </summary>
public sealed partial class ReconnectTests
{
    [Fact]
    public async Task Another_client_core_is_noticed_by_the_frequent_check_between_server_probes()
    {
        // Проба сервера и паузы — раз в час: успеть может только частая проверка других клиентов.
        var hour = TimeSpan.FromHours(1);
        IReadOnlyList<RunningProcess> running = [];
        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour), processes: () => running,
            othersWatch: TimeSpan.FromMilliseconds(200));
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        running = [new RunningProcess(7, "xray", "/opt/other-client/xray")];
        var noted = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.OtherClients.Count > 0, rig.Service.Journal))[^1];

        noted.OtherClients.Select(c => (c.Program, c.Sign)).ShouldBe([(string.Empty, V1.OtherClientSign.Process)],
            "признак без названия программы");
    }

    [Fact]
    public async Task Frequent_client_check_waits_through_captive_pause_without_scanning_or_notifying()
    {
        using var clock = new OtherClientsClock();
        var reads = 0;
        var blockRead = 0;
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishRead = new ManualResetEventSlim();
        IReadOnlyList<RunningProcess> running = [];
        await using var rig = await OtherClientsRig.CreateAsync(clock, () =>
        {
            Interlocked.Increment(ref reads);
            if (Interlocked.Exchange(ref blockRead, 0) == 1)
            {
                reading.TrySetResult();
                if (!finishRead.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException("Тест не закончил проверку процессов");
                }
            }

            return running;
        });
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort(), systemProxy: true);
        request.UserProxyManagedByWindow = true;
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);
        await clock.NextTimerAsync();
        (await rig.Service.Gateway.PauseAsync(TimeSpan.FromHours(1), Ct)).ShouldNotBeNull();
        var paused = rig.Service.Gateway.Status;
        paused.IsPaused.ShouldBeTrue();
        var scans = Volatile.Read(ref reads);
        running = [new RunningProcess(7, "xray", "/opt/other-client/xray")];

        clock.Tick();
        await clock.NextTimerAsync();

        Volatile.Read(ref reads).ShouldBe(scans, "на паузе частая проверка не читает процессы");
        var after = rig.Service.Gateway.Status;
        after.OtherClients.ShouldBeNull();
        NotificationPolicy.For(paused, after, null, DateTimeOffset.UtcNow).ShouldBeNull("пауза не дает уведомление о другом клиенте");
        await rig.Service.Gateway.ResumeAsync(Ct);
        // Пауза может прийти и после первого снимка: ОС еще читает процессы, а пользователь уже нажал паузу.
        Interlocked.Exchange(ref blockRead, 1);
        var tick = Task.Run(clock.Tick, Ct);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            (await rig.Service.Gateway.PauseAsync(TimeSpan.FromHours(1), Ct)).ShouldNotBeNull();
        }
        finally
        {
            finishRead.Set();
        }

        await tick;
        await clock.NextTimerAsync();
        rig.Service.Gateway.Status.OtherClients.ShouldBeNull("пауза, начатая во время чтения процессов, тоже не дает находку");
        await rig.Service.Gateway.ResumeAsync(Ct);
        clock.Tick();
        await clock.NextTimerAsync();
        rig.Service.Gateway.Status.OtherClients.ShouldNotBeNull().Items.ShouldContain(c => c.Sign == OtherClientSign.Process,
            "после паузы тот же наблюдатель снова замечает другое ядро");
    }

    [Fact]
    public async Task Frequent_client_check_does_not_keep_a_timer_after_a_terminal_core_error()
    {
        using var clock = new OtherClientsClock();
        await using var rig = await OtherClientsRig.CreateAsync(clock, () => []);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        request.Connection.Reconnect = V1.ReconnectPolicy.Never;
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);
        await clock.NextTimerAsync();
        var scheduled = clock.Scheduled;

        Process.GetProcessById(CoreProcessId(rig.Service)).Kill();
        var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error, rig.Service.Journal))[^1];
        error.Error.Code.ShouldBe(ErrorCodes.CoreFailed);
        clock.Tick();
        clock.Tick();

        clock.Scheduled.ShouldBe(scheduled, "после конечного состояния прежний наблюдатель не ставит новый таймер");
    }

    [Fact]
    public async Task Frequent_client_check_ends_on_reconnect_and_a_new_one_detects_clients_after_recovery()
    {
        using var clock = new OtherClientsClock();
        var network = new FakeNetwork();
        IReadOnlyList<RunningProcess> running = [];
        await using var rig = await OtherClientsRig.CreateAsync(clock, () => running, network);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);
        await clock.NextTimerAsync();
        var scheduled = clock.Scheduled;
        var first = CoreProcessId(rig.Service);

        Process.GetProcessById(first).Kill();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting, rig.Service.Journal);
        clock.Tick();
        clock.Scheduled.ShouldBe(scheduled, "на время переподключения прежний наблюдатель завершается");
        network.Raise();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);
        await clock.NextTimerAsync();
        CoreProcessId(rig.Service).ShouldNotBe(first, "соединение восстановлено новым настоящим ядром");
        running = [new RunningProcess(7, "xray", "/opt/other-client/xray")];
        clock.Tick();
        await clock.NextTimerAsync();

        rig.Service.Gateway.Status.OtherClients.ShouldNotBeNull().Items.ShouldContain(c => c.Sign == OtherClientSign.Process,
            "после восстановления AfterConnected запускает новую частую проверку");
    }

    /// <summary>Только частый поиск управляется тестом; готовность ядра, IPC и пробы работают по настоящим часам.</summary>
    private sealed class OtherClientsClock : TimeProvider, IDisposable
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);
        private readonly FakeTimeProvider _checks = new();
        private readonly SemaphoreSlim _scheduled = new(0);
        private int _count;

        public int Scheduled => Volatile.Read(ref _count);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Interval || period != Timeout.InfiniteTimeSpan)
            {
                return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            }

            var timer = _checks.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _count);
            _scheduled.Release();
            return timer;
        }

        public void Tick() => _checks.Advance(Interval);

        public void Dispose() => _scheduled.Dispose();

        public async Task NextTimerAsync() => (await _scheduled.WaitAsync(TimeSpan.FromSeconds(10), Ct))
            .ShouldBeTrue("наблюдатель поставил следующий таймер поиска");
    }

    /// <summary>Своя служба, настоящий Xray и свой отвечающий сайт; пробы и повторы раз в час, поиск — по часам теста.</summary>
    private sealed class OtherClientsRig(LocalSite site, CoreProcess server, ServerDefinition definition, RunningService service) : IAsyncDisposable
    {
        public ServerDefinition Definition => definition;
        public RunningService Service => service;

        public static async Task<OtherClientsRig> CreateAsync(OtherClientsClock clock, Func<IReadOnlyList<RunningProcess>> processes, FakeNetwork? network = null)
        {
            Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
            var site = new LocalSite("частая проверка");
            CoreProcess? server = null;
            try
            {
                var serverPort = XrayKit.FreePort();
                var (definition, config) = XrayKit.Pair("vless", serverPort);
                config["outbounds"] = new JsonArray(new JsonObject
                {
                    ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" },
                });
                server = await XrayKit.Start(config, serverPort);
                var hour = TimeSpan.FromHours(1);
                var service = await RunningService.StartAsync(site.Url, time: clock, options: o => o with
                {
                    Reconnect = new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour),
                    Network = network,
                    Processes = processes,
                    OtherClientsWatch = OtherClientsClock.Interval,
                });
                return new OtherClientsRig(site, server, definition, service);
            }
            catch
            {
                server?.Dispose();
                site.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await service.DisposeAsync();
            server.Dispose();
            site.Dispose();
        }
    }
}
