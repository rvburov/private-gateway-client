using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Одноразовый SOCKS-мост замера под закрытой сетью. Принимает только парольное подключение с петли и только TCP
/// к закрепленным адресам профилей. Имена не разрешает, произвольный CONNECT и UDP не принимает. Наружные сокеты
/// принадлежат службе, помечены и привязаны к физической карте; точное исключение брандмауэра ставит вызывающий.
/// </summary>
public sealed class DiagnosticProxy : IAsyncDisposable
{
    public const int MaxEndpoints = 64;
    private const int MaxConnections = 32;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly Dictionary<(string Host, int Port), IPAddress[]> _allowed = [];
    private readonly Func<IPEndPoint, string?, uint, CancellationToken, ValueTask<Socket>> _connect;
    private readonly string? _adapter;
    private readonly uint _mark;
    private readonly TimeSpan _timeout;
    private readonly Task _accepting;
    private long _next;
    private Exception? _unexpected;
    private int _disposed;

    public DiagnosticProxy(IReadOnlyDictionary<TransportEndpoint, IReadOnlyList<IPAddress>> allowed, string? adapter, uint mark,
        TimeSpan timeout, Func<IPEndPoint, string?, uint, CancellationToken, ValueTask<Socket>>? connect = null)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count is 0 or > MaxEndpoints || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("Недопустимый план диагностического моста", nameof(allowed));
        }
        foreach (var (endpoint, addresses) in allowed)
        {
            if (endpoint.Port is <= 0 or > 65535 || addresses.Count is 0 or > 16)
            {
                throw new ArgumentException("Недопустимый адрес диагностического моста", nameof(allowed));
            }
            var key = (Normalize(endpoint.Host), endpoint.Port);
            var pinned = addresses.Select(Normalize);
            _allowed[key] = _allowed.TryGetValue(key, out var previous) ? previous.Concat(pinned).Distinct().ToArray() : pinned.Distinct().ToArray();
        }
        if (_allowed.Values.Sum(addresses => addresses.Length) > MaxEndpoints)
        {
            throw new ArgumentException("Слишком много адресов диагностического моста", nameof(allowed));
        }
        _adapter = adapter;
        _mark = mark;
        _timeout = timeout;
        _connect = connect ?? ConnectMarkedAsync;
        _listener.Start(MaxConnections);
        Upstream = new XrayProbeUpstream(((IPEndPoint)_listener.LocalEndpoint).Port,
            new SocksAccount(VpnTunnel.Secret(), VpnTunnel.Secret()));
        _accepting = AcceptAsync();
    }

    public XrayProbeUpstream Upstream { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        _slots.Dispose();
        _stop.Dispose();
        if (_unexpected is { } error)
        {
            throw new IOException("Ошибка диагностического моста", error);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }
                var id = Interlocked.Increment(ref _next);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connections.TryAdd(id, completed.Task);
                _ = ServeAndReleaseAsync(client, id, completed);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Конец одного диагностического запроса закрывает прием и все принятые сокеты.
        }
    }

    private async Task ServeAndReleaseAsync(TcpClient client, long id, TaskCompletionSource completed)
    {
        try
        {
            await ServeAsync(client).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _unexpected, error, null);
        }
        finally
        {
            _slots.Release();
            _connections.TryRemove(id, out _);
            completed.TrySetResult();
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token))
        {
            try
            {
                var stream = client.GetStream();
                var hello = await ReadAsync(stream, 2, handshake.Token).ConfigureAwait(false);
                if (hello[0] != 5 || hello[1] == 0)
                {
                    return;
                }
                var methods = await ReadAsync(stream, hello[1], handshake.Token).ConfigureAwait(false);
                var password = methods.Contains((byte)2);
                await stream.WriteAsync(new byte[] { 5, password ? (byte)2 : (byte)255 }, handshake.Token).ConfigureAwait(false);
                if (!password || !await AuthenticateAsync(stream, handshake.Token).ConfigureAwait(false))
                {
                    return;
                }
                var request = await ReadAsync(stream, 4, handshake.Token).ConfigureAwait(false);
                if (request[0] != 5 || request[1] != 1 || request[2] != 0)
                {
                    await ReplyAsync(stream, 7, handshake.Token).ConfigureAwait(false);
                    return;
                }
                var host = request[3] switch
                {
                    1 => new IPAddress(await ReadAsync(stream, 4, handshake.Token).ConfigureAwait(false)).ToString(),
                    4 => new IPAddress(await ReadAsync(stream, 16, handshake.Token).ConfigureAwait(false)).ToString(),
                    3 => Encoding.UTF8.GetString(await ReadAsync(stream,
                        (await ReadAsync(stream, 1, handshake.Token).ConfigureAwait(false))[0], handshake.Token).ConfigureAwait(false)),
                    _ => null,
                };
                if (host is null)
                {
                    await ReplyAsync(stream, 8, handshake.Token).ConfigureAwait(false);
                    return;
                }
                var portBytes = await ReadAsync(stream, 2, handshake.Token).ConfigureAwait(false);
                var port = (portBytes[0] << 8) | portBytes[1];
                if (!_allowed.TryGetValue((Normalize(host), port), out var addresses))
                {
                    await ReplyAsync(stream, 2, handshake.Token).ConfigureAwait(false);
                    return;
                }

                // После аутентификации срок относится к соединению с кандидатом; копирование живет лишь до конца замера.
                deadline.CancelAfter(_timeout);
                Socket? remote = null;
                foreach (var address in addresses)
                {
                    using var addressStop = CancellationTokenSource.CreateLinkedTokenSource(handshake.Token);
                    if (addresses.Length > 1)
                    {
                        addressStop.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_timeout.TotalSeconds / addresses.Length, 1, 3)));
                    }
                    try
                    {
                        remote = await _connect(new IPEndPoint(address, port), _adapter, _mark, addressStop.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception error) when (error is SocketException or IOException)
                    {
                        // Следующий заранее закрепленный адрес того же профиля; DNS и прямого обхода нет.
                    }
                    catch (OperationCanceledException) when (!handshake.IsCancellationRequested)
                    {
                        // Медленный первый адрес не забирает все время у второго закрепленного адреса.
                    }
                }
                if (remote is null)
                {
                    await ReplyAsync(stream, 5, handshake.Token).ConfigureAwait(false);
                    return;
                }
                using (remote)
                await using (var outside = new NetworkStream(remote, ownsSocket: false))
                {
                    await ReplyAsync(stream, 0, handshake.Token).ConfigureAwait(false);
                    using var copying = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var outgoing = stream.CopyToAsync(outside, copying.Token);
                    var incoming = outside.CopyToAsync(stream, copying.Token);
                    await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
                    await copying.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Ошибка одного кандидата или отмена пробы не открывает ни другого адреса, ни другого пути.
            }
        }
    }

    private async Task<bool> AuthenticateAsync(NetworkStream stream, CancellationToken token)
    {
        var head = await ReadAsync(stream, 2, token).ConfigureAwait(false);
        if (head[0] != 1)
        {
            return false;
        }
        var user = await ReadAsync(stream, head[1], token).ConfigureAwait(false);
        var password = await ReadAsync(stream, (await ReadAsync(stream, 1, token).ConfigureAwait(false))[0], token).ConfigureAwait(false);
        var accepted = CryptographicOperations.FixedTimeEquals(user, Encoding.UTF8.GetBytes(Upstream.Account.User))
            & CryptographicOperations.FixedTimeEquals(password, Encoding.UTF8.GetBytes(Upstream.Account.Password));
        await stream.WriteAsync(new byte[] { 1, accepted ? (byte)0 : (byte)1 }, token).ConfigureAwait(false);
        return accepted;
    }

    private static ValueTask ReplyAsync(NetworkStream stream, byte code, CancellationToken token) =>
        stream.WriteAsync(new byte[] { 5, code, 0, 1, 127, 0, 0, 1, 0, 0 }, token);

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static string Normalize(string host) => IPAddress.TryParse(host.Trim('[', ']'), out var address)
        ? Normalize(address).ToString() : host.TrimEnd('.').ToLowerInvariant();

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static async ValueTask<Socket> ConnectMarkedAsync(IPEndPoint endpoint, string? adapter, uint mark, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (OperatingSystem.IsLinux() && mark != 0)
            {
                socket.SetRawSocketOption(1, 36, BitConverter.GetBytes(mark));
            }
            if (adapter is not null)
            {
                DnsProbe.BindTo(socket, adapter);
            }
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
