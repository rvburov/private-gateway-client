using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// DNS по UDP (по умолчанию на 127.0.0.1): на запрос A отвечает одним адресом, на AAAA — адресом IPv6, если он задан,
/// на остальные — пустым ответом; помнит имена.
/// </summary>
internal sealed class TestDns : IDisposable
{
    private readonly UdpClient _socket;
    private readonly IPAddress _answer;
    private readonly IPAddress? _answer6;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _queries = [];

    /// <param name="answer">Адрес в ответе на запрос A.</param>
    /// <param name="listen">Где слушать; null — свободный порт на 127.0.0.1.</param>
    /// <param name="answer6">Адрес в ответе на запрос AAAA; null — пустой ответ.</param>
    public TestDns(IPAddress answer, IPEndPoint? listen = null, IPAddress? answer6 = null)
    {
        _answer = answer;
        _answer6 = answer6;
        _socket = new UdpClient(listen ?? new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    /// <summary>Имена из запросов в порядке прихода.</summary>
    public IReadOnlyList<string> Queries
    {
        get
        {
            lock (_queries)
            {
                return [.. _queries];
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (Reply(received.Buffer) is { } reply)
            {
                await _socket.SendAsync(reply, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ответ на запрос с одним вопросом; разобрать не вышло — null.</summary>
    private byte[]? Reply(byte[] query)
    {
        if (query.Length < 12)
        {
            return null;
        }

        var name = new StringBuilder();
        var at = 12;
        while (at < query.Length && query[at] != 0)
        {
            var length = query[at];
            if (at + 1 + length > query.Length)
            {
                return null;
            }

            name.Append(name.Length > 0 ? "." : string.Empty).Append(Encoding.ASCII.GetString(query, at + 1, length));
            at += 1 + length;
        }

        var questionEnd = at + 5;
        if (questionEnd > query.Length)
        {
            return null;
        }

        lock (_queries)
        {
            _queries.Add(name.ToString());
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(at + 1));
        var answer = type switch
        {
            1 => _answer,
            28 => _answer6,
            _ => null,
        };
        var reply = new List<byte>(query[..questionEnd]);
        reply[2] = 0x81; // ответ, рекурсия запрошена
        reply[3] = 0x80; // рекурсия доступна, без ошибки
        reply[6] = 0;
        reply[7] = answer is null ? (byte)0 : (byte)1;
        reply[8] = reply[9] = reply[10] = reply[11] = 0;
        if (answer is not null)
        {
            // Имя — ссылкой на вопрос, тип A или AAAA, класс IN, TTL 60 с, адрес.
            var bytes = answer.GetAddressBytes();
            reply.AddRange([0xC0, 0x0C, 0, (byte)type, 0, 1, 0, 0, 0, 60, 0, (byte)bytes.Length]);
            reply.AddRange(bytes);
        }

        return [.. reply];
    }
}
