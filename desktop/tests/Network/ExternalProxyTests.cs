using System.Net;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Прямой внешний прокси: настоящий TCP/TLS, авторизация, UDP и отсутствие прямого обхода после отказа.</summary>
public sealed class ExternalProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("https", false)]
    [InlineData("https", true)]
    [InlineData("socks5", false)]
    [InlineData("socks5", true)]
    public async Task Applications_reach_the_external_proxy_with_the_selected_authentication(string kind, bool authenticated)
    {
        RequireXray();
        using var site = new LocalSite(ExternalProxyFixture.ExitPage);
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair(kind, externalPort, authenticated);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        var localPort = XrayKit.FreePort();
        localPort.ShouldNotBe(externalPort, "внешний порт и локальный HTTP/SOCKS-вход — независимые настройки");
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        try
        {
            (await ExternalProxyFixture.GetAsync(site.Url, localPort)).ShouldBe(ExternalProxyFixture.ExitPage);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"Внешний прокси {kind}:\nклиент:\n{XrayKit.Log(client)}\nсервер:\n{XrayKit.Log(server)}", error);
        }
        (await ExternalProxyFixture.GetAsync(site.Url, localPort, socks: true)).ShouldBe(ExternalProxyFixture.ExitPage);
        var exit = await LatencyProbe.ExitIpAsync(new ServerProbe(localPort, null, site.Url, TimeSpan.FromSeconds(5)), Ct);

        exit.ShouldBe("203.0.113.9", "контрольный адрес измерен реальным HTTP-запросом через ядро и внешний прокси");
        site.Requests.ShouldBe(3);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task A_wrong_password_never_reaches_the_site(string kind)
    {
        RequireXray();
        using var site = new LocalSite("auth was bypassed");
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        definition = definition with { Credentials = definition.Credentials with { Password = "wrong-test-password" } };
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        await Should.ThrowAsync<Exception>(() => ExternalProxyFixture.GetAsync(site.Url, localPort));

        site.Requests.ShouldBe(0, "ошибка внешней авторизации не разрешает прямой запрос");
    }

    [Fact]
    public async Task Https_does_not_downgrade_when_the_server_certificate_is_wrong()
    {
        RequireXray();
        using var site = new LocalSite("TLS was bypassed");
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair("https", externalPort, authenticated: false);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        definition = definition with
        {
            Security = definition.Security with { PinnedPeerCertSha256 = [new string('0', 64)] },
        };
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        await Should.ThrowAsync<Exception>(() => ExternalProxyFixture.GetAsync(site.Url, localPort));

        site.Requests.ShouldBe(0, "HTTPS не заменяется открытым HTTP или прямым выходом при отказе сертификата");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task A_stopped_external_proxy_does_not_fall_back_to_the_device_connection(string kind)
    {
        RequireXray();
        using var site = new LocalSite("only through the external proxy");
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);
        (await ExternalProxyFixture.GetAsync(site.Url, localPort)).ShouldBe("only through the external proxy");
        site.Requests.ShouldBe(1);

        server.Dispose();

        // Новый HTTP-клиент не берет старое соединение. Сайт на loopback остался доступен напрямую: обход был бы виден.
        await Should.ThrowAsync<Exception>(() => ExternalProxyFixture.GetAsync(site.Url, localPort));
        site.Requests.ShouldBe(1, "после потери upstream число запросов к живому сайту не растет");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Socks5_udp_uses_the_upstream_association_and_its_reply_port(bool authenticated)
    {
        RequireXray();
        using var echo = new UdpEcho();
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair("socks5", externalPort, authenticated);
        ExternalProxyFixture.Redirect(serverConfig, echo.Port);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);
        byte[] payload = [0x50, 0x47, 0x2D, 0x55, 0x44, 0x50];

        var answer = await ExternalProxyFixture.DatagramAsync(localPort, new IPEndPoint(IPAddress.Parse("192.0.2.53"), 5300),
            payload, TimeSpan.FromSeconds(5));

        answer.ShouldBe(payload, "документационный адрес достигает echo только через redirect внешнего SOCKS5-сервера");
        echo.Datagrams.ShouldBe(1);
    }

    [Fact]
    public async Task A_wrong_socks5_password_does_not_open_the_upstream_udp_path()
    {
        RequireXray();
        using var echo = new UdpEcho();
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair("socks5", externalPort, authenticated: true);
        ExternalProxyFixture.Redirect(serverConfig, echo.Port);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        definition = definition with { Credentials = definition.Credentials with { Password = "wrong-test-password" } };
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        var answer = await ExternalProxyFixture.DatagramAsync(localPort, new IPEndPoint(IPAddress.Parse("192.0.2.53"), 5300),
            [0x50, 0x47], TimeSpan.FromSeconds(1));

        answer.ShouldBeNull("неверная авторизация не разрешает UDP ASSOCIATE");
        echo.Datagrams.ShouldBe(0);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task Http_connect_does_not_send_udp_directly(string kind)
    {
        RequireXray();
        using var echo = new UdpEcho();
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        var answer = await ExternalProxyFixture.DatagramAsync(localPort, new IPEndPoint(IPAddress.Loopback, echo.Port),
            [0x50, 0x47], TimeSpan.FromSeconds(1));

        answer.ShouldBeNull("HTTP CONNECT переносит TCP, а не UDP");
        echo.Datagrams.ShouldBe(0, "неподдерживаемая датаграмма не отправляется с устройства напрямую");
    }

    [Fact]
    public async Task Application_dns_datagram_can_follow_the_socks5_udp_path()
    {
        RequireXray();
        using var dns = new TestDns(IPAddress.Parse("203.0.113.53"));
        var externalPort = XrayKit.FreePort();
        var (definition, serverConfig) = ExternalProxyFixture.Pair("socks5", externalPort, authenticated: true);
        ExternalProxyFixture.Redirect(serverConfig, dns.Port);
        using var server = await XrayKit.Start(serverConfig, externalPort);
        var localPort = XrayKit.FreePort();
        using var client = await XrayKit.Start(ExternalProxyFixture.ClientConfig(definition, localPort), localPort);

        var answer = await ExternalProxyFixture.DatagramAsync(localPort, new IPEndPoint(IPAddress.Parse("192.0.2.53"), 53),
            ExternalProxyFixture.DnsQuery("proxy-dns.example.com"), TimeSpan.FromSeconds(5));

        answer.ShouldNotBeNull();
        (answer[2] & 0x80).ShouldBe(0x80);
        dns.Queries.ShouldBe(["proxy-dns.example.com"], "проверяется DNS самого приложения через UDP SOCKS5, а не DNS всей ОС");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task A_controlled_exit_probe_passes_the_destination_name_to_the_external_proxy(string kind)
    {
        RequireXray();
        await using var upstream = new ExternalProxyObserver(kind);
        var (definition, _) = ExternalProxyFixture.Pair(kind, upstream.Port, authenticated: true);
        var localPort = XrayKit.FreePort();
        var config = XrayConfigBuilder.BuildProbe([definition], [localPort], ConnectionSettings.Default).Config.ShouldNotBeNull();
        using var client = await XrayKit.Start(config, localPort);

        var exit = await LatencyProbe.ExitIpAsync(new ServerProbe(localPort, null,
            new Uri("http://proxy-target.invalid/trace"), TimeSpan.FromSeconds(5)), Ct);

        exit.ShouldBe("203.0.113.9");
        (await upstream.Target.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBe("proxy-target.invalid:80",
            "контрольное имя не разрешается локально; это проверка пути пробы, а не охвата DNS всей системы");
    }

    private static void RequireXray() => Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
}
