using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Запрос DNS мимо туннеля — так, как его шлет DNS ОС по каждой карте: Windows рассылает запросы имен во все адаптеры,
/// systemd-resolved спрашивает DNS карты через саму карту. Сокет UDP с привязкой к карте идет мимо маршрутов TUN, и остановить
/// его могут блок при обрыве или правила «только туннель». Запрос — корень «.» NS: резолвер не узнает ни одного имени.
/// Ответ своего резолвера пришел — DNS мимо туннеля открыт. Отказ ОС, отсутствие карты и тишина не объясняют причину:
/// это отсутствие подтвержденного ответа, а не доказательство блокировки или проверка всех DNS-путей браузера.
/// </summary>
public static class DnsProbe
{
    /// <summary>Резолвер для запроса — Cloudflare по адресу, как и проверочный адрес IP.</summary>
    public static readonly IPEndPoint DefaultResolver = new(IPAddress.Parse("1.1.1.1"), 53);

    // Привязка сокета к карте: Linux — SO_BINDTODEVICE, Windows — IP_UNICAST_IF, macOS — IP_BOUND_IF.
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;
    private const int IpUnicastIf = 31;
    private const int IpBoundIf = 25;

    /// <summary>
    /// Запрос к <paramref name="resolver"/> (IPv4) через карту <paramref name="adapter"/>, не дольше <paramref name="timeout"/>:
    /// ответ своего резолвера на свой запрос — true; отказ ОС, нет карты или тишина — false (неизвестный результат).
    /// Отмена вызывающего остается отменой и не превращается в подтверждение защиты.
    /// </summary>
    public static async Task<bool> PastTunnelAsync(IPEndPoint resolver, string adapter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();
        var query = Query();
        var reply = new byte[512];
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            BindTo(socket, adapter);
            await socket.SendToAsync(query, resolver, wait.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await socket.ReceiveFromAsync(reply, new IPEndPoint(IPAddress.Any, 0), wait.Token).ConfigureAwait(false);

                // Ответ своего резолвера на свой запрос: тот же адрес/порт, номер и флаг ответа.
                if (received.RemoteEndPoint.Equals(resolver) && received.ReceivedBytes >= 12
                    && reply[0] == query[0] && reply[1] == query[1] && (reply[2] & 0x80) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
            }
        }
        catch (Exception e) when (e is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Запрос «.» NS со случайным номером и флагом рекурсии.</summary>
    private static byte[] Query()
    {
        var query = new byte[17];
        RandomNumberGenerator.Fill(query.AsSpan(0, 2));
        query[2] = 0x01;
        query[5] = 1;

        // Имя — корень (нулевой байт 12), тип NS (2), класс IN (1).
        query[14] = 2;
        query[16] = 1;
        return query;
    }

    /// <summary>
    /// Привязать сокет IPv4 к карте <paramref name="adapter"/>: он пойдет ее маршрутом, мимо TUN. Нет карты —
    /// <see cref="SocketException"/>; ОС без такой привязки — <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static void BindTo(Socket socket, string adapter)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (OperatingSystem.IsLinux())
        {
            socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(adapter + "\0"));
            return;
        }

        var index = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapter)?.GetIPProperties().GetIPv4Properties()?.Index
            ?? throw new SocketException((int)SocketError.NetworkDown);
        if (OperatingSystem.IsWindows())
        {
            // Windows ждет номер карты в сетевом порядке байт.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(index));
            return;
        }

        // macOS: опции IP_BOUND_IF нет среди имен .NET — значение прямо в setsockopt (IPPROTO_IP — 0), номер в своем порядке.
        socket.SetRawSocketOption(0, IpBoundIf, BitConverter.GetBytes(index));
    }
}
