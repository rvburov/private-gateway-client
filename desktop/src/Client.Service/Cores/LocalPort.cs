using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Свободный порт на 127.0.0.1 для входа ядра.</summary>
public static class LocalPort
{
    /// <summary>
    /// Порт, свободный и для TCP, и для UDP: вход SOCKS5 ядра занимает оба. На Windows списки
    /// зарезервированных портов у TCP и UDP свои — порт, который ОС дала для TCP, для UDP бывает закрыт.
    /// </summary>
    public static int FreeForTcpAndUdp()
    {
        for (var attempt = 1; ; attempt++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException) when (attempt < 100)
            {
                // Для UDP порт закрыт или занят — следующий.
            }
        }
    }
}
