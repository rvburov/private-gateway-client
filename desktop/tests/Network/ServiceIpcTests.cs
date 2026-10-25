using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using PrivateGateway.Client.Service.Ipc;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Служба и интерфейс по настоящему каналу IPC (Unix-сокет, на Windows — named pipe): сведения,
/// подключение в режиме прокси с настоящим ядром, отказы с кодами ошибок, задержка, журнал без
/// ключей. Служба поднимается в процессе теста без прав администратора.
/// </summary>
public sealed class ServiceIpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Service_tells_its_versions_and_cores()
    {
        await using var service = await RunningService.StartAsync();

        var info = await service.Client.GetServiceInfoAsync(new V1.GetServiceInfoRequest(), cancellationToken: Ct);

        (info.ApiVersion, info.ServiceVersion).ShouldBe((ContractMapping.ApiVersion, ServiceVersion.Current));
        info.Cores.Select(c => (c.Name, c.Available)).ShouldBe([("xray", XrayKit.Xray is not null), ("sing-box", XrayKit.SingBox is not null)]);
        if (XrayKit.SingBox is not null)
        {
            info.Cores[1].Version.ShouldBe(File.ReadAllText(Path.Combine(XrayKit.Root, "shared", "cores", "sing-box.version")).Trim().TrimStart('v'));
        }
    }

    [Fact]
    public async Task Proxy_mode_starts_the_core_and_stops_it_on_disconnect()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var service = await RunningService.StartAsync();
        using var site = new LocalSite("через службу");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        (await ReadUntilAsync(watch, _ => true)).ShouldHaveSingleItem().State.ShouldBe(V1.ConnectionState.Off, "первым приходит текущее состояние");

        var reply = await service.Client.ConnectAsync(Request(definition, proxyPort), cancellationToken: Ct);

        reply.Accepted.ShouldBeTrue(reply.Error?.Message);
        var states = await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        states.Select(s => s.State).ShouldBe([V1.ConnectionState.Connecting, V1.ConnectionState.Connected]);
        var connected = states[^1].ToCore();
        (connected.Mode, connected.ServerId, connected.Protection.AllTraffic).ShouldBe((ConnectionMode.Proxy, "srv-1", CheckState.Warning));
        // Локальная сеть разрешена в запросе — сайт на 127.0.0.1 отвечает через прокси службы.
        (await Get(site.Url, proxyPort)).ShouldBe("через службу");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        (await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off)).Select(s => s.State)
            .ShouldBe([V1.ConnectionState.Off], "остановка ядра при отключении — не ошибка");
        await Should.ThrowAsync<Exception>(() => Get(site.Url, proxyPort));
        var journal = string.Join('\n', service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message));
        journal.ShouldNotContain(XrayKit.Uuid, Case.Insensitive, "ключ сервера в журнал не попадает");
    }

    [Fact]
    public async Task Switching_servers_goes_through_connecting_without_an_error()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var service = await RunningService.StartAsync();
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(Request(definition, proxyPort), cancellationToken: Ct);
        await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        var second = Request(definition with { Name = "Второй" }, proxyPort);
        second.Server.Id = "srv-2";
        (await service.Client.ConnectAsync(second, cancellationToken: Ct)).Accepted.ShouldBeTrue();

        var states = await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        states.Select(s => (s.State, s.ServerId)).ShouldBe([(V1.ConnectionState.Connecting, "srv-2"), (V1.ConnectionState.Connected, "srv-2")],
            "выход старого ядра при смене сервера — не ошибка нового подключения");
    }

    [Fact]
    public async Task Status_watch_ends_when_the_window_stops_listening()
    {
        await using var gateway = new Gateway(new GatewayOptions(null, CanVpn: false, LatencyProbe.DefaultTarget), TimeProvider.System,
            new ServiceJournal(TimeProvider.System));
        using var stop = new CancellationTokenSource();

        var (current, updates) = gateway.Watch(stop.Token);
        stop.Cancel();

        current.State.ShouldBe(ConnectionState.Off);
        await updates.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public void Core_output_hides_addresses_and_service_lines_keep_them()
    {
        var journal = new ServiceJournal(TimeProvider.System);

        journal.Add(JournalLevel.Info, "xray", "[Info] proxy/vless/outbound: tunneling request to tcp:example.com:443 via 203.0.113.1:443");
        journal.Add(JournalLevel.Info, "sing-box", "INFO inbound/tun[tun-in]: inbound connection to 198.51.100.7:80");
        journal.Add(JournalLevel.Warning, "service", "Работает другой VPN (pgother0): маршрут через 192.0.2.1");

        journal.Tail(3).Select(e => e.Message).ShouldBe([
            "[Info] proxy/vless/outbound: tunneling request to tcp:•••:443 via •••:443",
            "INFO inbound/tun[tun-in]: inbound connection to •••:80",
            "Работает другой VPN (pgother0): маршрут через 192.0.2.1",
        ], "адреса сайтов из вывода ядер в журнал не попадают; свои строки служба пишет сама и знает, что в них");
    }

    [Fact]
    public void Journal_hides_connection_keys_in_any_line()
    {
        var journal = new ServiceJournal(TimeProvider.System);
        journal.Hide(["pw-very-secret-1", null, "abc"]);

        journal.Add(JournalLevel.Info, "xray", "auth failed for pw-very-secret-1 at 203.0.113.1");
        journal.Add(JournalLevel.Info, "xray", "abc — короткое не прячется");

        journal.Tail(10).Select(e => e.Message).ShouldBe(["auth failed for *** at •••", "abc — короткое не прячется"], "адрес в выводе ядра тоже скрыт");
        for (var i = 0; i < ServiceJournal.Capacity + 5; i++)
        {
            journal.Add(JournalLevel.Debug, "service", "строка " + i);
        }

        journal.Tail(1000).Count.ShouldBe(ServiceJournal.Capacity);
        journal.Tail(1).ShouldHaveSingleItem().Message.ShouldBe("строка " + (ServiceJournal.Capacity + 4));
    }

    [Fact]
    public async Task Core_that_cannot_open_the_port_is_an_error_with_a_code()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var service = await RunningService.StartAsync();
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var (definition, _) = XrayKit.Pair("vless", XrayKit.FreePort());
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(Request(definition, ((IPEndPoint)busy.LocalEndpoint).Port), cancellationToken: Ct);

        var states = await ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Error or V1.ConnectionState.Connected);
        states[^1].State.ShouldBe(V1.ConnectionState.Error, "чужой вход на порту — не готовность ядра");
        states[^1].Error.Code.ShouldBe(ErrorCodes.CoreFailed);
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.Contains("занят другой программой", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("vpn", ErrorCodes.AdminRequired)]
    [InlineData("hysteria2", ErrorCodes.UnsupportedServer)]
    [InlineData("unspecified", ErrorCodes.BadRequest)]
    public async Task Refused_request_says_why_and_is_an_error_for_everyone(string kind, string code)
    {
        await using var service = await RunningService.StartAsync();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await ReadUntilAsync(watch, _ => true);
        var (definition, _) = XrayKit.Pair("vless", 443);
        var request = Request(kind == "hysteria2"
            ? XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password)) with { Transport = TransportOptions.Quic }
            : definition, 10808);
        request.Mode = kind switch
        {
            "vpn" => V1.Mode.Vpn,
            "unspecified" => V1.Mode.Unspecified,
            _ => request.Mode,
        };

        var reply = await service.Client.ConnectAsync(request, cancellationToken: Ct);

        (reply.Accepted, reply.Error.Code).ShouldBe((false, code));
        var states = await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error);
        states.Select(s => (s.State, s.ServerId, s.Error?.Code))
            .ShouldBe([(V1.ConnectionState.Connecting, "srv-1", null), (V1.ConnectionState.Error, "srv-1", code)],
                "отказ видят все окна, а не только то, что просило");
        states[^1].ToCore().Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off), "подключения нет — проверки не обещают блок");
    }

    [Fact]
    public async Task Refused_switch_does_not_leave_the_previous_server_running()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var service = await RunningService.StartAsync();
        using var site = new LocalSite("старый сервер");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(Request(definition, proxyPort), cancellationToken: Ct);
        await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        var hysteria = Request(XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password))
            with { Transport = TransportOptions.Quic }, proxyPort);
        hysteria.Server.Id = "srv-2";
        (await service.Client.ConnectAsync(hysteria, cancellationToken: Ct)).Accepted.ShouldBeFalse();

        var error = (await ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error))[^1];
        (error.ServerId, error.Error.Code).ShouldBe(("srv-2", ErrorCodes.UnsupportedServer));
        await Should.ThrowAsync<Exception>(() => Get(site.Url, proxyPort), "прежний прокси остановлен: защита не та, что просили");
    }

    [Fact]
    public async Task Latency_is_measured_through_each_server_by_the_service()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url);
        var serverPort = XrayKit.FreePort();
        var (alive, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new V1.MeasureLatencyRequest();
        request.Targets.Add(new V1.LatencyTarget { Id = "alive", Server = alive.ToContract("alive") });
        request.Targets.Add(new V1.LatencyTarget { Id = "broken" });

        var reply = await service.Client.MeasureLatencyAsync(request, cancellationToken: Ct);

        var results = reply.Results.ToDictionary(r => r.Id, r => r.ToCore());
        results["alive"].Milliseconds.ShouldNotBeNull();
        results["broken"].ShouldBe(LatencyResult.Failed(LatencyFailure.Unsupported), "цель без сервера — не поддерживается, а не ошибка службы");
        site.Requests.ShouldBe(1);
    }

    [Theory]
    [InlineData(0u, 1000u, null, true)]
    [InlineData(1000u, 1000u, null, true)]
    [InlineData(1001u, 0u, 1001u, true)]
    [InlineData(1002u, 0u, 1001u, false)]
    [InlineData(1002u, 1000u, null, false)]
    [InlineData(null, 1000u, null, false)]
    public void Only_root_the_service_itself_or_the_owner_may_connect(uint? peer, uint self, uint? owner, bool allowed)
    {
        IpcServer.IsAllowed(peer, self, owner).ShouldBe(allowed);
    }

    [Fact]
    public async Task Peer_uid_comes_from_the_os()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "на Windows доступ к pipe держит список доступа, а не uid");
        var path = Path.Combine(Path.GetTempPath(), "pg-peer-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), Ct);
        using var accepted = await listener.AcceptAsync(Ct);
        File.Delete(path);

        PeerCredentials.UnixUid(accepted).ShouldBe(PeerCredentials.CurrentUid());
    }

    /// <summary>Запрос в режиме прокси; системный прокси — только по просьбе теста, настройки машины не трогаются.</summary>
    internal static V1.ConnectRequest Request(ServerDefinition server, int proxyPort, bool systemProxy = false)
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            Protection = ProtectionSettings.Default with { AllowLan = true },
            Connection = ConnectionSettings.Default with { ProxyPort = proxyPort, ConnectTimeoutSeconds = 10, SystemProxy = systemProxy },
        };
        return new V1.ConnectRequest
        {
            Server = server.ToContract("srv-1"),
            Mode = V1.Mode.Proxy,
            Protection = settings.ToContract(),
            Connection = settings.ToConnection(),
            Routing = settings.ToRouting(),
        };
    }

    /// <summary>Читать поток состояния, пока не придет нужное; не пришло за 20 с — ошибка теста.</summary>
    internal static async Task<List<V1.Status>> ReadUntilAsync(AsyncServerStreamingCall<V1.Status> watch, Func<V1.Status, bool> done)
    {
        var seen = new List<V1.Status>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (await watch.ResponseStream.MoveNext(timeout.Token))
        {
            seen.Add(watch.ResponseStream.Current);
            if (done(watch.ResponseStream.Current))
            {
                return seen;
            }
        }

        throw new InvalidOperationException("Поток состояния закрылся: " + string.Join(", ", seen.Select(s => s.State)));
    }

    private static async Task<string> Get(Uri url, int proxyPort)
    {
        using var handler = new SocketsHttpHandler { Proxy = new FixedProxy(new Uri($"socks5://127.0.0.1:{proxyPort}")), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(url, Ct);
    }

    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
