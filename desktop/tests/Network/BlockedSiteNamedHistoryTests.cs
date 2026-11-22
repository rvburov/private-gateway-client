using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Имена в истории получены от настоящего маршрутизатора после FakeDNS/SNI, а не угаданы по адресу IP.</summary>
public sealed class BlockedSiteNamedHistoryTests
{
    private const string Blocked = "blocked.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("socks5", false)]
    [InlineData("socks5", true)]
    public async Task Fake_dns_ip_is_recorded_as_the_restored_blocked_name_without_public_access_logs(string protocol, bool sniffing)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var port = XrayKit.FreePort();
        var request = Request(port, ConnectionMode.Vpn, sniffing, fakeDns: true);
        var config = XrayConfigBuilder.Build(request,
            new TunLink(XrayKit.FreePort(), "only-local-test", IPAddress.Loopback.ToString())).Config.ShouldNotBeNull();
        config["log"]!["access"]!.GetValue<string>().ShouldBe("none", "исходный access.To не дает восстановленное имя");
        config["log"]!["loglevel"]!.GetValue<string>().ShouldBe("info", "уровень внутри ядра нужен только закрытому приемнику");
        var dnsPort = AddDnsInput(config);
        var history = new BlockedSiteHistory(TimeProvider.System);
        var publicLines = new ServiceJournal(TimeProvider.System);
        using var core = await XrayKit.Start(config, port, Capture(history, publicLines));

        var fake = await ResolveAsync(new IPEndPoint(IPAddress.Loopback, dnsPort), Blocked);
        var bytes = fake.GetAddressBytes();
        (bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19).ShouldBeTrue("ответил реальный FakeDNS этого ядра");
        using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"{protocol}://127.0.0.1:{port}")) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync($"http://{fake}/", Ct));

        await WaitForHistoryAsync(history);
        var actual = history.Snapshot().ShouldHaveSingleItem();
        actual.Host.ShouldBe(Blocked, "браузер послал только fake IP; настоящее имя восстановлено таблицей ядра");
        SitePattern.IsPlainDomain(actual.Host).ShouldBeTrue("кнопка «Разрешить» должна работать с доменом, не общим IP");
        PublicHistoryDoesNotContainTargets(publicLines, fake.ToString());
    }

    [Fact]
    public async Task Tls_sni_is_recorded_when_socks_requested_only_an_ip_and_fake_dns_is_off()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var port = XrayKit.FreePort();
        var config = XrayConfigBuilder.Build(Request(port, ConnectionMode.Proxy, sniffing: true, fakeDns: false)).Config.ShouldNotBeNull();
        // Диапазон документации IPv6 не относится к LocalNetworks; диапазоны документации IPv4 в нем есть.
        // Даже при ошибке блока оба freedom-выхода могут набрать только нашу петлю: тест не обращается к внешней сети.
        foreach (var outbound in config["outbounds"]!.AsArray().OfType<JsonObject>().Where(item => item["protocol"]?.GetValue<string>() == "freedom"))
        {
            outbound["settings"]!["redirect"] = $"127.0.0.1:{targetPort}";
        }
        var history = new BlockedSiteHistory(TimeProvider.System);
        var publicLines = new ServiceJournal(TimeProvider.System);
        using var core = await XrayKit.Start(config, port, Capture(history, publicLines));
        using var client = await SocksIpAsync(port);
        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = Blocked,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, stop.Token));

        await WaitForHistoryAsync(history);
        history.Snapshot().ShouldHaveSingleItem().Host.ShouldBe(Blocked, "имя было только в ClientHello; обратный DNS не использовался");
        target.Pending().ShouldBeFalse("маршрутизатор выбрал block по SNI до соединения с IP");
        PublicHistoryDoesNotContainTargets(publicLines, null);

        // Контроль меняет только действие того же доменного правила: общий блок IPv6 или неисправный прямой
        // выход не должны выдавать себя за работающую фильтрацию SNI. Выход остается ограничен нашей петлей.
        var allowed = config.DeepClone().AsObject();
        var allowedPort = XrayKit.FreePort();
        allowed["inbounds"]![0]!["port"] = allowedPort;
        var domainRule = allowed["routing"]!["rules"]!.AsArray().Single(rule => rule?["domain"] is JsonArray domains
            && domains.Any(domain => domain?.GetValue<string>() == "domain:" + Blocked));
        domainRule!["outboundTag"] = XrayConfigBuilder.ProxyTag;
        using var allowedCore = await XrayKit.Start(allowed, allowedPort);
        using var allowedClient = await SocksIpAsync(allowedPort);
        using var allowedSsl = new SslStream(allowedClient.GetStream(), leaveInnerStreamOpen: true);
        using var allowedStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        allowedStop.CancelAfter(TimeSpan.FromSeconds(5));
        var handshake = allowedSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = Blocked,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, allowedStop.Token);
        using var arrived = await target.AcceptTcpClientAsync(allowedStop.Token);
        arrived.Close();
        await Assert.ThrowsAnyAsync<Exception>(() => handshake);
    }

    private static async Task<TcpClient> SocksIpAsync(int port)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, Ct);
            var hello = new byte[2];
            await stream.ReadExactlyAsync(hello, Ct);
            hello.ShouldBe(new byte[] { 5, 0 });
            var connect = new List<byte> { 5, 1, 0, 4 };
            connect.AddRange(IPAddress.Parse("2001:db8::20").GetAddressBytes());
            connect.AddRange([1, 187]);
            await stream.WriteAsync(connect.ToArray(), Ct);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, Ct);
            reply[1].ShouldBe((byte)0);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static Action<string> Capture(BlockedSiteHistory history, ServiceJournal publicLines) => line =>
    {
        if (!history.Consume(line) && CoreLines.Level("xray", line) is JournalLevel.Warning or JournalLevel.Error)
        {
            publicLines.Add(CoreLines.Level("xray", line), "xray", line);
        }
    };

    private static void PublicHistoryDoesNotContainTargets(ServiceJournal publicLines, string? fake) =>
        publicLines.Tail(ServiceJournal.Capacity).ShouldAllBe(line => !line.Message.Contains(Blocked, StringComparison.Ordinal)
            && (fake == null || !line.Message.Contains(fake, StringComparison.Ordinal))
            && !line.Message.Contains(" accepted ", StringComparison.Ordinal) && !line.Message.Contains("taking detour", StringComparison.Ordinal));

    private static async Task WaitForHistoryAsync(BlockedSiteHistory history)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (history.Snapshot().Count == 0 && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(20, Ct);
        }
    }

    private static CoreRequest Request(int port, ConnectionMode mode, bool sniffing, bool fakeDns) => new(
        new ServerDefinition("unused", string.Empty, ProxyProtocol.Socks5, "127.0.0.1", 10808,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials()), mode,
        ProtectionSettings.Default with { FakeDns = fakeDns, AllowLan = true },
        ConnectionSettings.Default with { ProxyPort = port, VpnSystemProxy = mode == ConnectionMode.Vpn, Sniffing = sniffing, CoreLog = CoreLogLevel.Warning },
        RoutingSettings.Default with { Rules = [new DomainRule(Blocked, RuleAction.Block)] }, LocalSiteFilter: true);

    private static int AddDnsInput(JsonObject config)
    {
        var port = XrayKit.FreePort();
        config["inbounds"]!.AsArray().Add(new JsonObject
        {
            ["tag"] = "test-dns-in", ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "dokodemo-door",
            ["settings"] = new JsonObject { ["address"] = "8.8.8.8", ["port"] = 53, ["network"] = "udp" },
        });
        config["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject
        {
            ["type"] = "field", ["inboundTag"] = new JsonArray("test-dns-in"), ["outboundTag"] = XrayConfigBuilder.DnsTag,
        });
        return port;
    }

    private static async Task<IPAddress> ResolveAsync(IPEndPoint resolver, string host)
    {
        var query = new List<byte> { 0x50, 0x47, 0x01, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in host.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }
        query.AddRange([0, 0, 1, 0, 1]);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        await udp.SendAsync(query.ToArray(), resolver, stop.Token);
        var answer = (await udp.ReceiveAsync(stop.Token)).Buffer;
        (answer[3] & 0x0F).ShouldBe(0);
        ((answer[^6] << 8) | answer[^5]).ShouldBe(4);
        return new IPAddress(answer.AsSpan(answer.Length - 4, 4));
    }

    private sealed class LocalProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => endpoint;
        public bool IsBypassed(Uri host) => false;
    }
}
