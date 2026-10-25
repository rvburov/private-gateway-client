using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Сайт на 127.0.0.1 (или своем адресе): на любой запрос отвечает одной страницей и считает запросы.</summary>
internal sealed class LocalSite : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _response;
    private readonly CancellationTokenSource _stop = new();
    private int _requests;

    /// <param name="page">Текст страницы.</param>
    /// <param name="address">Где слушать; null — 127.0.0.1.</param>
    /// <param name="port">Порт; 0 — свободный.</param>
    public LocalSite(string page, IPAddress? address = null, int port = 0)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        var body = Encoding.UTF8.GetBytes(page);
        _response = [.. Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>Адрес сайта; адрес IPv6 — в скобках.</summary>
    public Uri Url => new($"http://{(IPEndPoint)_listener.LocalEndpoint}/");

    public int Requests => Volatile.Read(ref _requests);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(_response).ConfigureAwait(false);
            }
        }
    }
}
