using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящий FakeDNS и оба протокола дополнительного входа VPN; только localhost, без TUN и прав.</summary>
public sealed class FakeDnsProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http", true)]
    [InlineData("http", false)]
    [InlineData("socks5", true)]
    [InlineData("socks5", false)]
    public async Task Proxy_recovers_its_own_fake_dns_address_and_applies_domain_rules(string protocol, bool sniffing)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("имя из FakeDNS через выбранный сервер");
        var output = new CoreOutput();
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        // Сервер не выпускает запросы в интернет. Только два имени теста ведут на сайт; сам fake IP не отвечает.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("full:example.com", "full:blocked.example"),
                ["outboundTag"] = "site",
            }),
        };
        using var server = await XrayKit.Start(serverConfig, serverPort, line => output.Add("server", line));
        var port = XrayKit.FreePort();
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true },
            ConnectionSettings.Default with { VpnSystemProxy = true, ProxyPort = port, Sniffing = sniffing, CoreLog = CoreLogLevel.Debug },
            new RoutingSettings(RoutingMode.AllTraffic, [], [new DomainRule("blocked.example", RuleAction.Block)]));
        var config = XrayConfigBuilder.Build(request, VpnTunnel.NewLink(IPAddress.Loopback)).Config.ShouldNotBeNull();
        // Вместо TUN только транспорт DNS-пакета: ответ и таблицу FakeDNS создает тот же Xray, который обслужит HTTP/SOCKS.
        var dnsPort = AddDnsInput(config);
        using var core = await XrayKit.Start(config, port, line => output.Add("client", line));
        var resolver = new IPEndPoint(IPAddress.Loopback, dnsPort);
        var address = await output.TraceAsync(() => ResolveAsync(resolver, "example.com"), "DNS example.com");
        var bytes = address.GetAddressBytes();
        (bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19)
            .ShouldBeTrue("адрес получен из настоящего пула FakeDNS этого ядра");
        using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"{protocol}://127.0.0.1:{port}")) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var page = new Uri($"http://{address}/");
        (await output.TraceAsync(() => http.GetStringAsync(page, Ct), $"{protocol} к {page}"))
            .ShouldBe("имя из FakeDNS через выбранный сервер");
        (await output.TraceAsync(() => http.GetStringAsync("http://example.com/", Ct), $"{protocol} к обычному имени"))
            .ShouldBe("имя из FakeDNS через выбранный сервер");
        site.Requests.ShouldBe(2);

        var blockedAddress = await output.TraceAsync(() => ResolveAsync(resolver, "blocked.example"), "DNS blocked.example");
        blockedAddress.ShouldNotBe(address);
        await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync($"http://{blockedAddress}/", Ct));
        site.Requests.ShouldBe(2, "сервер ответил бы на blocked.example, но клиент восстановил имя и применил Block");
        await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync("http://198.19.255.254/", Ct));
        site.Requests.ShouldBe(2, "адрес без записи FakeDNS закрыт, повторный разбор не зацикливается");
    }

    [Theory]
    [InlineData("http", RuleAction.Direct)]
    [InlineData("http", RuleAction.Block)]
    [InlineData("socks5", RuleAction.Direct)]
    [InlineData("socks5", RuleAction.Block)]
    public async Task Recovered_name_keeps_ip_rules_and_unknown_fake_ip_stays_blocked_with_lan_allowed(string protocol, RuleAction action)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("через сервер");
        using var direct = new LocalSite("прямой выход");
        using var dns = new TestDns(IPAddress.Parse("1.1.1.1"));
        var output = new CoreOutput();
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dns.Port}" } });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" },
                new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:example.com"), ["outboundTag"] = "site" }),
        };
        using var server = await XrayKit.Start(serverConfig, serverPort, line => output.Add("server", line));
        var port = XrayKit.FreePort();
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true, AllowLan = true, Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default with { VpnSystemProxy = true, ProxyPort = port, CoreLog = CoreLogLevel.Debug },
            new RoutingSettings(RoutingMode.Split, [], [new DomainRule("1.1.1.0/24", action)]));
        var config = XrayConfigBuilder.Build(request, VpnTunnel.NewLink(IPAddress.Loopback)).Config.ShouldNotBeNull();
        // Прямой выход тоже отвечает только своим сайтом. Даже ошибочно пропущенный unknown fake IP не уйдет в сеть.
        config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == XrayConfigBuilder.DirectTag)!
            ["settings"]!["redirect"] = $"127.0.0.1:{direct.Url.Port}";
        var dnsPort = AddDnsInput(config);
        using var core = await XrayKit.Start(config, port, line => output.Add("client", line));
        var address = await output.TraceAsync(() => ResolveAsync(new IPEndPoint(IPAddress.Loopback, dnsPort), "example.com"), "DNS example.com");
        using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"{protocol}://127.0.0.1:{port}")) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        if (action == RuleAction.Direct)
        {
            (await output.TraceAsync(() => http.GetStringAsync($"http://{address}/", Ct), $"{protocol} с правилом IP Direct"))
                .ShouldBe("прямой выход");
            direct.Requests.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync($"http://{address}/", Ct));
            direct.Requests.ShouldBe(0);
        }

        dns.Queries.ShouldContain("example.com", "правило подсети сравнивает настоящий адрес восстановленного имени");
        site.Requests.ShouldBe(0, "правило IP не обходится выходом proxy после восстановления имени");
        var requests = direct.Requests;
        await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync("http://198.19.255.254/", Ct));
        direct.Requests.ShouldBe(requests, "AllowLan не разрешает остаточный адрес FakeDNS без имени");
        site.Requests.ShouldBe(0);
    }

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

    private static async Task<IPAddress> ResolveAsync(IPEndPoint resolver, string name)
    {
        var query = new List<byte> { 0x50, 0x47, 0x01, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }

        query.AddRange([0, 0, 1, 0, 1]);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        await udp.SendAsync(query.ToArray(), resolver, wait.Token);
        var reply = (await udp.ReceiveAsync(wait.Token)).Buffer;
        reply[0..2].ShouldBe(query.Take(2).ToArray());
        (reply[2] & 0x80).ShouldBe(0x80, "ответ, а не возвращенный запрос");
        (reply[3] & 0x0F).ShouldBe(0, "DNS завершился без ошибки");
        ((reply[6] << 8) | reply[7]).ShouldBe(1, "один ответ A");
        // Свой единственный вопрос A: длина вопроса известна; ответ Xray завершает четырехбайтный IPv4.
        ((reply[^6] << 8) | reply[^5]).ShouldBe(4);
        return new IPAddress(reply.AsSpan(reply.Length - 4, 4));
    }

    private sealed class LocalProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => endpoint;
        public bool IsBypassed(Uri host) => false;
    }
}
