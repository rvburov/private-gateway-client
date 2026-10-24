using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Demo;

/// <summary>
/// Имитация службы для этапа 1, пока службы нет: подключение идет через те же состояния и
/// переходы, что и настоящее, с задержкой, а данные — заведомо ненастоящие (адреса из диапазонов
/// документации). Интерфейс показывает, что работает на имитации (значок «демо»).
/// </summary>
public sealed class SimulatedGatewayClient(TimeProvider time) : IGatewayClient, IDisposable
{
    /// <summary>Сколько длится «подключение» в имитации.</summary>
    public static readonly TimeSpan ConnectDelay = TimeSpan.FromMilliseconds(1200);

    private static readonly NetworkInfo DemoNetwork = new(NetworkKind.WiFi, "Home", false, false);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private ConnectionStatus _status = ConnectionStatus.Initial(time.GetUtcNow()) with
    {
        Network = DemoNetwork,
        Exit = new ExitInfo(null, null, null, null, "198.51.100.7"),
    };

    public ConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool IsSimulated => true;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public async Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        CancellationToken token;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _pending.Token;
            _status = _status.MoveTo(ConnectionState.Connecting, time.GetUtcNow()) with { ServerId = server.Id };
        }

        Raise();
        try
        {
            await Task.Delay(ConnectDelay, time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (token.IsCancellationRequested || _status.State != ConnectionState.Connecting)
            {
                return;
            }

            var now = time.GetUtcNow();
            _status = _status.MoveTo(ConnectionState.Connected, now) with
            {
                Protection = ProtectionChecks.For(ConnectionState.Connected) with { CheckedAt = now },
                Traffic = new TrafficStats(server.LatencyMs ?? 40, 18.9, 4.0),
                Exit = new ExitInfo("203.0.113.24", "Амстердам", "дата-центр", "AS64500", _status.Exit.DirectIp),
            };
        }

        Raise();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pending?.Cancel();
            if (_status.State == ConnectionState.Off)
            {
                return Task.CompletedTask;
            }

            _status = _status.MoveTo(ConnectionState.Off, time.GetUtcNow()) with
            {
                ServerId = null,
                Traffic = TrafficStats.Empty,
                Exit = new ExitInfo(null, null, null, null, _status.Exit.DirectIp),
            };
        }

        Raise();
        return Task.CompletedTask;
    }

    public Task RunChecksAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status.State != ConnectionState.Connected)
            {
                return Task.CompletedTask;
            }

            _status = _status with { Protection = _status.Protection with { CheckedAt = time.GetUtcNow() } };
        }

        Raise();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
    }

    private void Raise() => StatusChanged?.Invoke(this, Status);
}
