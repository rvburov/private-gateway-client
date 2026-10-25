using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Контролируемый прокси-тест (этап 2): настоящее ядро Xray с конфигом от <see cref="XrayConfigBuilder"/>,
/// свой сервер и свой сайт — все на 127.0.0.1, без интернета и без прав администратора.
/// </summary>
public sealed class XrayProxyTests
{
    private const string Page = "привет через туннель";

    /// <summary>Каждый протокол Xray из этапа 2 и транспорты, которые служба собирает в конфиг.</summary>
    [Theory]
    [InlineData("vless")]
    [InlineData("vless-ws")]
    [InlineData("vless-grpc")]
    [InlineData("vless-httpupgrade")]
    [InlineData("vmess")]
    [InlineData("trojan-tls")]
    [InlineData("shadowsocks")]
    public async Task Request_goes_through_the_server_by_socks5_and_http_on_one_port(string kind)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair(kind, serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        var config = ClientConfig(definition, proxyPort);
        // Сайт теста — на 127.0.0.1, а локальная сеть в конфиге закрыта: для проверки туннеля правило убирается.
        config["routing"]!["rules"]!.AsArray().RemoveAt(0);
        using var client = await XrayKit.Start(config, proxyPort);

        (await Get(site.Url, $"socks5://127.0.0.1:{proxyPort}")).ShouldBe(Page);
        (await Get(site.Url, $"http://127.0.0.1:{proxyPort}")).ShouldBe(Page, "HTTP и SOCKS5 — на одном порту");
        site.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task Local_network_stays_closed_through_the_proxy_by_default()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ClientConfig(definition, proxyPort), proxyPort);

        await Should.ThrowAsync<Exception>(() => Get(site.Url, $"socks5://127.0.0.1:{proxyPort}"));

        site.Requests.ShouldBe(0, "geoip:private — блок: до сайта в локальной сети запрос не дошел");
    }

    [Fact]
    public void Missing_core_is_an_error_not_a_silent_start()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-xray-" + Guid.NewGuid().ToString("N"), "xray");

        Should.Throw<System.ComponentModel.Win32Exception>(() => XrayProcess.Start(missing, new JsonObject()));
    }

    private static JsonObject ClientConfig(ServerDefinition server, int proxyPort)
    {
        var request = new CoreRequest(server, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort, CoreLog = CoreLogLevel.Info }, RoutingSettings.Default);
        return XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
    }

    private static async Task<string> Get(Uri url, string proxy)
    {
        using var handler = new SocketsHttpHandler { Proxy = new AlwaysProxy(new Uri(proxy)), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return await http.GetStringAsync(url).ConfigureAwait(false);
    }

    /// <summary>Прокси для всех адресов: WebProxy сам пропускает localhost мимо прокси, а тесту нужен именно он.</summary>
    private sealed class AlwaysProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
