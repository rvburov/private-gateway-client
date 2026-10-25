using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Порт входа ядра: свободен и для TCP, и для UDP. На Windows с занятыми диапазонами (Hyper-V, WinNAT)
/// порт, который ОС дает одному протоколу, у другого бывает закрыт — CI это ловит.
/// </summary>
public sealed class LocalPortTests
{
    [Fact]
    public void Port_takes_both_tcp_and_udp()
    {
        for (var i = 0; i < 50; i++)
        {
            var port = LocalPort.FreeForTcpAndUdp();

            using var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            port.ShouldBeGreaterThan(0);
        }
    }
}
