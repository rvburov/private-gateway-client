using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class PrivateGatewayCompatibilityTests
{
    /// <summary>
    /// Vision распознает TLS полезной нагрузки и меняет способ копирования: простой HTTP этого пути не проверяет.
    /// В VPN проверяются настоящий парольный вход пробы и вход Shadowsocks, которым пользуется TUN; ОС не меняется.
    /// </summary>
    [Theory]
    [InlineData(TransportKind.Tcp, false, false, SslProtocols.None)]
    [InlineData(TransportKind.Tcp, false, true, SslProtocols.None)]
    [InlineData(TransportKind.Tcp, true, false, SslProtocols.None)]
    [InlineData(TransportKind.Tcp, true, true, SslProtocols.None)]
    [InlineData(TransportKind.Xhttp, false, false, SslProtocols.None)]
    [InlineData(TransportKind.Xhttp, false, true, SslProtocols.None)]
    [InlineData(TransportKind.Xhttp, true, false, SslProtocols.None)]
    [InlineData(TransportKind.Xhttp, true, true, SslProtocols.None)]
    [InlineData(TransportKind.Tcp, true, false, SslProtocols.Tls12)]
    [InlineData(TransportKind.Tcp, true, true, SslProtocols.Tls12)]
    public async Task Device_profile_carries_https_through_proxy_and_vpn_inputs(TransportKind transport, bool vpn, bool fragment, SslProtocols tls)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var splice = Environment.GetEnvironmentVariable("xray.buf.splice");
        var spliceAlias = Environment.GetEnvironmentVariable("XRAY_BUF_SPLICE");
        var page = string.Concat(Enumerable.Repeat("TLS через Vision; ", 4096));
        await using var site = new HttpsSite(page);
        var url = site.Url;
        using (var directHandler = PinnedHandler(null, site.Pin, tls))
        using (var direct = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(15) })
        {
            (await direct.GetStringAsync(url, TestContext.Current.CancellationToken)).ShouldBe(page,
                "контрольный HTTPS-сайт обязан отвечать сам, до проверки VPN");
        }

        var maskPort = XrayKit.FreePort();
        using var mask = await XrayKit.Start(XrayKit.Pair("trojan-tls", maskPort).Config, maskPort);
        var definition = LinkParser.Parse(File.ReadAllText(Path.Combine(XrayKit.Root, "shared", "testdata", "links", "vless", "private-gateway.txt")))
            .Server.ShouldNotBeNull();
        var serverPort = XrayKit.FreePort();
        var serverConfig = ServerConfig(serverPort, definition.Credentials.Uuid!, $"127.0.0.1:{maskPort}", definition.Security.RealityShortId!);
        if (transport == TransportKind.Xhttp)
        {
            serverConfig["inbounds"]![0]!["settings"]!["clients"]![0]!.AsObject().Remove("flow");
            var stream = serverConfig["inbounds"]![0]!["streamSettings"]!;
            stream["network"] = "xhttp";
            stream["xhttpSettings"] = new JsonObject { ["path"] = "/pg" };
            definition = definition with
            {
                Transport = new TransportOptions(TransportKind.Xhttp, Path: "/pg", Mode: "stream-one"),
                Options = definition.Options with { Flow = null },
            };
        }

        serverConfig["log"]!["loglevel"] = "info";
        using var server = await XrayKit.Start(serverConfig, serverPort);
        definition = definition with
        {
            Host = "127.0.0.1",
            Port = serverPort,
            Security = definition.Security with { RealityPublicKey = PublicKey(RealityPrivate) },
        };

        var proxyPort = XrayKit.FreePort();
        var tun = vpn ? new TunLink(XrayKit.FreePort(), "test-vpn-password", "127.0.0.1") : null;
        var probe = vpn ? new ProbeLink(XrayKit.FreePort(), "test-probe-user", "test-probe-password") : null;
        var request = new CoreRequest(definition, vpn ? ConnectionMode.Vpn : ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort, TlsFragment = fragment, CoreLog = CoreLogLevel.Info }, RoutingSettings.Default);
        var config = XrayConfigBuilder.Build(request, tun, probe).Config.ShouldNotBeNull();
        // Сайт теста на loopback: убирается только общее правило частных сетей, остальные правила VPN остаются.
        var rules = config["routing"]!["rules"]!.AsArray();
        rules.Remove(rules.Single(r => r?["ip"] is JsonArray addresses && addresses.Any(a => a?.GetValue<string>() == "127.0.0.0/8")));
        using var client = await XrayKit.Start(config, tun?.Port ?? proxyPort);

        var paths = new List<(int Port, NetworkCredential? Credentials)> { (probe?.Port ?? proxyPort, probe is null ? null : new(probe.User, probe.Password)) };
        using var relay = tun is null ? null : await XrayKit.Start(ShadowsocksInput(tun, proxyPort), proxyPort);
        if (tun is not null)
        {
            paths.Add((proxyPort, null));
        }

        foreach (var (port, credentials) in paths)
        {
            using var handler = PinnedHandler(new FixedProxy(new Uri($"socks5://127.0.0.1:{port}")) { Credentials = credentials }, site.Pin, tls);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                (await http.GetStringAsync(url, TestContext.Current.CancellationToken)).ShouldBe(page);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // Ретранслятор Shadowsocks — свой процесс между тестом и входом TUN: на macOS CI 2026-11-10 дважды соединение
                // через него закрывалось сразу после передачи запроса (клиент видел EOF от ретранслятора), а его журнала в
                // сообщении не было.
                var relayLog = relay is null ? string.Empty : $"\nретранслятор:\n{XrayKit.Log(relay)}";
                throw new InvalidOperationException($"transport={transport}, VPN={vpn}, fragment={fragment}, TLS={tls}, вход={port}\nклиент:\n{XrayKit.Log(client)}\nсервер:\n{XrayKit.Log(server)}{relayLog}", error);
            }
        }

        site.Requests.ShouldBe(paths.Count + 1, "после контрольного запроса HTTPS прошел через заданные входы");
        if (tls == SslProtocols.Tls12)
        {
            site.Protocols.ShouldAllBe(protocol => protocol == SslProtocols.Tls12, "Windows 10 проверяет связь через TLS 1.2");
        }
        else if (OperatingSystem.IsLinux())
        {
            site.Protocols.ShouldAllBe(protocol => protocol == SslProtocols.Tls13,
                "регрессия Linux splice обязана пройти настоящий TLS 1.3, а не запасной TLS 1.2");
        }
        else
        {
            site.Protocols.ShouldAllBe(protocol => protocol == SslProtocols.Tls12 || protocol == SslProtocols.Tls13);
        }

        Environment.GetEnvironmentVariable("xray.buf.splice").ShouldBe(splice, "настройка копирования принадлежит дочернему ядру");
        Environment.GetEnvironmentVariable("XRAY_BUF_SPLICE").ShouldBe(spliceAlias);
    }

    private static SocketsHttpHandler PinnedHandler(IWebProxy? proxy, string pin, SslProtocols tls) => new()
    {
        Proxy = proxy,
        UseProxy = proxy is not null,
        SslOptions = new()
        {
            EnabledSslProtocols = tls,
            // Заведомо свой самоподписанный сайт: доверяем только его точному сертификату, не любому TLS.
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256)
                .Equals(pin, StringComparison.OrdinalIgnoreCase) == true,
        },
    };

    /// <summary>Парольный вход Xray режима VPN — как для TUN, без прав на адаптер ОС.</summary>
    private static JsonObject ShadowsocksInput(TunLink tun, int port) => new()
    {
        ["log"] = new JsonObject { ["loglevel"] = "info", ["access"] = "none" },
        ["inbounds"] = new JsonArray(new JsonObject
        {
            ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth" },
        }),
        ["outbounds"] = new JsonArray(new JsonObject
        {
            ["protocol"] = "shadowsocks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["address"] = "127.0.0.1", ["port"] = tun.Port, ["method"] = TunLink.Method, ["password"] = tun.Password,
                }),
            },
        }),
    };

    /// <summary>Контрольный HTTPS-сайт с точным сертификатом; закрытие ожидает все его соединения.</summary>
    private sealed class HttpsSite : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _response;
        private readonly Task _accepting;
        private readonly List<Task> _connections = [];
        private readonly List<SslProtocols> _protocols = [];
        private int _requests;

        public HttpsSite(string page)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
            var body = Encoding.UTF8.GetBytes(page);
            _response = [.. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
            _listener.Start();
            _accepting = ServeAsync();
        }

        public Uri Url => new($"https://{(IPEndPoint)_listener.LocalEndpoint}/");

        public string Pin => _certificate.GetCertHashString(HashAlgorithmName.SHA256);

        public int Requests => Volatile.Read(ref _requests);

        public IReadOnlyList<SslProtocols> Protocols
        {
            get
            {
                lock (_protocols)
                {
                    return [.. _protocols];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    _connections.Add(AnswerAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Сайт закрыт.
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            await using (var stream = new SslStream(client.GetStream()))
            {
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                    using var headers = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await headers.ReadLineAsync(_stop.Token) is { Length: > 0 })
                    {
                    }

                    lock (_protocols)
                    {
                        _protocols.Add(stream.SslProtocol);
                    }

                    Interlocked.Increment(ref _requests);
                    await stream.WriteAsync(_response, _stop.Token);
                }
                catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException)
                {
                    // Клиент оборвал соединение или тест остановился.
                }
            }
        }
    }
}
