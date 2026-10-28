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
    private readonly bool _clientCloses;
    private int _requests;

    /// <param name="page">Текст страницы.</param>
    /// <param name="address">Где слушать; null — 127.0.0.1.</param>
    /// <param name="port">Порт; 0 — свободный.</param>
    /// <param name="status">Строка ответа: код и текст.</param>
    /// <param name="clientCloses">
    /// Соединение после ответа закрывает клиент, как у сервера HTTP/1.1 с keep-alive; false — сайт закрывает сразу. Сервер Xray-core
    /// 26.x закрывает WebSocket, как только сайт закрыл соединение, и клиент Xray (любой версии) считает такое закрытие ошибкой и
    /// бросает непрочитанный хвост ответа — простой HTTP через HTTP-прокси получал 503 (пункт ROADMAP этапа 11).
    /// </param>
    public LocalSite(string page, IPAddress? address = null, int port = 0, string status = "200 OK", bool clientCloses = false)
    {
        _clientCloses = clientCloses;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        var body = Encoding.UTF8.GetBytes(page);
        _response = [.. Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
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

            // Каждое соединение — своей задачей: соединение, которое молчит или шлет не HTTP (TLS чужой программы), не держит
            // остальные.
            _ = AnswerAsync(client);
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(_response, _stop.Token).ConfigureAwait(false);
                if (_clientCloses)
                {
                    // Ответ отдан — закрытия ждем от клиента (не дольше минуты).
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    wait.CancelAfter(TimeSpan.FromMinutes(1));
                    while (await stream.ReadAsync(buffer, wait.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Соединение оборвалось или сайт закрыт.
            }
        }
    }
}
