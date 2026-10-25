using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Свободный порт на 127.0.0.1 для входа ядра.</summary>
public static class LocalPort
{
    /// <summary>
    /// Порт, свободный и для TCP, и для UDP: вход SOCKS5 ядра занимает оба. На Windows списки
    /// зарезервированных портов у TCP и UDP свои — порт, который ОС дала для TCP, для UDP бывает закрыт,
    /// и наоборот. Поэтому порт по очереди выбирает то TCP, то UDP, а другой протокол его проверяет.
    /// </summary>
    public static int FreeForTcpAndUdp()
    {
        for (var attempt = 1; ; attempt++)
        {
            var (first, second) = attempt % 2 == 1 ? (ProtocolType.Tcp, ProtocolType.Udp) : (ProtocolType.Udp, ProtocolType.Tcp);
            try
            {
                using var chosen = Bind(first, 0);
                var port = ((IPEndPoint)chosen.LocalEndPoint!).Port;
                using var other = Bind(second, port);
                return port;
            }
            catch (SocketException) when (attempt < 200)
            {
                // Порт закрыт или занят для другого протокола — следующий.
            }
        }
    }

    private static Socket Bind(ProtocolType protocol, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
