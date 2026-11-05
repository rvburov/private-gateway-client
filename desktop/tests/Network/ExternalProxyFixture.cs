using System.Buffers.Binary;
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

/// <summary>Внешний HTTP/SOCKS5-прокси на localhost: настоящее ядро, отдельный от локального входа клиента порт.</summary>
internal static class ExternalProxyFixture
{
    public const string Username = "proxy-test-user";
    public const string Password = XrayKit.Password;
    public const string ExitPage = "ip=203.0.113.9\n";

    public static (ServerDefinition Definition, JsonObject Config) Pair(string kind, int port, bool authenticated,
        string username = Username, string password = Password)
    {
        var socks = kind == "socks5";
        var tls = kind == "https";
        var settings = new JsonObject();
        if (socks)
        {
            settings["auth"] = authenticated ? "password" : "noauth";
            settings["udp"] = true;
            settings["ip"] = "127.0.0.1";
        }

        if (authenticated)
        {
            settings["accounts"] = new JsonArray(new JsonObject { ["user"] = username, ["pass"] = password });
        }

        var stream = new JsonObject { ["network"] = "raw" };
        if (tls)
        {
            stream["security"] = "tls";
            stream["tlsSettings"] = new JsonObject
            {
                ["certificates"] = new JsonArray(JsonNode.Parse(XrayKit.Certificate.Json)),
                // HTTP-вход Xray читает HTTP/1.1. Его TLS-дефолт h2 не соответствует этому приложению.
                ["alpn"] = new JsonArray("http/1.1"),
            };
        }

        var definition = XrayKit.Definition(socks ? ProxyProtocol.Socks5 : ProxyProtocol.Http, port,
            authenticated ? new ServerCredentials(Password: password, Username: username) : new ServerCredentials(),
            security: tls ? XrayKit.Pinned : SecurityOptions.None);
        var config = XrayKit.ServerConfig(port, socks ? "socks" : "http", settings.ToJsonString(), stream.ToJsonString());
        config["log"]!["loglevel"] = "info";
        return (definition, config);
    }

    public static JsonObject ClientConfig(ServerDefinition server, int port)
    {
        var request = new CoreRequest(server, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = port, Mux = true, CoreLog = CoreLogLevel.Info }, RoutingSettings.Default);
        var config = XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
        // Адреса тестового сайта — loopback. Убирается только запрет LAN: прямого выхода для сайта не добавляем.
        var rules = config["routing"]!["rules"]!.AsArray();
        foreach (var rule in rules.Where(r => r?["ip"] is JsonArray networks
            && networks.Any(network => network?.GetValue<string>() == "127.0.0.0/8")).ToArray())
        {
            rules.Remove(rule);
        }

        return config;
    }

    public static void Redirect(JsonObject config, int port) => config["outbounds"] = new JsonArray(new JsonObject
    {
        ["protocol"] = "freedom",
        ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{port}" },
    });

    public static async Task<string> GetAsync(Uri destination, int localPort, bool socks = false)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new AlwaysProxy(new Uri($"{(socks ? "socks5" : "http")}://127.0.0.1:{localPort}")),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return await http.GetStringAsync(destination, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>RFC 1928: UDP ASSOCIATE, управляющий TCP остается открытым до конца передачи.</summary>
    public static async Task<byte[]?> DatagramAsync(int localPort, IPEndPoint destination, byte[] payload, TimeSpan timeout)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(timeout);
        using var control = new TcpClient();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            await control.ConnectAsync(IPAddress.Loopback, localPort, stop.Token).ConfigureAwait(false);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, stop.Token).ConfigureAwait(false);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, stop.Token).ConfigureAwait(false);
            greeting.ShouldBe(new byte[] { 5, 0 });
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, stop.Token).ConfigureAwait(false);
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, stop.Token).ConfigureAwait(false);
            header[0].ShouldBe((byte)5);
            if (header[1] != 0)
            {
                return null;
            }

            var address = header[3] switch
            {
                1 => await ReadAsync(stream, 4, stop.Token).ConfigureAwait(false),
                4 => await ReadAsync(stream, 16, stop.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException("UDP ASSOCIATE вернул нечисловой адрес стенда"),
            };
            var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2, stop.Token).ConfigureAwait(false));
            var relayAddress = new IPAddress(address);
            if (IPAddress.Any.Equals(relayAddress))
            {
                relayAddress = IPAddress.Loopback;
            }

            port.ShouldNotBe((ushort)0, "датаграммы идут на порт из ответа UDP ASSOCIATE, не на догадку о локальном порте");
            var target = destination.Address.GetAddressBytes();
            target.Length.ShouldBe(4);
            var packet = new byte[10 + payload.Length];
            packet[3] = 1;
            target.CopyTo(packet, 4);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), checked((ushort)destination.Port));
            payload.CopyTo(packet, 10);
            await udp.SendAsync(packet, new IPEndPoint(relayAddress, port), stop.Token).ConfigureAwait(false);
            var response = (await udp.ReceiveAsync(stop.Token).ConfigureAwait(false)).Buffer;
            response.Length.ShouldBeGreaterThanOrEqualTo(10);
            response[2].ShouldBe((byte)0, "фрагментированные ответы стенд не использует");
            var start = response[3] switch { 1 => 10, 4 => 22, 3 => 7 + response[4], _ => -1 };
            start.ShouldBeGreaterThan(0);
            response.Length.ShouldBeGreaterThanOrEqualTo(start);
            return response[start..];
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public static byte[] DnsQuery(string name)
    {
        var result = new List<byte> { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var part in name.Split('.'))
        {
            result.Add(checked((byte)part.Length));
            result.AddRange(Encoding.ASCII.GetBytes(part));
        }

        result.AddRange([0, 0, 1, 0, 1]);
        return [.. result];
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private sealed class AlwaysProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => false;
    }
}
