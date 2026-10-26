using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Эхо по UDP: возвращает каждую датаграмму отправителю и считает их.</summary>
internal sealed class UdpEcho : IDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _stop = new();
    private int _datagrams;

    /// <param name="listen">Где слушать; null — свободный порт на 127.0.0.1.</param>
    public UdpEcho(IPEndPoint? listen = null)
    {
        _socket = new UdpClient(listen ?? new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    /// <summary>Сколько датаграмм пришло.</summary>
    public int Datagrams => Volatile.Read(ref _datagrams);

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
            try
            {
                var received = await _socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _datagrams);
                await _socket.SendAsync(received.Buffer, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }
}
