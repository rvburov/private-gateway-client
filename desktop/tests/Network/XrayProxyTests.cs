using System.Net;
using System.Net.Sockets;
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
    private const string Page = "привет через туннель";

    private static readonly string? Xray = FindXray();

    [Fact]
    public async Task Request_goes_through_a_vless_server_by_socks5_and_http_on_one_port()
    {
        Assert.SkipWhen(Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite(Page);
        var serverPort = FreePort();
        using var server = await Start(ServerConfig(serverPort), serverPort);
        var proxyPort = FreePort();
        var config = ClientConfig(serverPort, proxyPort);
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
        using var server = await Start(ServerConfig(serverPort), serverPort);
        var proxyPort = FreePort();
        using var client = await Start(ClientConfig(serverPort, proxyPort), proxyPort);

        await Should.ThrowAsync<Exception>(() => Get(site.Url, $"socks5://127.0.0.1:{proxyPort}"));

        site.Requests.ShouldBe(0, "geoip:private — блок: до сайта в локальной сети запрос не дошел");
    }

    [Fact]
    public void Missing_core_is_an_error_not_a_silent_start()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-xray-" + Guid.NewGuid().ToString("N"), "xray");

        Should.Throw<System.ComponentModel.Win32Exception>(() => XrayProcess.Start(missing, new JsonObject()));
    }

    private static JsonObject ClientConfig(int serverPort, int proxyPort)
    {
        var server = new ServerDefinition("Тест", string.Empty, ProxyProtocol.Vless, "127.0.0.1", serverPort,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials(Uuid: Uuid));
        var request = new CoreRequest(server, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort, CoreLog = CoreLogLevel.Info }, RoutingSettings.Default);
        return XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
    }

    /// <summary>Сервер VLESS без шифрования транспорта — только для теста на localhost.</summary>
    private static JsonObject ServerConfig(int port) => JsonNode.Parse($$"""
        {
          "log": { "loglevel": "warning", "access": "none" },
          "inbounds": [{
            "listen": "127.0.0.1", "port": {{port}}, "protocol": "vless",
            "settings": { "clients": [{ "id": "{{Uuid}}" }], "decryption": "none" },
            "streamSettings": { "network": "raw", "security": "none" }
          }],
          "outbounds": [{ "protocol": "freedom" }]
        }
        """)!.AsObject();

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
