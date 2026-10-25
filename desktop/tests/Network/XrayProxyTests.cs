using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Контролируемый прокси-тест (этап 2): настоящее ядро Xray с конфигом от <see cref="XrayConfigBuilder"/>,
/// свой сервер VLESS и свой сайт — все на 127.0.0.1, без интернета и без прав администратора.
/// </summary>
public sealed class XrayProxyTests
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";
    private const string Password = "not-a-real-password";
    private const string Page = "привет через туннель";

    private static readonly string? Xray = FindXray();

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
        Assert.SkipWhen(Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = FreePort();
        var (definition, serverConfig) = Pair(kind, serverPort);
        using var server = await Start(serverConfig, serverPort);
        var proxyPort = FreePort();
        var config = ClientConfig(definition, proxyPort);
        // Сайт теста — на 127.0.0.1, а локальная сеть в конфиге закрыта: для проверки туннеля правило убирается.
        config["routing"]!["rules"]!.AsArray().RemoveAt(0);
        using var client = await Start(config, proxyPort);

        (await Get(site.Url, $"socks5://127.0.0.1:{proxyPort}")).ShouldBe(Page);
        (await Get(site.Url, $"http://127.0.0.1:{proxyPort}")).ShouldBe(Page, "HTTP и SOCKS5 — на одном порту");
        site.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task Local_network_stays_closed_through_the_proxy_by_default()
    {
        Assert.SkipWhen(Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = FreePort();
        var (definition, serverConfig) = Pair("vless", serverPort);
        using var server = await Start(serverConfig, serverPort);
        var proxyPort = FreePort();
        using var client = await Start(ClientConfig(definition, proxyPort), proxyPort);

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

    /// <summary>
    /// Описание сервера для клиента и конфиг того же сервера в Xray. Без шифрования транспорта — только
    /// для теста на localhost; у Trojan — TLS с самоподписанным сертификатом на example.com.
    /// </summary>
    private static (ServerDefinition Server, JsonObject Config) Pair(string kind, int port)
    {
        var withUuid = new ServerCredentials(Uuid: Uuid);
        var vlessClients = $$"""{ "clients": [{ "id": "{{Uuid}}" }], "decryption": "none" }""";
        const string raw = """{ "network": "raw" }""";
        return kind switch
        {
            "vless" => (Definition(ProxyProtocol.Vless, port, withUuid), ServerConfig(port, "vless", vlessClients, raw)),
            "vless-ws" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.WebSocket, Path: "/pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "ws", "wsSettings": { "path": "/pg" } }""")),
            "vless-grpc" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.Grpc, ServiceName: "pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "grpc", "grpcSettings": { "serviceName": "pg" } }""")),
            "vless-httpupgrade" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.HttpUpgrade, Path: "/pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "httpupgrade", "httpupgradeSettings": { "path": "/pg" } }""")),
            "vmess" => (Definition(ProxyProtocol.Vmess, port, withUuid),
                ServerConfig(port, "vmess", $$"""{ "clients": [{ "id": "{{Uuid}}" }] }""", raw)),
            "trojan-tls" => (Definition(ProxyProtocol.Trojan, port, new ServerCredentials(Password: Password),
                    security: new SecurityOptions(TransportSecurity.Tls, Sni: "example.com", AllowInsecure: true)),
                ServerConfig(port, "trojan", $$"""{ "clients": [{ "password": "{{Password}}" }] }""",
                    $$"""{ "network": "raw", "security": "tls", "tlsSettings": { "certificates": [{{SelfSigned("example.com")}}] } }""")),
            "shadowsocks" => (Definition(ProxyProtocol.Shadowsocks, port, new ServerCredentials(Password: Password),
                    options: new ProtocolOptions(Cipher: "aes-256-gcm")),
                ServerConfig(port, "shadowsocks", $$"""{ "method": "aes-256-gcm", "password": "{{Password}}", "network": "tcp" }""", raw)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static ServerDefinition Definition(ProxyProtocol protocol, int port, ServerCredentials credentials,
        TransportOptions? transport = null, SecurityOptions? security = null, ProtocolOptions? options = null) =>
        new("Тест", string.Empty, protocol, "127.0.0.1", port, transport ?? TransportOptions.Tcp, security ?? SecurityOptions.None,
            options ?? ProtocolOptions.Empty, credentials);

    private static JsonObject ServerConfig(int port, string protocol, string settings, string stream) => JsonNode.Parse($$"""
        {
          "log": { "loglevel": "warning", "access": "none" },
          "inbounds": [{ "listen": "127.0.0.1", "port": {{port}}, "protocol": "{{protocol}}", "settings": {{settings}}, "streamSettings": {{stream}} }],
          "outbounds": [{ "protocol": "freedom" }]
        }
        """)!.AsObject();

    /// <summary>Самоподписанный сертификат на сутки — строками PEM, как их принимает Xray.</summary>
    private static string SelfSigned(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new JsonObject
        {
            ["certificate"] = Lines(certificate.ExportCertificatePem()),
            ["key"] = Lines(key.ExportPkcs8PrivateKeyPem()),
        }.ToJsonString();

        static JsonArray Lines(string pem) => new([.. pem.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonValue.Create(l.TrimEnd('\r')))]);
    }

    private static async Task<XrayProcess> Start(JsonObject config, int port)
    {
        var output = new List<string>();
        var xray = XrayProcess.Start(Xray!, config);
        xray.Output += (_, line) =>
        {
            lock (output)
            {
                output.Add(line);
            }
        };

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (xray.HasExited)
            {
                break;
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return xray;
            }
            catch (SocketException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        xray.Dispose();
        lock (output)
        {
            throw new InvalidOperationException("Ядро не открыло порт " + port + ":\n" + string.Join('\n', output));
        }
    }

    private static async Task<string> Get(Uri url, string proxy)
    {
        using var handler = new SocketsHttpHandler { Proxy = new AlwaysProxy(new Uri(proxy)), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return await http.GetStringAsync(url).ConfigureAwait(false);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindXray()
    {
        if (Environment.GetEnvironmentVariable("PG_XRAY") is { Length: > 0 } path && File.Exists(path))
        {
            return path;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                var xray = Path.Combine(dir.FullName, "artifacts", "cores", "xray", OperatingSystem.IsWindows() ? "xray.exe" : "xray");
                return File.Exists(xray) ? xray : null;
            }
        }

        return null;
    }

    /// <summary>Прокси для всех адресов: WebProxy сам пропускает localhost мимо прокси, а тесту нужен именно он.</summary>
    private sealed class AlwaysProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }

    /// <summary>Сайт на 127.0.0.1: на любой запрос отвечает одной страницей и считает запросы.</summary>
    private sealed class LocalSite : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _response;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public LocalSite(string page)
        {
            var body = Encoding.UTF8.GetBytes(page);
            _response = [.. Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var received = new StringBuilder();
                    while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Interlocked.Increment(ref _requests);
                    await stream.WriteAsync(_response).ConfigureAwait(false);
                }
            }
        }
    }
}
