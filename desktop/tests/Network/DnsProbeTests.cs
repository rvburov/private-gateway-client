using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Запрос DNS через карту мимо туннеля (<see cref="DnsProbe"/>) — на петле машины, на каждой ОС: привязка сокета к карте этой ОС
/// работает, ответ на свой запрос узнается, резолвер не узнает ни одного имени; карты нет или тишина — «закрыто».
/// </summary>
public sealed class DnsProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Петля — карта, которая есть на каждой машине: lo, lo0, Loopback Pseudo-Interface 1.
    private static string Loopback => NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback).Name;

    [Fact]
    public async Task Query_through_the_card_gets_its_answer_and_names_nothing()
    {
        using var dns = new TestDns(IPAddress.Parse("192.0.2.53"));

        (await DnsProbe.PastTunnelAsync(new IPEndPoint(IPAddress.Loopback, dns.Port), Loopback, TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        dns.Queries.ShouldBe([string.Empty], "спрошен только корень — ни одного имени");
    }

    [Fact]
    public async Task Card_that_is_not_there_and_silence_mean_closed()
    {
        using var dns = new TestDns(IPAddress.Parse("192.0.2.53"));

        (await DnsProbe.PastTunnelAsync(new IPEndPoint(IPAddress.Loopback, dns.Port), "pg-no-such0", TimeSpan.FromSeconds(2), Ct))
            .ShouldBeFalse("к карте, которой нет, сокет не привязать — запрос не уходит");
        dns.Queries.ShouldBeEmpty();
        (await DnsProbe.PastTunnelAsync(new IPEndPoint(IPAddress.Loopback, LocalPort.FreeForTcpAndUdp()), Loopback, TimeSpan.FromSeconds(1), Ct))
            .ShouldBeFalse("резолвер не ответил — пути нет");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Only_the_answer_to_its_own_query_counts(bool sameNumber, bool answerFlag)
    {
        // Пакет на порт запроса — не ответ: свой запрос, вернувшийся эхом, или ответ на чужой номер.
        using var other = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            var got = await other.ReceiveAsync(Ct);
            var packet = got.Buffer;
            packet[0] ^= sameNumber ? (byte)0 : (byte)0xFF;
            packet[2] = answerFlag ? (byte)(packet[2] | 0x80) : packet[2];
            await other.SendAsync(packet, got.RemoteEndPoint, Ct);
        }, Ct);

        (await DnsProbe.PastTunnelAsync((IPEndPoint)other.Client.LocalEndPoint!, Loopback, TimeSpan.FromSeconds(1), Ct)).ShouldBeFalse();
    }
}
