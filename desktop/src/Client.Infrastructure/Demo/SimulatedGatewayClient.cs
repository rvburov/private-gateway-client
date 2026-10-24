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

    /// <summary>Сколько длятся проверки в имитации.</summary>
    public static readonly TimeSpan ChecksDelay = TimeSpan.FromMilliseconds(1400);

    /// <summary>Сколько последних записей журнала держит имитация.</summary>
    public const int JournalLimit = 200;

    private static readonly NetworkInfo DemoNetwork = new(NetworkKind.WiFi, "Home", false, false);
    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _journal =
    [
        new(time.GetUtcNow(), JournalLevel.Info, "service", "service started  ipc=ok"),
        // Имя сети в журнал не пишется — как у настоящей службы.
        new(time.GetUtcNow(), JournalLevel.Info, "network", "net up  wifi «•••»  v4+v6"),
    ];

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

    public IReadOnlyList<JournalEntry> Journal
    {
        get
        {
            lock (_gate)
            {
                return [.. _journal];
            }
        }
    }

    public event EventHandler<JournalEntry>? JournalAdded;

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
        Log(JournalLevel.Info, "service", $"connect  server «{server.Name}»");
        try
        {
            await Task.Delay(ConnectDelay, time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool blocked;
        lock (_gate)
        {
            if (token.IsCancellationRequested || _status.State != ConnectionState.Connecting)
            {
                return;
            }

            var now = time.GetUtcNow();
            blocked = server.LatencyMs is null;
            if (server.LatencyMs is { } latency)
            {
                _status = _status.MoveTo(ConnectionState.Connected, now) with
                {
                    Protection = ProtectionChecks.For(ConnectionState.Connected) with { CheckedAt = now },
                    Traffic = new TrafficStats(latency, 18.9, 4.0),
                    // Выход — сам сервер: в демо-данных адреса серверов — IP из диапазонов документации.
                    Exit = new ExitInfo(server.Host, server.City, "дата-центр", "AS64500", _status.Exit.DirectIp),
                };
            }
            else
            {
                // Сервер не отвечает (в списке пинг «нет»): как настоящая служба с блоком при обрыве —
                // интернет перекрыт, другой сервер сам не выбирается, решение за пользователем.
                _status = _status.MoveTo(ConnectionState.Blocked, now) with
                {
                    Protection = ProtectionChecks.For(ConnectionState.Blocked),
                    Traffic = TrafficStats.Empty,
                    Exit = new ExitInfo(null, null, null, null, _status.Exit.DirectIp),
                };
            }
        }

        if (blocked)
        {
            Log(JournalLevel.Warning, "service", $"server «{server.Name}» not responding  traffic blocked, waiting for user");
        }
        else
        {
            Log(JournalLevel.Info, "network", "route v4/v6 default -> tun");
            Log(JournalLevel.Info, "firewall", $"killswitch on  allow={server.Host}");
            Log(JournalLevel.Info, "service", $"tunnel up  server «{server.Name}»");
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

        Log(JournalLevel.Info, "service", "tunnel down  route restored  killswitch off");
        Raise();
        return Task.CompletedTask;
    }

    public async Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(ChecksDelay, time, cancellationToken).ConfigureAwait(false);
        DiagnosticReport report;
        var raise = false;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_status.State == ConnectionState.Connected)
            {
                _status = _status with { Protection = _status.Protection with { CheckedAt = now } };
                raise = true;
            }

            report = new DiagnosticReport(Checks(_status), now);
        }

        if (raise)
        {
            Raise();
        }

        var warnings = report.Results.Count(r => r.State == CheckState.Warning);
        Log(warnings == 0 ? JournalLevel.Info : JournalLevel.Warning, "service",
            $"checks done  ok={report.Results.Count(r => r.State == CheckState.Ok)}  warning={warnings}");
        return report;
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
    }

    private void Raise() => StatusChanged?.Invoke(this, Status);

    /// <summary>Проверки по состоянию имитации. Служба честно отвечает, что она — имитация.</summary>
    private static List<DiagnosticResult> Checks(ConnectionStatus status)
    {
        var state = status.State;
        var protection = state is ConnectionState.Off
            ? ProtectionChecks.For(ConnectionState.Off)
            : status.Protection;
        var running = state switch
        {
            ConnectionState.Connected => CheckState.Ok,
            ConnectionState.Connecting or ConnectionState.Reconnecting => CheckState.Waiting,
            ConnectionState.Blocked or ConnectionState.Error => CheckState.Stopped,
            _ => CheckState.Off,
        };
        return
        [
            new(DiagnosticCheck.Service, CheckState.Warning, "simulated"),
            new(DiagnosticCheck.Core, running, state is ConnectionState.Blocked ? "no_reply" : null),
            new(DiagnosticCheck.AllTraffic, protection.AllTraffic),
            new(DiagnosticCheck.KillSwitch, protection.KillSwitch),
            new(DiagnosticCheck.Dns, protection.Dns),
            new(DiagnosticCheck.Ipv6, protection.Ipv6),
            new(DiagnosticCheck.ExitIp, running),
            new(DiagnosticCheck.OtherVpn, CheckState.Ok),
        ];
    }

    private void Log(JournalLevel level, string source, string message)
    {
        var entry = new JournalEntry(time.GetUtcNow(), level, source, message);
        lock (_gate)
        {
            _journal.Add(entry);
            if (_journal.Count > JournalLimit)
            {
                _journal.RemoveAt(0);
            }
        }

        JournalAdded?.Invoke(this, entry);
    }
}
