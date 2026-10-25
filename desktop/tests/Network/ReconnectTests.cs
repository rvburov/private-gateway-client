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
public sealed class ReconnectTests
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

        public static async Task<ProxyRig> CreateAsync(ReconnectTiming? timing = null, INetworkEvents? network = null, IRouteTable? routes = null)
        {
            Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
            var site = new LocalSite("проверка");
            var serverPort = XrayKit.FreePort();
            var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
            serverConfig["outbounds"] = new JsonArray(
                new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
            var server = await XrayKit.Start(serverConfig, serverPort);
            var service = await RunningService.StartAsync(site.Url, options: o => o with { Reconnect = timing ?? Fast, Network = network, Routes = routes });
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
}
