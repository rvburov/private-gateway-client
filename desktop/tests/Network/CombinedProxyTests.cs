using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed class CombinedProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_fixed_proxy_port_is_rejected_before_starting_any_core(bool udp)
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, udp ? SocketType.Dgram : SocketType.Stream,
            udp ? ProtocolType.Udp : ProtocolType.Tcp)
        { ExclusiveAddressUse = true };
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        if (!udp)
        {
            occupied.Listen(1);
        }

        var port = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        var request = new CoreRequest(XrayKit.Pair("vless", 443).Server, ConnectionMode.Vpn, ProtectionSettings.Default,
            ConnectionSettings.Default with { VpnSystemProxy = true, ProxyPort = port }, RoutingSettings.Default);
        var error = await Should.ThrowAsync<TunnelException>(() => VpnTunnel.StartAsync("not-used-xray", "not-used-sing-box", request,
            [IPAddress.Loopback], TimeSpan.FromSeconds(5), TimeProvider.System));
        error.Message.ShouldContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        error.Message.ShouldContain("занят или недоступен");
    }

    [Fact]
    public async Task Vpn_core_accepts_http_and_socks_on_the_extra_input_and_keeps_site_rules()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("два входа одного ядра");
        var serverPort = XrayKit.FreePort();
        var (serverDefinition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var port = XrayKit.FreePort();
        var request = new CoreRequest(serverDefinition, ConnectionMode.Vpn, ProtectionSettings.Default,
            ConnectionSettings.Default with { VpnSystemProxy = true, ProxyPort = port },
            RoutingSettings.Default);
        var config = Build(request);
        using var core = await XrayKit.Start(config, port);
        foreach (var protocol in new[] { "http", "socks5" })
        {
            using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"{protocol}://127.0.0.1:{port}")) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            (await http.GetStringAsync(site.Url, TestContext.Current.CancellationToken)).ShouldBe("два входа одного ядра");
        }

        site.Requests.ShouldBe(2);
        var blocked = request with
        {
            Connection = request.Connection with { ProxyPort = XrayKit.FreePort() },
            Routing = new RoutingSettings(RoutingMode.AllTraffic, [], [new DomainRule("127.0.0.1", RuleAction.Block)]),
        };
        using var blockedCore = await XrayKit.Start(Build(blocked), blocked.Connection.ProxyPort);
        foreach (var protocol in new[] { "http", "socks5" })
        {
            using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"{protocol}://127.0.0.1:{blocked.Connection.ProxyPort}")) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync(site.Url, TestContext.Current.CancellationToken));
        }

        site.Requests.ShouldBe(2, "правило блокирует тот же адрес, который до этого действительно отвечал");
    }

    private static JsonObject Build(CoreRequest request)
    {
        var config = XrayConfigBuilder.Build(request, VpnTunnel.NewLink(IPAddress.Loopback)).Config.ShouldNotBeNull();
        var rules = config["routing"]!["rules"]!.AsArray();
        // Сайт теста на loopback: убираем только общее правило частных сетей. Его соединяет выход сервера,
        // пользовательское правило того же адреса остается и должно дать наблюдаемый отказ.
        var local = rules.Single(r => r!["ip"] is JsonArray addresses && addresses.Any(a => a?.GetValue<string>() == "127.0.0.0/8"));
        rules.Remove(local);
        return config;
    }

    private sealed class LocalProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => endpoint;
        public bool IsBypassed(Uri host) => false;
    }
}
