using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Hysteria2 и TUIC через ретранслятор sing-box за Xray (решение 452) на настоящих серверах: sing-box с входом hysteria2 или
/// tuic и самоподписанным сертификатом, выход — на сайт и DNS теста. Замер задержки, режим прокси через службу (с выходом
/// ретранслятора посреди подключения) и туннель VPN в своем сетевом пространстве Linux.
/// </summary>
[Collection(NamespaceSerial.Name)]
public sealed class RelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // Публичный адрес: через прокси он идет к серверу (локальную сеть запрос теста пускает напрямую), а сервер теста ведет
    // соединения на свой сайт.
    private static readonly Uri Public = new("http://1.1.1.1/");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Latency_of_hysteria2_and_tuic_is_measured_through_the_relay()
    {
        SkipWithoutCores();
        using var site = new LocalSite("204");
        await using var hysteria = await RelayServer.StartAsync(ProxyProtocol.Hysteria2, site);
        await using var tuic = await RelayServer.StartAsync(ProxyProtocol.Tuic, site);
        var wrong = hysteria.Definition with { Credentials = hysteria.Definition.Credentials with { Password = "not-the-password" } };

        var results = await LatencyProbe.MeasureAsync(XrayKit.Xray!, XrayKit.SingBox, [hysteria.Definition, tuic.Definition, wrong], ConnectionSettings.Default,
            site.Url, Timeout, TimeProvider.System, Ct);

        results.Select(r => r.Failure).ShouldBe([null, null, LatencyFailure.NoResponse], string.Join(", ", results));
        results[0].Milliseconds.ShouldNotBeNull();
        site.Requests.ShouldBe(2, "до сайта дошли запросы только через серверы с верным паролем");
    }

    [Fact]
    public async Task Proxy_mode_reaches_hysteria2_and_tuic_and_the_relay_lives_and_dies_with_xray()
    {
        SkipWithoutCores();
        using var site = new LocalSite("через ретранслятор");
        await using var hysteria = await RelayServer.StartAsync(ProxyProtocol.Hysteria2, site);
        await using var tuic = await RelayServer.StartAsync(ProxyProtocol.Tuic, site);
        await using var service = await RunningService.StartAsync(options: o => o with { SingBoxPath = XrayKit.SingBox, Reconnect = ReconnectTests.Fast with { Every = null } });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var proxyPort = XrayKit.FreePort();

        (await service.Client.ConnectAsync(ServiceIpcTests.Request(hysteria.Definition, proxyPort), cancellationToken: Ct)).Accepted.ShouldBeTrue();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        (await ServiceIpcTests.Get(Public, proxyPort)).ShouldBe("через ретранслятор");
        var (xray, relay) = Cores(service);

        // Ретранслятор вышел — Xray снимается вместе с ним, и служба переподключается, как после выхода ядра.
        Process.GetProcessById(relay).Kill();
        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        states.Select(s => s.State).ShouldContain(V1.ConnectionState.Reconnecting);
        Exited(xray).ShouldBeTrue("Xray без ретранслятора не остается");
        var (xray2, relay2) = Cores(service);
        (xray2, relay2).ShouldNotBe((xray, relay));
        (await ServiceIpcTests.Get(Public, proxyPort)).ShouldBe("через ретранслятор");

        // Другой сервер — TUIC: прежние ядра остановлены оба.
        var second = ServiceIpcTests.Request(tuic.Definition, proxyPort);
        second.Server.Id = "srv-2";
        (await service.Client.ConnectAsync(second, cancellationToken: Ct)).Accepted.ShouldBeTrue();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected && s.ServerId == "srv-2", service.Journal);
        (Exited(xray2), Exited(relay2)).ShouldBe((true, true));
        (await ServiceIpcTests.Get(Public, proxyPort)).ShouldBe("через ретранслятор");
        var (_, relay3) = Cores(service);

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
        Exited(relay3).ShouldBeTrue("отключение останавливает и ретранслятор");
        site.Requests.ShouldBe(3);
        var journal = string.Join('\n', service.Journal.Tail(ServiceJournal.Capacity).Select(e => e.Message));
        journal.ShouldNotContain(XrayKit.Password, Case.Sensitive, "пароль сервера в журнал не попадает");
    }

    [Fact]
    public async Task Xray_that_exits_by_itself_takes_the_relay_with_it()
    {
        SkipWithoutCores();
        using var site = new LocalSite("через ретранслятор");
        await using var hysteria = await RelayServer.StartAsync(ProxyProtocol.Hysteria2, site);
        // Первая попытка переподключения — через минуту: ретранслятор до нее уходит сам, а не с остановкой старых ядер.
        var minute = TimeSpan.FromMinutes(1);
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            SingBoxPath = XrayKit.SingBox,
            Reconnect = new ReconnectTiming(null, minute, minute, minute, TimeSpan.FromMinutes(5)),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(ServiceIpcTests.Request(hysteria.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        var (xray, relay) = Cores(service);

        Process.GetProcessById(xray).Kill();

        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting, service.Journal);
        Exited(relay).ShouldBeTrue("ретранслятор без Xray не остается");
    }

    [Fact]
    public async Task Proxy_is_protected_only_when_the_relay_is_up()
    {
        SkipWithoutCores();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ретранслятор с задержкой — сценарий sh");
        using var site = new LocalSite("через ретранслятор");
        await using var hysteria = await RelayServer.StartAsync(ProxyProtocol.Hysteria2, site);
        var directory = Directory.CreateTempSubdirectory("pg-slow-");
        try
        {
            // Ретранслятор открывает входы на 6 с позже Xray (дольше, чем Xray сам повторяет соединение с ним): «Защищено» раньше
            // обещало бы прокси, который к серверу не ведет.
            var slow = Path.Combine(directory.FullName, "sing-box");
            await File.WriteAllTextAsync(slow, $"#!/bin/sh\nsleep 6\nexec '{XrayKit.SingBox}' \"$@\"\n", Ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(slow, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            await using var service = await RunningService.StartAsync(options: o => o with { SingBoxPath = slow });
            using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
            var proxyPort = XrayKit.FreePort();

            var request = ServiceIpcTests.Request(hysteria.Definition, proxyPort);
            request.Connection.ConnectTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(20));

            await service.Client.ConnectAsync(request, cancellationToken: Ct);

            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
            (await ServiceIpcTests.Get(Public, proxyPort)).ShouldBe("через ретранслятор", "«Защищено» — когда ретранслятор уже ведет к серверу");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Vpn_carries_tcp_and_dns_over_hysteria2_past_its_own_tunnel()
    {
        NetworkNamespace.SkipUnlessAvailable("curl", "python3", "ip");
        SkipWithoutCores();
        using var netns = NetworkNamespace.Create();
        var (host, _, _) = netns.LinkAsMachine();
        using var site = new LocalSite("через Hysteria2");
        using var dns = new TestDns(IPAddress.Parse("203.0.113.9"));
        await using var server = await RelayServer.StartAsync(ProxyProtocol.Hysteria2, site, dns, host);
        var request = new CoreRequest(server.Definition, ConnectionMode.Vpn, ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug }, RoutingSettings.Default);
        var output = new CoreOutput(netns);

        var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], Timeout, TimeProvider.System, netns.Path, output.Add,
            openUdp: netns.OpenSocksUdpAsync, cancellationToken: Ct);
        int relay;
        try
        {
            relay = tunnel.Relay.ShouldNotBeNull("у Hysteria2 — ретранслятор");
            (await output.TraceAsync(() => netns.CurlAsync(new Uri("http://1.1.1.1/")))).ShouldBe("через Hysteria2",
                "TUN → Xray → ретранслятор → сервер: соединение ретранслятора с сервером в TUN не возвращается");
            (await output.TraceAsync(() => netns.ResolveAsync("example.com", IPAddress.Parse(SingBoxConfigBuilder.TunDns))))
                .ShouldBe("203.0.113.9", "DNS ОС — по UDP через Xray, ретранслятор и QUIC сервера");
            dns.Queries.ShouldContain("example.com");
        }
        finally
        {
            await tunnel.DisposeAsync();
        }

        Exited(relay).ShouldBeTrue("туннель остановлен — и ретранслятор");
        site.Requests.ShouldBe(1);
    }

    private static void SkipWithoutCores() =>
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");

    /// <summary>Xray и ретранслятор, которые служба запустила последними, — из ее журнала.</summary>
    private static (int Xray, int Relay) Cores(RunningService service)
    {
        const string Prefix = "Ядро Xray запущено, процесс ";
        const string Marker = ", ретранслятор sing-box для Hysteria2 и TUIC — процесс ";
        var line = service.Journal.Tail(ServiceJournal.Capacity).Last(e => e.Message.StartsWith(Prefix, StringComparison.Ordinal)).Message;
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        at.ShouldBeGreaterThan(0, line);
        return (int.Parse(line[Prefix.Length..at], CultureInfo.InvariantCulture), int.Parse(line[(at + Marker.Length)..], CultureInfo.InvariantCulture));
    }

    // Процесс вышел (или вот-вот выйдет: снятие ждет до 10 с).
    private static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Сервер Hysteria2 (с обфускацией salamander) или TUIC теста — sing-box на <c>listen</c> с самоподписанным сертификатом на
    /// example.com. Соединения TCP он ведет на сайт теста, запросы на порт 53 — на DNS теста: из машины ничего не уходит.
    /// </summary>
    private sealed class RelayServer(ServerDefinition definition, CoreProcess process) : IAsyncDisposable
    {
        public ServerDefinition Definition { get; } = definition;

        public static async Task<RelayServer> StartAsync(ProxyProtocol protocol, LocalSite site, TestDns? dns = null, IPAddress? listen = null)
        {
            var address = (listen ?? IPAddress.Loopback).ToString();
            var port = LocalPort.FreeForTcpAndUdp();
            var tls = JsonNode.Parse(XrayKit.SelfSigned("example.com"))!.AsObject();
            tls["enabled"] = true;
            tls["alpn"] = new JsonArray("h3");
            var hysteria = protocol == ProxyProtocol.Hysteria2;
            var inbound = new JsonObject
            {
                ["type"] = hysteria ? "hysteria2" : "tuic",
                ["listen"] = address,
                ["listen_port"] = port,
                ["users"] = new JsonArray(hysteria
                    ? new JsonObject { ["password"] = XrayKit.Password }
                    : new JsonObject { ["uuid"] = XrayKit.Uuid, ["password"] = XrayKit.Password }),
                ["tls"] = tls,
            };
            if (hysteria)
            {
                inbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = ObfsPassword };
            }
            else
            {
                inbound["congestion_control"] = "bbr";
            }

            var rules = new JsonArray();
            if (dns is not null)
            {
                rules.Add(Redirect(new JsonObject { ["port"] = 53 }, dns.Port));
            }

            rules.Add(Redirect(new JsonObject { ["network"] = "tcp" }, site.Url.Port));
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false },
                ["inbounds"] = new JsonArray(inbound),
                ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
                ["route"] = new JsonObject { ["rules"] = rules },
            };

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new List<string>();
            var process = CoreProcess.StartSingBox(XrayKit.SingBox!, config, output: line =>
            {
                lock (output)
                {
                    output.Add(line);
                }

                if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
                {
                    started.TrySetResult();
                }
            });
            process.Exited += (_, _) => started.TrySetException(new InvalidOperationException("Сервер sing-box вышел: " + string.Join('\n', output)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

            var definition = new ServerDefinition("Тест", string.Empty, protocol, address, port, TransportOptions.Quic,
                new SecurityOptions(TransportSecurity.Tls, Sni: "example.com", AllowInsecure: true),
                hysteria ? new ProtocolOptions(Obfs: "salamander") : new ProtocolOptions(CongestionControl: "bbr"),
                hysteria ? new ServerCredentials(Password: XrayKit.Password, ObfsPassword: ObfsPassword) : new ServerCredentials(Uuid: XrayKit.Uuid, Password: XrayKit.Password));
            return new RelayServer(definition, process);

            static JsonObject Redirect(JsonObject match, int to)
            {
                match["action"] = "route";
                match["outbound"] = "direct";
                match["override_address"] = "127.0.0.1";
                match["override_port"] = to;
                return match;
            }
        }

        private const string ObfsPassword = "not-a-real-obfs-password";

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
