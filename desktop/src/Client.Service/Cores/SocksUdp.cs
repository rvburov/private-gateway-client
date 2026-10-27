using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// UDP входа SOCKS5 ядра Xray — открыть до первого пакета TUN. Вход с паролем пускает UDP только с адресов, которые уже
/// прошли UDP ASSOCIATE, а адрес запоминает после того, как ответил на него: первый пакет sing-box (запрос DNS сразу после
/// подключения) мог прийти в эту щель и пропадал — «Unauthorized UDP access» в журнале Xray, у программы — ожидание
/// повтора (glibc — 5 с). Служба проходит ASSOCIATE сама до запуска TUN: Xray помнит 127.0.0.1 все время работы.
/// </summary>
public static class SocksUdp
{
    /// <summary>
    /// Пройти вход на 127.0.0.1:<paramref name="port"/> с паролем (RFC 1929) и UDP ASSOCIATE (RFC 1928). true — вход
    /// ответил «успех»; false — отказал, не ответил за <paramref name="timeout"/> или закрыл соединение.
    /// </summary>
    public static async Task<bool> AssociateAsync(int port, string user, string password, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), limit.Token).ConfigureAwait(false);

            // Приветствие: версия 5, один способ — имя и пароль.
            await socket.SendAsync(new byte[] { 5, 1, 2 }, limit.Token).ConfigureAwait(false);
            if (await ReadAsync(socket, 2, limit.Token).ConfigureAwait(false) is not [5, 2])
            {
                return false;
            }

            var name = Encoding.UTF8.GetBytes(user);
            var secret = Encoding.UTF8.GetBytes(password);
            if (name.Length is 0 or > 255 || secret.Length is 0 or > 255)
            {
                return false;
            }

            await socket.SendAsync((byte[])[1, (byte)name.Length, .. name, (byte)secret.Length, .. secret], limit.Token).ConfigureAwait(false);
            if (await ReadAsync(socket, 2, limit.Token).ConfigureAwait(false) is not [1, 0])
            {
                return false;
            }

            // UDP ASSOCIATE с 0.0.0.0:0 — адрес отправителя заранее не известен.
            await socket.SendAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, limit.Token).ConfigureAwait(false);
            var head = await ReadAsync(socket, 4, limit.Token).ConfigureAwait(false);
            if (head is not [5, 0, _, _])
            {
                return false;
            }

            // Ответ дочитывается до конца: адрес (IPv4, IPv6 или имя) и порт.
            var rest = head[3] switch
            {
                1 => 4 + 2,
                4 => 16 + 2,
                3 => (await ReadAsync(socket, 1, limit.Token).ConfigureAwait(false))[0] + 2,
                _ => -1,
            };
            return rest > 0 && (await ReadAsync(socket, rest, limit.Token).ConfigureAwait(false)).Length == rest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Вышел срок, а не отмена вызвавшего.
            return false;
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return false;
        }
    }

    /// <summary>Ровно <paramref name="count"/> байт; соединение закрылось раньше — <see cref="IOException"/>.</summary>
    private static async Task<byte[]> ReadAsync(Socket socket, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = await socket.ReceiveAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                throw new IOException("Вход закрыл соединение");
            }

            read += got;
        }

        return buffer;
    }
}
