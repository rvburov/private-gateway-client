using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// До и после VPN (этап 7): разбор ответов STUN (RFC 5389), замер STUN своим сервером на 127.0.0.1, адаптер выхода в
/// интернет, сведения ОС в одну запись и сравнение через IPC. Из машины ничего не уходит: STUN и проверочные адреса — свои.
/// </summary>
public sealed class CompareTests
{
    private static readonly byte[] Transaction = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Stun_answer_gives_the_xor_mapped_address_of_this_request_only()
    {
        StunProbe.Parse(Answer(0x0101, Transaction, Xor(new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112), Transaction)), Transaction)
            .ShouldBe(new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112));
        StunProbe.Parse(Answer(0x0101, Transaction, Xor(new IPEndPoint(IPAddress.Parse("2001:db8::24"), 40112), Transaction)), Transaction)
            .ShouldBe(new IPEndPoint(IPAddress.Parse("2001:db8::24"), 40112), "IPv6 смешан с cookie и номером запроса");
        StunProbe.Parse(Answer(0x0101, Transaction, Plain(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 5000))), Transaction)
            .ShouldBe(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 5000), "старый сервер — MAPPED-ADDRESS без XOR");
        StunProbe.Parse(Answer(0x0101, Transaction, [.. Plain(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 5000)),
                .. Xor(new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112), Transaction)]), Transaction)
            .ShouldBe(new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112), "XOR важнее простого: простой переписывают роутеры");

        var answer = Answer(0x0101, Transaction, Xor(new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112), Transaction));
        StunProbe.Parse(answer, [.. Transaction.Reverse()]).ShouldBeNull("ответ на чужой запрос");
        StunProbe.Parse(Answer(0x0111, Transaction, []), Transaction).ShouldBeNull("ошибка Binding — не адрес");
        StunProbe.Parse(answer[..19], Transaction).ShouldBeNull("короче заголовка");
        StunProbe.Parse(answer[..26], Transaction).ShouldBeNull("атрибут обрезан");
        StunProbe.Request(Transaction).ShouldBe([0, 1, 0, 0, 0x21, 0x12, 0xA4, 0x42, .. Transaction], "Binding без атрибутов");
    }

    [Fact]
    public async Task Stun_server_names_the_address_it_sees_and_silence_is_null()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serve = Task.Run(async () =>
        {
            // Первый запрос теряется, как бывает с UDP: ответ — на повтор.
            await server.ReceiveAsync(Ct);
            var request = await server.ReceiveAsync(Ct);
            await server.SendAsync(Answer(0x0101, request.Buffer[8..20], Xor(request.RemoteEndPoint, request.Buffer[8..20])), request.RemoteEndPoint, Ct);
        }, Ct);

        var mapped = await StunProbe.MappedAsync((IPEndPoint)server.Client.LocalEndPoint!, TimeSpan.FromSeconds(3), Ct);

        mapped.ShouldNotBeNull().Address.ShouldBe(IPAddress.Loopback);
        mapped.Port.ShouldBeGreaterThan(0);
        await serve;

        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        (await StunProbe.MappedAsync((IPEndPoint)silent.Client.LocalEndPoint!, TimeSpan.FromMilliseconds(600), Ct)).ShouldBeNull();
    }

    [Fact]
    public void Facts_name_the_adapter_that_leads_to_the_internet_with_its_gateway_and_dns()
    {
        var router = IPAddress.Parse("192.0.2.1");
        var snapshot = new NetworkSnapshot(DateTimeOffset.UnixEpoch,
        [
            new NetworkCard("Wi-Fi", string.Empty, AdapterKind.Wifi, true, null, null, null, [new(IPAddress.Parse("192.0.2.23"), 24)], [router], [router], null),
            new NetworkCard("Private Gateway", string.Empty, AdapterKind.Tunnel, true, null, null, null, [new(IPAddress.Parse("172.19.0.1"), 30)], [],
                [IPAddress.Parse("172.19.0.2")], null),
        ], []);
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var webrtc = new IPEndPoint(IPAddress.Parse("203.0.113.24"), 40112);

        Gateway.Facts(at, snapshot, IPAddress.Parse("192.0.2.23"), "198.51.100.7", null, null)
            .ShouldBe(new NetworkFacts(at, "198.51.100.7", null, null, "Wi-Fi", router, snapshot.Cards[0].DnsServers));
        var tunnel = Gateway.Facts(at, snapshot, IPAddress.Parse("172.19.0.1"), "203.0.113.24", null, webrtc);
        (tunnel.Route, tunnel.Gateway, tunnel.Webrtc).ShouldBe(("Private Gateway", (IPAddress?)null, webrtc));
        tunnel.Dns.ShouldBe([IPAddress.Parse("172.19.0.2")]);
        Gateway.Facts(at, snapshot, null, null, null, null).Route.ShouldBeNull("нет сети — нет и адаптера");
    }

    [Fact]
    public void This_machine_has_a_way_to_the_internet_through_one_of_its_adapters()
    {
        var egress = Gateway.EgressAddress(IPAddress.Parse("1.1.1.1"));

        egress.ShouldNotBeNull("у машины CI есть маршрут в интернет — UDP соединяется без пакетов");
        IPAddress.IsLoopback(egress).ShouldBeFalse();
    }

    [Fact]
    public void Comparison_goes_through_the_contract_and_comes_back_the_same()
    {
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var before = new NetworkFacts(at, "198.51.100.7", "2001:db8:1::7", new IPEndPoint(IPAddress.Parse("198.51.100.7"), 51820), "Wi-Fi",
            IPAddress.Parse("192.0.2.1"), [IPAddress.Parse("192.0.2.1")]);
        var now = new NetworkFacts(at.AddMinutes(5), "203.0.113.24", null, null, null, null, []);

        var back = new NetworkComparison(before, now).ToContract().ToCore();

        (back.Before! with { Dns = [] }).ShouldBe(before with { Dns = [] });
        back.Before!.Dns.ShouldBe(before.Dns);
        (back.Now with { Dns = [] }).ShouldBe(now with { Dns = [] });
        new NetworkComparison(null, now).ToContract().ToCore().Before.ShouldBeNull();
        Should.Throw<ArgumentException>(() => new V1.NetworkComparison().ToCore());
    }

    [Fact]
    public async Task Window_gets_the_comparison_from_the_service()
    {
        using var direct = new LocalSite("fl=1\nip=198.51.100.7\nts=1\n");
        using var direct6 = new LocalSite("fl=1\nip=2001:db8:1::7\nts=1\n");
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            Trace = direct.Url,
            Trace6 = direct6.Url,
            NetworkDetails = ServiceHost.NetworkDetails(),
            Webrtc = _ => Task.FromResult<IPEndPoint?>(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 51820)),
        });
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);
        await client.MeasureIpAsync(Ct);

        var comparison = (await client.CompareNetworkAsync(Ct)).ShouldNotBeNull();

        comparison.Before.ShouldBeNull("защиту не включали — снимка «до» нет");
        comparison.Now.Ipv4.ShouldBe("198.51.100.7", "защита выключена — адрес реальный");
        comparison.Now.Ipv6.ShouldBe("2001:db8:1::7");
        comparison.Now.Webrtc.ShouldBe(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 51820));
        comparison.Now.Route.ShouldNotBeNull("адаптер выхода в интернет этой машины");
    }

    // Ответ STUN: заголовок с типом, длиной, cookie и номером запроса, потом атрибуты.
    private static byte[] Answer(ushort type, byte[] transaction, byte[] attributes)
    {
        var answer = new byte[20 + attributes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(answer, type);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), (ushort)attributes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(4), 0x2112A442);
        transaction.CopyTo(answer, 8);
        attributes.CopyTo(answer, 20);
        return answer;
    }

    private static byte[] Plain(IPEndPoint endpoint) => Attribute(0x0001, endpoint, null);

    private static byte[] Xor(IPEndPoint endpoint, byte[] transaction) => Attribute(0x0020, endpoint, transaction);

    private static byte[] Attribute(ushort type, IPEndPoint endpoint, byte[]? transaction)
    {
        var address = endpoint.Address.GetAddressBytes();
        var value = new byte[4 + address.Length];
        value[1] = (byte)(address.Length == 4 ? 1 : 2);
        var port = (ushort)endpoint.Port;
        if (transaction is not null)
        {
            port ^= 0x2112;
            byte[] mask = [0x21, 0x12, 0xA4, 0x42, .. transaction];
            for (var i = 0; i < address.Length; i++)
            {
                address[i] ^= mask[i];
            }
        }

        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), port);
        address.CopyTo(value, 4);
        var attribute = new byte[4 + ((value.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt16BigEndian(attribute, type);
        BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(2), (ushort)value.Length);
        value.CopyTo(attribute, 4);
        return attribute;
    }
}
