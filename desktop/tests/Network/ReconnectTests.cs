using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Переподключение только к выбранному серверу — с настоящими ядрами Xray в режиме прокси: сервер
/// перестал отвечать или ядро вышло — «Переподключение» к тому же серверу; вернулся — «Защищено»;
/// попытки кончились по политике — «Ошибка». Проверочный адрес — свой сайт: из машины ничего не уходит.
/// </summary>
public sealed partial class ReconnectTests
{
    /// <summary>Быстрые пробы и паузы, предел «До 5 минут» — 3 с.</summary>
    internal static readonly ReconnectTiming Fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(3));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Server_that_stops_answering_is_retried_until_it_comes_back()
    {
        await using var rig = await ProxyRig.CreateAsync();
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        rig.StopServer();

        var lost = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting && s.Attempt >= 2);
        lost.ShouldAllBe(s => s.ServerId == "srv-1", "другой сервер сам не выбирается");
        lost.First(s => s.State == V1.ConnectionState.Reconnecting).Attempt.ShouldBe(1);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сервер не отвечает — переподключение к тому же серверу");
        (await rig.Service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.Core.ToContractId()).State.ShouldBe(V1.CheckState.Waiting, "ядро поднимается заново");

        await rig.StartServerAsync();

        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected))[^1].Attempt.ShouldBe(0);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.StartsWith("Связь с сервером восстановлена", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proxy_gives_up_by_the_policy_with_an_error()
    {
        await using var rig = await ProxyRig.CreateAsync();
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var lostAt = Stopwatch.StartNew();

        rig.StopServer();

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error);
        states[^1].Error.Code.ShouldBe(ErrorCodes.ServerNotResponding, "в режиме прокси блока нет — «Ошибка», а не «Блокировка»");
        states.Select(s => s.State).ShouldContain(V1.ConnectionState.Reconnecting);
        lostAt.Elapsed.ShouldBeGreaterThanOrEqualTo(Fast.GiveUpAfter, "«До 5 минут» — пробует до предела, потом сдается");
    }

    [Fact]
    public async Task Core_that_exits_is_started_again_and_never_policy_stops_at_once()
    {
        await using var rig = await ProxyRig.CreateAsync();
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var first = CoreProcessId(rig.Service);

        Process.GetProcessById(first).Kill();

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        states.Select(s => s.State).ShouldContain(V1.ConnectionState.Reconnecting, "ядро вышло — переподключение, а не «Ошибка»");
        CoreProcessId(rig.Service).ShouldNotBe(first, "к тому же серверу — новым ядром");

        var never = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        never.Connection.Reconnect = V1.ReconnectPolicy.Never;
        await rig.Service.Client.ConnectAsync(never, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        Process.GetProcessById(CoreProcessId(rig.Service)).Kill();

        var error = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Reconnecting))[^1];
        (error.State, error.Error.Code).ShouldBe((V1.ConnectionState.Error, ErrorCodes.CoreFailed), "«Не повторять» — сразу «Ошибка»");
    }

    [Fact]
    public async Task Network_change_checks_the_server_at_once_and_skips_the_rest_of_the_pause()
    {
        // Пробы раз в час и паузы по часу: успеть может только смена сети. Другой VPN режиму прокси не помеха.
        var hour = TimeSpan.FromHours(1);
        var network = new FakeNetwork();
        var routes = new FakeRoutes { Routes = [new DefaultRoute("wg0", IsTunnel: true)] };
        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour), network, routes);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        rig.StopServer();
        network.Raise();

        var lost = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting))[^1];
        lost.ServerId.ShouldBe("srv-1");
        lost.NetworkChangedAt.ShouldNotBeNull("окно видит, что попытки идут сразу после смены сети");
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сеть сменилась, сервер не отвечает — переподключение к тому же серверу");

        await rig.StartServerAsync();
        network.Raise();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Сеть машины сменилась — попытка переподключения сразу");

        network.Raise();
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        (await rig.Service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State
            .ShouldBe(V1.ConnectionState.Connected, "сервер отвечает — смена сети ничего не ломает");
    }

    [Fact]
    public async Task Network_change_during_an_attempt_brings_the_next_attempt_at_once()
    {
        // Пауза между попытками — час: следующая попытка без часа ожидания бывает, только если смену сети во время попытки
        // служба не потеряла. Ответы пробы дает тест — по одному на каждый вызов.
        var hour = TimeSpan.FromHours(1);
        var network = new FakeNetwork();
        var probes = System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource<bool>>();
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            probes.Writer.TryWrite(answer);
            return answer.Task;
        }

        async Task<TaskCompletionSource<bool>> NextProbe() => await probes.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour), network, probe: Probe);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        (await NextProbe()).SetResult(true);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        network.Raise();
        (await NextProbe()).SetResult(false);
        (await NextProbe()).SetResult(false);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting);

        network.Raise();
        var first = await NextProbe();
        network.Raise();
        first.SetResult(false);

        // И во время удачной попытки сеть сменилась — этот сигнал следующему переподключению первую паузу не сокращает.
        var second = await NextProbe();
        network.Raise();
        second.SetResult(true);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Переподключение, попытка 2");

        network.Raise();
        (await NextProbe()).SetResult(false);
        (await NextProbe()).SetResult(false);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        probes.Reader.TryRead(out _).ShouldBeFalse("первая попытка нового переподключения ждет свою паузу");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pause_while_reconnecting_gives_the_system_proxy_back_and_connects_again_after(bool systemProxy)
    {
        // Отель в режиме прокси: системный прокси ведет браузер в ядро, а сервер за порталом не отвечает.
        var proxy = new FakeSystemProxy();
        var network = new FakeNetwork();
        var backup = Path.Combine(Path.GetTempPath(), "pg-proxy-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            await using var rig = await ProxyRig.CreateAsync(network: network, systemProxy: (proxy, backup));
            using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            var port = XrayKit.FreePort();
            await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, port, systemProxy), cancellationToken: Ct);
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
            rig.StopServer();
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting && s.Attempt >= 2);

            var hour = new V1.CaptivePauseRequest { Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromHours(1)) };
            var reply = await rig.Service.Client.PauseForCaptivePortalAsync(hour, cancellationToken: Ct);
            var attempts = Attempts(rig.Service);
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            if (!systemProxy)
            {
                reply.Until.ShouldBeNull("без системного прокси паузе нечего менять — браузер и так идет напрямую");
                Attempts(rig.Service).ShouldBeGreaterThan(attempts, "попытки идут дальше");
                return;
            }

            reply.Until.ShouldNotBeNull("в «Переподключении» пауза есть");
            var paused = (await rig.Service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
            (paused.State, paused.IsPaused, proxy.Port).ShouldBe((ConnectionState.Reconnecting, true, null), "страница входа в сеть — напрямую");
            Attempts(rig.Service).ShouldBe(attempts, "на паузе попыток нет");

            // Вошли в сеть отеля — сеть машины сменилась (новый адрес): попытки не подгоняются, пауза идет.
            network.Raise();
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Attempts(rig.Service).ShouldBe(attempts, "и смена сети на паузе попытку не начинает");
            rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Message == "Сеть машины сменилась — попытка переподключения сразу");

            await rig.StartServerAsync();
            await rig.Service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);

            var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error);
            states.Select(s => s.State).ShouldContain(V1.ConnectionState.Connecting, "после паузы — ядро заново, через «Подключение»");
            states[^1].State.ShouldBe(V1.ConnectionState.Connected, states[^1].Error?.Message);
            (states[^1].CaptivePauseUntil, proxy.Port).ShouldBe((null, port), "системный прокси — снова на ядре");
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Theory]
    [InlineData(V1.ReconnectPolicy.Backoff60S)]
    [InlineData(V1.ReconnectPolicy.Never)]
    public async Task Server_that_does_not_answer_at_connect_is_not_shown_as_protected(V1.ReconnectPolicy policy)
    {
        var proxy = new FakeSystemProxy();
        var backup = Path.Combine(Path.GetTempPath(), "pg-proxy-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            await using var rig = await ProxyRig.CreateAsync(systemProxy: (proxy, backup));
            using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            rig.StopServer();
            var port = XrayKit.FreePort();
            var request = ServiceIpcTests.Request(rig.Definition, port, systemProxy: true);
            request.Connection.Reconnect = policy;

            await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);

            // Ядро открыло вход, а сервер не ответил: «Защищено» не было, браузеры на мертвое ядро не направлены.
            var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Reconnecting or V1.ConnectionState.Error);
            states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected, "сервер не ответил — и «Защищено» не показано ни разу");
            proxy.Port.ShouldBeNull("системный прокси не ставится на ядро без связи с сервером");
            if (policy == V1.ReconnectPolicy.Never)
            {
                (states[^1].State, states[^1].Error.Code).ShouldBe((V1.ConnectionState.Error, ErrorCodes.ServerNotResponding));
                return;
            }

            await rig.StartServerAsync();

            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
            proxy.Port.ShouldBe(port, "связь вернулась — системный прокси на ядре");
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public async Task Waking_up_checks_the_server_at_once()
    {
        // Пробы раз в час: после сна сервер проверяется сразу, а не к следующей пробе.
        var hour = TimeSpan.FromHours(1);
        var network = new FakeNetwork();
        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour), network);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        rig.StopServer();
        network.Wake(TimeSpan.FromMinutes(30));

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Машина проснулась — проверка связи с сервером");
    }

    [Fact]
    public void Sleep_is_reported_once_when_the_sleep_clock_grows_and_the_network_is_compared()
    {
        var time = new FakeTimeProvider();
        var (network, clock) = ("wlan0=192.0.2.10", new FakeSuspendClock());
        using var events = new SystemNetworkEvents(time, () => network, clock);
        var (woke, changes) = (new List<TimeSpan>(), 0);
        events.Resumed += (_, slept) => woke.Add(slept);
        events.Changed += (_, _) => changes++;

        clock.Suspended = TimeSpan.FromSeconds(1);
        time.Advance(SystemNetworkEvents.SleepCheck);

        woke.ShouldBeEmpty("доля секунды на часах сна — шум, а не сон");

        // Проспала 10 минут, проснулась в другой сети.
        clock.Suspended += TimeSpan.FromMinutes(10);
        network = "wlan0=198.51.100.7";
        time.Advance(SystemNetworkEvents.SleepCheck);
        time.Advance(SystemNetworkEvents.Quiet);

        woke.ShouldBe([TimeSpan.FromMinutes(10)], "сколько спала — с прошлой сверки");
        changes.ShouldBe(1, "после сна сеть сверяется — и другая сеть видна без события ОС");

        time.Advance(SystemNetworkEvents.SleepCheck);

        woke.Count.ShouldBe(1, "один сон — одно пробуждение");
    }

    [Fact]
    public void Sleep_clock_of_this_os_reads()
    {
        Assert.SkipUnless(ServiceHost.SuspendClock() is not null, "часов сна для этой ОС нет");
        var clock = ServiceHost.SuspendClock()!;

        var (first, second) = (clock.Suspended, clock.Suspended);

        first.ShouldBeGreaterThanOrEqualTo(-TimeSpan.FromMilliseconds(50), "сон с загрузки — не меньше нуля (с погрешностью двух чтений)");
        (second - first).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1), "без сна часы сна стоят");
    }

    [Fact]
    public void Network_fingerprint_ignores_the_own_tunnel_the_loopback_and_the_order()
    {
        (string, bool, IEnumerable<IPAddress>) Adapter(string name, params string[] addresses) =>
            (name, false, addresses.Select(IPAddress.Parse));

        var before = SystemNetworkEvents.Fingerprint([Adapter("wlan0", "192.0.2.10", "2001:db8::10"), Adapter("eth0")], "pgtun0");

        SystemNetworkEvents.Fingerprint(
            [Adapter("eth0"), Adapter("wlan0", "2001:db8::10", "192.0.2.10"), Adapter("pgtun0", "172.19.0.1"), ("lo", true, [IPAddress.Loopback])], "pgtun0")
            .ShouldBe(before, "свой туннель и петля — не смена сети машины, порядок — тоже");
        SystemNetworkEvents.Fingerprint([Adapter("wlan0", "198.51.100.7"), Adapter("eth0")], "pgtun0")
            .ShouldNotBe(before, "новый адрес — другая сеть");
        SystemNetworkEvents.Fingerprint([Adapter("wlan0", "192.0.2.10", "2001:db8::10")], "pgtun0")
            .ShouldNotBe(before, "адаптер пропал — другая сеть");
    }

    [Fact]
    public void Network_change_is_reported_once_after_the_system_falls_quiet_and_only_if_the_network_differs()
    {
        var time = new FakeTimeProvider();
        var network = "wlan0=192.0.2.10";
        using var events = new SystemNetworkEvents(time, () => network);
        var changes = 0;
        events.Changed += (_, _) => changes++;

        events.Notify();
        time.Advance(SystemNetworkEvents.Quiet);

        changes.ShouldBe(0, "ОС сообщила о своем туннеле — сеть машины та же");

        network = "wlan0=198.51.100.7";
        events.Notify();
        time.Advance(SystemNetworkEvents.Quiet / 2);
        events.Notify();
        time.Advance(SystemNetworkEvents.Quiet / 2);

        changes.ShouldBe(0, "ОС еще сообщает о перемене — ждать затишья");

        time.Advance(SystemNetworkEvents.Quiet / 2);

        changes.ShouldBe(1, "одна перемена — одно событие");

        events.Notify();
        time.Advance(SystemNetworkEvents.Quiet);

        changes.ShouldBe(1, "та же новая сеть — уже не перемена");
    }

    /// <summary>Сколько попыток переподключения служба начала.</summary>
    private static int Attempts(RunningService service) =>
        service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message.StartsWith("Переподключение, попытка", StringComparison.Ordinal));

    /// <summary>Процесс ядра, которое служба запустила последним, — из ее журнала.</summary>
    private static int CoreProcessId(RunningService service)
    {
        const string prefix = "Ядро Xray запущено, процесс ";
        var line = service.Journal.Tail(ServiceJournal.Capacity).Last(e => e.Message.StartsWith(prefix, StringComparison.Ordinal)).Message;
        return int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
    }

    /// <summary>Свой сервер Xray с выходом на свой сайт и служба с быстрыми пробами; проверочный адрес — тот же сайт.</summary>
    private sealed class ProxyRig : IAsyncDisposable
    {
        private readonly LocalSite _site;
        private readonly JsonObject _serverConfig;
        private readonly int _serverPort;
        private CoreProcess? _server;

        private ProxyRig(LocalSite site, JsonObject serverConfig, int serverPort, Core.Servers.ServerDefinition definition, CoreProcess server,
            RunningService service)
        {
            (_site, _serverConfig, _serverPort, Definition, _server, Service) = (site, serverConfig, serverPort, definition, server, service);
        }

        public Core.Servers.ServerDefinition Definition { get; }

        public RunningService Service { get; }

        public static async Task<ProxyRig> CreateAsync(ReconnectTiming? timing = null, INetworkEvents? network = null, IRouteTable? routes = null,
            (ISystemProxy Proxy, string Backup)? systemProxy = null, Func<ServerProbe, CancellationToken, Task<bool>>? probe = null)
        {
            Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
            var site = new LocalSite("проверка");
            var serverPort = XrayKit.FreePort();
            var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
            serverConfig["outbounds"] = new JsonArray(
                new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
            var server = await XrayKit.Start(serverConfig, serverPort);
            var service = await RunningService.StartAsync(site.Url, systemProxy: systemProxy,
                options: o => o with { Reconnect = timing ?? Fast, Network = network, Routes = routes, Probe = probe ?? o.Probe });
            return new ProxyRig(site, serverConfig, serverPort, definition, server, service);
        }

        public void StopServer()
        {
            _server?.Dispose();
            _server = null;
        }

        public async Task StartServerAsync() => _server = await XrayKit.Start(_serverConfig, _serverPort);

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _server?.Dispose();
            _site.Dispose();
        }
    }

    private sealed class FakeSuspendClock : ISuspendClock
    {
        public TimeSpan Suspended { get; set; }
    }
}
