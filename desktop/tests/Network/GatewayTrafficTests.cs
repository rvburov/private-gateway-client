using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Пинг и скорость главного экрана — от службы. Она их не заполняла, и окно всегда показывало «—» и «0,0» (у пользователя
/// на Windows 2026-10-31). Ядра и адаптер здесь — подделки: замер пинга и счетчики адаптера туннеля.
/// </summary>
public sealed class GatewayTrafficTests
{
    [Fact]
    public async Task Vpn_reports_ping_through_the_server_and_speed_from_the_tunnel_adapter()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counters = (Received: 1_000_000L, Sent: 500_000L);
        var pings = 0;
        await using var gateway = Connected(time, ConnectionMode.Vpn, () => counters, () => ++pings * 10);

        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.ShouldBe(new TrafficStats(10, 0, 0), "первый замер скорости — точка отсчета, пинг — сразу");

        time.Advance(TimeSpan.FromSeconds(2));
        counters = (counters.Received + 2_500_000, counters.Sent + 250_000);
        await gateway.SampleTrafficAsync();

        // 2,5 МБ за 2 с — 10 Мбит/с загрузки; 0,25 МБ — 1 Мбит/с отдачи. Пинг — раз в 15 с, не на каждом замере.
        gateway.Status.Traffic.ShouldBe(new TrafficStats(10, 10, 1));
        pings.ShouldBe(1);

        time.Advance(Gateway.PingEvery);
        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.LatencyMs.ShouldBe(20);
    }

    [Fact]
    public async Task Proxy_has_a_ping_but_no_adapter_to_count_and_off_clears_the_numbers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counted = false;
        await using var gateway = Connected(time, ConnectionMode.Proxy, () =>
        {
            counted = true;
            return (1, 1);
        }, () => 35);

        await gateway.SampleTrafficAsync();

        gateway.Status.Traffic.ShouldBe(new TrafficStats(35, 0, 0));
        counted.ShouldBeFalse("в режиме прокси адаптер туннеля — не его трафик");

        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.ShouldBe(TrafficStats.Empty, "выключено — ни пинга, ни скорости");
    }

    [Fact]
    public async Task Speed_comes_from_the_core_counters_in_both_modes_and_falls_back_to_the_adapter()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        (long, long)? core = (1_000_000L, 100_000L);
        var adapter = (Received: 50_000_000L, Sent: 5_000_000L);
        var ports = new List<int>();
        await using var proxy = Connected(time, ConnectionMode.Proxy, () => adapter, () => 30, port =>
        {
            ports.Add(port);
            return core;
        });

        await proxy.SampleTrafficAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        core = (1_000_000L + 2_500_000, 100_000L + 250_000);
        await proxy.SampleTrafficAsync();

        // Режим прокси: адаптера туннеля нет, а ядро считает трафик программ — 2,5 МБ за 2 с — 10 Мбит/с.
        proxy.Status.Traffic.ShouldBe(new TrafficStats(30, 10, 1));
        ports.ShouldAllBe(port => port == 10810, "счетчики — того ядра, что этого подключения");

        time.Advance(TimeSpan.FromSeconds(2));
        core = null;
        await proxy.SampleTrafficAsync();
        proxy.Status.Traffic.ShouldBe(new TrafficStats(30, 0, 0), "ядро не ответило — в режиме прокси скорости нет");

        core = (5_000_000L, 500_000L);
        await using var vpn = Connected(time, ConnectionMode.Vpn, () => adapter, () => 30, _ => core);
        await vpn.SampleTrafficAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        (core, adapter) = ((5_000_000L + 5_000_000, 500_000L + 500_000), (adapter.Received + 250_000, adapter.Sent + 25_000));
        await vpn.SampleTrafficAsync();
        vpn.Status.Traffic.ShouldBe(new TrafficStats(30, 20, 2),
            "режим VPN: ядро считает и прямой локальный вход HTTP/SOCKS, которого адаптер туннеля не видит");

        time.Advance(TimeSpan.FromSeconds(2));
        core = null;
        await vpn.SampleTrafficAsync();
        vpn.Status.Traffic.ShouldBe(new TrafficStats(30, 0, 0), "сменился источник — числа разных источников не сравниваются");
        time.Advance(TimeSpan.FromSeconds(2));
        adapter = (adapter.Received + 2_500_000, adapter.Sent + 250_000);
        await vpn.SampleTrafficAsync();
        vpn.Status.Traffic.ShouldBe(new TrafficStats(30, 10, 1), "ядро молчит — скорость по адаптеру туннеля, как раньше");
    }

    [Fact]
    public async Task Real_core_counts_the_bytes_of_its_inbound_but_not_of_the_probe()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var page = new string('x', 300_000);
        using var site = new LocalSite(page);
        var (definition, _) = XrayKit.Pair("vless", XrayKit.FreePort());
        var proxyPort = XrayKit.FreePort();
        var link = new ProbeLink(XrayKit.FreePort(), "probe-user", "probe-pass", XrayKit.FreePort());
        // Локальная сеть открыта: сайт теста на 127.0.0.1 — напрямую из ядра, сервер не нужен; счетчик входа тот же.
        var request = new CoreRequest(definition, ConnectionMode.Proxy, ProtectionSettings.Default with { AllowLan = true },
            ConnectionSettings.Default with { ProxyPort = proxyPort }, RoutingSettings.Default);
        using var client = await XrayKit.Start(XrayConfigBuilder.Build(request, probe: link).Config.ShouldNotBeNull(), proxyPort);
        using var stats = new XrayStatsClient(link.StatsPort);
        var before = await Totals(stats);

        (await Get(site.Url, proxyPort)).Length.ShouldBe(page.Length);
        var after = await Totals(stats);

        (after.Received - before.Received).ShouldBeGreaterThanOrEqualTo(page.Length, "ядро посчитало все, что отдало программе");
        (after.Sent - before.Sent).ShouldBeGreaterThan(0, "и запрос программы");
        XrayStatsClient.Sum([("inbound>>>" + XrayConfigBuilder.ProbeTag + ">>>traffic>>>downlink", 1_000), ("outbound>>>proxy>>>traffic>>>downlink", 1_000),
            ("inbound>>>" + XrayConfigBuilder.LocalProxyTag + ">>>traffic>>>uplink", 7), ("broken", 5)]).ShouldBe((0L, 7L),
            "вход проверки службы, выходы и непонятные имена — не в счет");
    }

    [Fact]
    public async Task Ping_goes_through_the_core_and_the_server_on_a_warmed_connection()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        await using var service = await RunningService.StartAsync(site.Url);
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: TestContext.Current.CancellationToken);
        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, XrayKit.FreePort()), cancellationToken: TestContext.Current.CancellationToken);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var before = site.Requests;

        await service.Gateway.SampleTrafficAsync();

        service.Gateway.Status.Traffic.LatencyMs.ShouldNotBeNull("проверочный адрес ответил через ядро и сервер");
        (site.Requests - before).ShouldBe(2, "первый замер — прогрев соединения и сам замер: рукопожатия в пинг не входят");
    }

    /// <summary>Счетчики ядра: подождать, пока его API ответит, — оно открывается вместе с входами.</summary>
    private static async Task<(long Received, long Sent)> Totals(XrayStatsClient stats)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await stats.TotalsAsync(TestContext.Current.CancellationToken) is { } totals)
            {
                return totals;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new ShouldAssertException("ядро не ответило на запрос счетчиков за 5 с");
    }

    private static async Task<string> Get(Uri url, int proxyPort)
    {
        using var handler = new SocketsHttpHandler { Proxy = new AlwaysProxy(new Uri($"socks5://127.0.0.1:{proxyPort}")), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return await http.GetStringAsync(url, TestContext.Current.CancellationToken);
    }

    /// <summary>Прокси для всех адресов: WebProxy сам пропускает localhost мимо прокси, а тесту нужен именно он.</summary>
    private sealed class AlwaysProxy(Uri proxy) : System.Net.IWebProxy
    {
        public System.Net.ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }

    private static Gateway Connected(FakeTimeProvider time, ConnectionMode mode, Func<(long, long)?> counters, Func<int?> ping,
        Func<int, (long, long)?>? core = null)
    {
        var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"), BlockWatch: TimeSpan.FromHours(1),
            Delay: (_, _) => Task.FromResult(ping()), TunnelCounters: counters, TrafficWatch: Timeout.InfiniteTimeSpan,
            CoreCounters: core is null ? null : port => Task.FromResult<(long Received, long Sent)?>(core(port))), time, new ServiceJournal(time));
        // Подключенный снимок задается напрямую: настоящий запуск ядер здесь не нужен.
        var status = ConnectionStatus.Initial(time.GetUtcNow()) with { Mode = mode };
        status = status.MoveTo(ConnectionState.Connecting, time.GetUtcNow()).MoveTo(ConnectionState.Connected, time.GetUtcNow());
        typeof(Gateway).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, status);
        typeof(Gateway).GetField("_probe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(gateway, new ProbeLink(10809, "user", "password", core is null ? 0 : 10810));
        return gateway;
    }
}
