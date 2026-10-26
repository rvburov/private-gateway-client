using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Замер WebRTC: запрос STUN Binding (RFC 5389) по UDP — какой адрес и порт видит сервер STUN. Так браузер узнает свой
/// внешний адрес для WebRTC: адрес выхода — WebRTC идет через сервер; реальный — мимо него (в режиме прокси браузер
/// шлет UDP WebRTC напрямую).
/// </summary>
public static class StunProbe
{
    /// <summary>Сервер STUN по умолчанию: Cloudflare, как и проверочный адрес IP.</summary>
    public static readonly DnsEndPoint DefaultServer = new("stun.cloudflare.com", 3478);

    /// <summary>Сколько ждать ответа на все три попытки.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort MappedAddress = 0x0001;
    private const ushort XorMappedAddress = 0x0020;
    private const uint MagicCookie = 0x2112A442;

    /// <summary>Сервер STUN по имени: адрес — через DNS системы (как у браузера), первый IPv4; имени нет — null.</summary>
    public static async Task<IPEndPoint?> MappedAsync(DnsEndPoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        IPAddress[] found;
        try
        {
            found = await Dns.GetHostAddressesAsync(server.Host, AddressFamily.InterNetwork, cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            return null;
        }

        return found.Length == 0 ? null : await MappedAsync(new IPEndPoint(found[0], server.Port), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Адрес, который видит сервер STUN; три попытки за <paramref name="timeout"/> (UDP теряется). Нет ответа — null.
    /// </summary>
    public static async Task<IPEndPoint?> MappedAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var udp = new UdpClient(server.AddressFamily);
        var transaction = RandomNumberGenerator.GetBytes(12);
        var request = Request(transaction);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var attempt = TimeSpan.FromTicks(timeout.Ticks / 3);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                await udp.SendAsync(request, server, deadline.Token).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(attempt);
                try
                {
                    while (true)
                    {
                        var answer = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        if (Parse(answer.Buffer, transaction) is { } mapped)
                        {
                            return mapped;
                        }
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // Ответ не пришел за попытку — еще раз.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Время вышло.
        }
        catch (SocketException)
        {
            // Сеть недоступна или закрыта блоком.
        }

        return null;
    }

    /// <summary>Запрос Binding: заголовок 20 байт без атрибутов.</summary>
    public static byte[] Request(byte[] transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(request, BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transaction.CopyTo(request, 8);
        return request;
    }

    /// <summary>
    /// Адрес из ответа Binding Success на запрос <paramref name="transaction"/>: XOR-MAPPED-ADDRESS, а если его нет —
    /// MAPPED-ADDRESS. Чужой ответ, не тот тип или испорченный — null.
    /// </summary>
    public static IPEndPoint? Parse(byte[] answer, byte[] transaction)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(transaction);
        if (answer.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(answer) != BindingSuccess
            || BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(4)) != MagicCookie || !answer.AsSpan(8, 12).SequenceEqual(transaction))
        {
            return null;
        }

        var end = Math.Min(answer.Length, 20 + BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2)));
        IPEndPoint? plain = null;
        for (var at = 20; at + 4 <= end;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(at));
            var length = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(at + 2));
            if (at + 4 + length > end)
            {
                break;
            }

            var value = answer.AsSpan(at + 4, length);
            if (type == XorMappedAddress && Address(value, answer.AsSpan(4, 16)) is { } mapped)
            {
                return mapped;
            }

            if (type == MappedAddress)
            {
                plain ??= Address(value, null);
            }

            at += 4 + ((length + 3) & ~3);
        }

        return plain;
    }

    // Атрибут адреса: 0, семейство (1 — IPv4, 2 — IPv6), порт, адрес; у XOR — порт и адрес смешаны с cookie и номером запроса.
    private static IPEndPoint? Address(ReadOnlySpan<byte> value, ReadOnlySpan<byte> xor)
    {
        if (value.Length < 4)
        {
            return null;
        }

        var size = value[1] switch
        {
            1 => 4,
            2 => 16,
            _ => 0,
        };
        if (size == 0 || value.Length < 4 + size)
        {
            return null;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        var address = value.Slice(4, size).ToArray();
        if (!xor.IsEmpty)
        {
            port ^= (ushort)(MagicCookie >> 16);
            for (var i = 0; i < size; i++)
            {
                address[i] ^= xor[i];
            }
        }

        return new IPEndPoint(new IPAddress(address), port);
    }
}
