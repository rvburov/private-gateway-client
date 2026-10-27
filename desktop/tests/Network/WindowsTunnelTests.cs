using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// TUN sing-box на настоящей Windows (CI — от администратора): адаптер «Private Gateway» с адресом DNS туннеля, путь
/// TUN → Xray → свой сервер Xray → сайт и DNS теста. Конфиги ядер — те, что собирает служба, с одним отличием: в TUN идут
/// только адреса теста (публичный 1.1.1.1 и пул FakeDNS), а не вся сеть, и без строгих маршрутов (на Windows это фильтры
/// DNS и IPv6 на всю машину) — машина CI остается в сети. Сервер теста отвечает своим сайтом и своим DNS: из машины
/// ничего не уходит. Блок при обрыве (WFP) проверяют свои тесты; тесты идут по одному, без соседей.
/// </summary>
[Collection(FirewallSerial.Name)]
public sealed class WindowsTunnelTests
{
    private static readonly IPAddress Public = IPAddress.Parse("1.1.1.1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_gets_the_tunnel_dns_and_carries_traffic_through_xray_and_the_server(bool fakeDns)
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        using var site = new LocalSite("через туннель Windows");
        using var dns = new TestDns(IPAddress.Parse("203.0.113.7"));
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        // На сайт теста — только его имя и адрес. С FakeDNS адреса из пула получает и сама Windows (DNS адаптера —
        // туннель): ее соединения (WNS, проверка сети) тоже приходят на сервер теста — им блок, а не сайт теста.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dns.Port}" } });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" },
                new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:example.com"), ["outboundTag"] = "site" },
                new JsonObject { ["type"] = "field", ["ip"] = new JsonArray(Public + "/32"), ["outboundTag"] = "site" }),
        };

        using var server = await XrayKit.Start(serverConfig, serverPort);
        // DNS Xray — обычный, по UDP через сервер: на сервере теста его отвечает свой DNS.
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8", FakeDns = fakeDns },
            ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug }, RoutingSettings.Default);
        var output = new CoreOutput();
        var link = VpnTunnel.NewLink(IPAddress.Loopback);
        var xrayConfig = XrayConfigBuilder.Build(request, link).Config.ShouldNotBeNull();
        using var xray = CoreProcess.StartXray(XrayKit.Xray!, xrayConfig, output: line => output.Add("xray", line));
        await WaitAsync(() => Listens(link.Port), "вход Xray", output);

        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(link, [IPAddress.Loopback], request.Protection, request.Connection, new TunIpv6(Kernel: true, Tunnel: false)));
        var inbound = tun["inbounds"]![0]!.AsObject();
        inbound["route_address"] = new JsonArray(Public + "/32", XrayConfigBuilder.FakeDnsPool);
        inbound["strict_route"] = false;
        var started = 0;
        using var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, output: line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started, 1);
            }

            output.Add("sing-box", line);
        });
        try
        {
            await WaitAsync(() => Volatile.Read(ref started) == 1 && Adapter() is not null, "sing-box и адаптер", output);
            var properties = Adapter()!.GetIPProperties();
            properties.UnicastAddresses.Select(a => a.Address).ShouldContain(IPAddress.Parse(SingBoxConfigBuilder.TunAddress.Split('/')[0]));
            properties.DnsAddresses.ShouldContain(IPAddress.Parse(SingBoxConfigBuilder.TunDns), "DNS адаптера — адрес DNS туннеля, его спрашивает Windows");

            // Запрос DNS к адресу DNS туннеля — как его шлет DNS ОС через адаптер: отвечает Xray.
            var answer = await output.TraceAsync(() => ResolveAsync(new IPEndPoint(IPAddress.Parse(SingBoxConfigBuilder.TunDns), 53), "example.com"));
            Uri page;
            if (fakeDns)
            {
                InPool(answer).ShouldBeTrue($"с FakeDNS адрес из пула {XrayConfigBuilder.FakeDnsPool}, а не {answer}");
                page = new Uri($"http://{answer}/");
            }
            else
            {
                answer.ShouldBe(IPAddress.Parse("203.0.113.7"), "ответ DNS теста за сервером, а не DNS провайдера");
                dns.Queries.ShouldContain("example.com");
                page = new Uri($"http://{Public}/");
            }

            (await output.TraceAsync(() => GetAsync(page))).ShouldBe("через туннель Windows", "адрес — через TUN, Xray и сервер");
            site.Requests.ShouldBe(1);
        }
        finally
        {
            await singBox.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await xray.StopAsync(cancellationToken: CancellationToken.None);
        }

        await WaitAsync(() => Adapter() is null, "адаптер убран вместе с sing-box", output);
    }

    private static NetworkInterface? Adapter() =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == SingBoxConfigBuilder.InterfaceName && n.OperationalStatus == OperationalStatus.Up);

    private static bool Listens(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool InPool(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static async Task WaitAsync(Func<bool> ready, string what, CoreOutput output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline)
            {
                await output.TraceAsync<bool>(() => throw new InvalidOperationException("Не дождались: " + what));
            }

            await Task.Delay(100, Ct);
        }
    }

    private static async Task<string> GetAsync(Uri page)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            return await http.GetStringAsync(page, Ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"{page}: {e.Message}", e);
        }
    }

    /// <summary>Запрос A по UDP к <paramref name="server"/>: первый адрес ответа; нет ответа или адреса — исключение.</summary>
    private static async Task<IPAddress> ResolveAsync(IPEndPoint server, string name)
    {
        var query = new List<byte> { 0x50, 0x47, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }

        query.AddRange([0, 0, 1, 0, 1]);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        byte[] reply;
        try
        {
            await udp.SendAsync(query.ToArray(), server, wait.Token);
            reply = (await udp.ReceiveAsync(wait.Token)).Buffer;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !Ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"DNS {server} не ответил на {name}: {e.Message}", e);
        }

        // Заголовок, вопрос (имя, тип, класс), затем ответы: имя (ссылка или метки), тип, класс, TTL, длина, данные.
        var answers = (reply[6] << 8) | reply[7];
        var at = SkipName(reply, 12) + 4;
        for (var i = 0; i < answers; i++)
        {
            at = SkipName(reply, at);
            var type = (reply[at] << 8) | reply[at + 1];
            var length = (reply[at + 8] << 8) | reply[at + 9];
            at += 10;
            if (type == 1 && length == 4)
            {
                return new IPAddress(reply.AsSpan(at, 4));
            }

            at += length;
        }

        throw new InvalidOperationException($"DNS {server} ответил на {name} без адреса (ответов: {answers})");
    }

    private static int SkipName(byte[] message, int at)
    {
        while (message[at] != 0)
        {
            if ((message[at] & 0xC0) == 0xC0)
            {
                return at + 2;
            }

            at += message[at] + 1;
        }

        return at + 1;
    }
}
