using System.Globalization;
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

    /// <summary>
    /// Каждый протокол Xray и каждый транспорт, которые служба собирает в конфиг, — с TLS и без; регрессия этапа 11. REALITY —
    /// в <see cref="PrivateGatewayCompatibilityTests"/>, Hysteria2 и TUIC — в <see cref="RelayTests"/>.
    /// </summary>
    [Theory]
    [InlineData("vless", false)]
    [InlineData("vless-ws", false)]
    [InlineData("vless-grpc", false)]
    [InlineData("vless-httpupgrade", false)]
    [InlineData("vless-xhttp", false)]
    [InlineData("vless-ws-tls", false)]
    [InlineData("vmess", false)]
    [InlineData("vmess-grpc-tls", false)]
    [InlineData("trojan-tls", false)]
    [InlineData("shadowsocks", false)]
    [InlineData("shadowsocks-2022", false)]
    [InlineData("wireguard", false)]
    [InlineData("vless-ws", true)]
    [InlineData("vmess", true)]
    [InlineData("trojan-tls", true)]
    public async Task Request_goes_through_the_server_by_socks5_and_http_on_one_port(string kind, bool mux)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair(kind, serverPort);
        var url = site.Url;
        if (kind == "wireguard")
        {
            // Выход WireGuard без CAP_NET_ADMIN (CI, обычный пользователь) — стек gVisor, а он 127.0.0.1 в туннель не шлет:
            // запрос идет на адрес документации, выход сервера ведет его на сайт теста.
            serverConfig["outbounds"] = new JsonArray(
                new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
            url = new Uri($"http://192.0.2.10:{site.Url.Port}/");
        }

        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        var config = ClientConfig(definition, proxyPort, mux: mux);
        (config["outbounds"]![0]!["mux"]?["enabled"]?.GetValue<bool>() ?? false).ShouldBe(mux, "Mux — из настройки «Подключение и ядро»");
        // Сайт теста — на 127.0.0.1, а локальная сеть в конфиге закрыта: для проверки туннеля правило убирается.
        config["routing"]!["rules"]!.AsArray().RemoveAt(0);
        using var client = await XrayKit.Start(config, proxyPort);

        (await Get(url, $"socks5://127.0.0.1:{proxyPort}")).ShouldBe(Page);
        (await Get(url, $"http://127.0.0.1:{proxyPort}")).ShouldBe(Page, "HTTP и SOCKS5 — на одном порту");
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

        site.Requests.ShouldBe(0, "локальная сеть — блок: до сайта в локальной сети запрос не дошел");
    }

    [Theory]
    [InlineData(AddressPreference.PreferIpv4)]
    [InlineData(AddressPreference.Ipv4Only)]
    public async Task Name_with_an_ipv6_address_goes_through_when_ipv6_is_closed(AddressPreference addresses)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        // Выход сервера — на свой сайт: из машины ничего не уходит, куда бы ни вело имя.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        // У имени и A, и AAAA — как у большинства сайтов.
        using var dns = new TestDns(IPAddress.Parse("1.1.1.1"), answer6: IPAddress.Parse("2606:4700:4700::1111"));
        var proxyPort = XrayKit.FreePort();
        var config = ClientConfig(definition, proxyPort, ProtectionSettings.Default with { Addresses = addresses });
        config["dns"]!["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = dns.Port });
        // DNS теста — на 127.0.0.1, а локальная сеть закрыта: запросам ядра к нему — напрямую, остальные правила как есть.
        config["routing"]!["rules"]!.AsArray().Insert(0,
            new JsonObject { ["type"] = "field", ["network"] = "udp", ["port"] = dns.Port.ToString(CultureInfo.InvariantCulture), ["outboundTag"] = XrayConfigBuilder.DirectTag });
        // Подробный журнал ядра — в сообщение при сбое: на Windows в CI запрос ядра изредка не доходит до DNS теста.
        config["log"] = new JsonObject { ["loglevel"] = "debug" };
        using var client = await XrayKit.Start(config, proxyPort);
        var log = new List<string>();
        client.Output += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        (await Get(new Uri("http://dual.example.com/"), $"socks5://127.0.0.1:{proxyPort}")).ShouldBe(Page,
            "сайт с A и AAAA не закрывается целиком из-за адреса IPv6 в ответе DNS — и при «Только IPv4»");
        // Запись DNS теста и вывод ядра приходят из своих потоков: читать их сразу после ответа — значит иногда увидеть
        // пустой журнал (так было на Windows в CI). До 3 с ждем запрос, а без него — хвост журнала ядра.
        for (var i = 0; i < 30 && !dns.Queries.Contains("dual.example.com"); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        lock (log)
        {
            dns.Queries.ShouldContain("dual.example.com", "имя разрешало ядро — правила по адресам проверены; журнал ядра:\n" + string.Join('\n', log));
        }
    }

    [Fact]
    public void Missing_core_is_an_error_not_a_silent_start()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-xray-" + Guid.NewGuid().ToString("N"), "xray");

        Should.Throw<System.ComponentModel.Win32Exception>(() => CoreProcess.StartXray(missing, new JsonObject()));
    }

    private static JsonObject ClientConfig(ServerDefinition server, int proxyPort, ProtectionSettings? protection = null, bool mux = false)
    {
        var request = new CoreRequest(server, ConnectionMode.Proxy, protection ?? ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort, CoreLog = CoreLogLevel.Info, Mux = mux }, RoutingSettings.Default);
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
