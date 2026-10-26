using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Настоящая служба по IPC. Состояние и журнал приходят потоками WatchStatus и WatchLog (обрыв —
/// переподключение через 2 с); Connect собирает запрос из описания сервера с ключами и текущих
/// настроек. Отказ — состояние самой службы («Ошибка» с ее кодом); служба не отвечает или пропала
/// связь с ней — «Ошибка» с кодом «служба не запущена».
/// </summary>
public sealed class ServiceGatewayClient : IGatewayClient, IDisposable
{
    /// <summary>Сколько последних записей журнала службы держит интерфейс.</summary>
    public const int JournalLimit = 200;

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private readonly GrpcChannel _channel;
    private readonly V1.GatewayService.GatewayServiceClient _client;
    private readonly Func<string, ServerDefinition?> _definitions;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();

    // Токен взят заранее: потоки дочитывают после Dispose, а у закрытого источника токен уже не взять.
    private readonly CancellationToken _stopping;
    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _journal = [];
    private ConnectionStatus _status;

    // «Продолжить (не рекомендуется)» на «Работает другой VPN»: до отключения другой VPN не мешает подключаться.
    private volatile bool _allowOtherVpn;

    /// <param name="channel">Канал до службы (<see cref="IpcChannel"/>); клиент его закрывает.</param>
    /// <param name="service">Какая это служба: установленная или пользователя.</param>
    /// <param name="definitions">Описание сервера с ключами по id — из хранилища (ключи — из хранилища ОС).</param>
    /// <param name="settings">Текущие настройки: режим, защита, подключение, маршрутизация.</param>
    /// <param name="time">Часы программы.</param>
    public ServiceGatewayClient(GrpcChannel channel, ServiceKind service, Func<string, ServerDefinition?> definitions, Func<AppSettings> settings,
        TimeProvider time)
    {
        _channel = channel;
        Service = service;
        _client = new V1.GatewayService.GatewayServiceClient(channel);
        _definitions = definitions;
        _settings = settings;
        _time = time;
        _stopping = _stop.Token;
        _status = ConnectionStatus.Initial(time.GetUtcNow());
        _ = WatchStatusAsync();
        _ = WatchLogAsync();
    }

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

    public ServiceKind Service { get; }

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
        var settings = _settings();
        var request = new V1.ConnectRequest
        {
            // Описания нет (сервер удален) — запрос без сервера: служба откажет и остановит прежнее подключение.
            Server = _definitions(server.Id)?.ToContract(server.Id),
            Mode = settings.Mode.ToContract(),
            Protection = settings.ToContract(),
            Connection = settings.ToConnection(),
            Routing = settings.ToRouting(),
            AllowOtherVpn = _allowOtherVpn,
        };
        try
        {
            // Отказ — состояние службы «Ошибка». Поток WatchStatus принесет его позже ответа, а окно решает
            // сразу после подключения: состояние берется у службы здесь же.
            var reply = await _client.ConnectAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!reply.Accepted)
            {
                Apply((await _client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore());
            }
        }
        catch (RpcException)
        {
            Unreachable(server.Id, settings.Mode);
        }
    }

    public async Task<ServiceDetails?> GetDetailsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _client.GetServiceInfoAsync(new V1.GetServiceInfoRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore();
        }
        catch (RpcException)
        {
            return null;
        }
    }

    public Task ConnectDespiteOtherVpnAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        _allowOtherVpn = true;
        return ConnectAsync(server, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _allowOtherVpn = false;
        try
        {
            await _client.DisconnectAsync(Disconnect(_settings()), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // Службы нет — и защиты через нее нет; состояние обновится, когда она вернется.
        }
    }

    /// <summary>Запрос отключения: в строгом режиме защита выключается, а сеть остается закрытой.</summary>
    public static V1.DisconnectRequest Disconnect(AppSettings settings) => new() { KeepBlock = ConnectionController.KeepsBlock(settings) };

    /// <summary>Пауза началась — в ответе службы есть срок; службы нет — паузы нет.</summary>
    public async Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(duration) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Until is not null;
        }
        catch (RpcException)
        {
            return false;
        }
    }

    public async Task ResumeProtectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException)
        {
        }
    }

    /// <summary>Проверки службы; службы нет — одна проверка «служба» остановлена.</summary>
    public async Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await _client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DiagnosticReport(
                [.. report.Results
                    .Where(r => ContractMapping.ToCoreCheck(r.Id) is not null && r.State != V1.CheckState.Unspecified)
                    .Select(r => new DiagnosticResult(ContractMapping.ToCoreCheck(r.Id)!.Value, r.State.ToCore(), r.Reason is { Length: > 0 } reason ? reason : null))],
                report.FinishedAt?.ToDateTimeOffset() ?? _time.GetUtcNow());
        }
        catch (RpcException)
        {
            return new DiagnosticReport([new DiagnosticResult(DiagnosticCheck.Service, CheckState.Stopped, "no_reply")], _time.GetUtcNow());
        }
    }

    public async Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        var request = new V1.MeasureLatencyRequest();
        foreach (var id in serverIds)
        {
            if (_definitions(id) is { } definition)
            {
                request.Targets.Add(new V1.LatencyTarget { Id = id, Host = definition.Host, Port = (uint)definition.Port, Server = definition.ToContract(id) });
            }
        }

        if (request.Targets.Count == 0)
        {
            return new Dictionary<string, LatencyResult>();
        }

        try
        {
            var reply = await _client.MeasureLatencyAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Results.Where(r => r.Reachable || r.Failure != V1.LatencyFailure.Unspecified).ToDictionary(r => r.Id, r => r.ToCore());
        }
        catch (RpcException)
        {
            return new Dictionary<string, LatencyResult>();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _channel.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Служба не ответила на Connect — «Подключение» и сразу «Ошибка» с кодом «служба не запущена»:
    /// так интерфейс показывает причину, а следующее нажатие снова подключает.
    /// </summary>
    private void Unreachable(string serverId, ConnectionMode mode)
    {
        ConnectionStatus status;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var current = _status with { Mode = mode, ServerId = serverId };
            if (!ConnectionStateMachine.CanMove(current.State, ConnectionState.Error))
            {
                current = current.MoveTo(ConnectionState.Connecting, now);
            }

            status = _status = NoService((current with { ErrorCode = ErrorCodes.ServiceNotRunning }).MoveTo(ConnectionState.Error, now));
        }

        StatusChanged?.Invoke(this, status);
    }

    private async Task WatchStatusAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var call = _client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: _stopping);
                while (await call.ResponseStream.MoveNext(_stopping).ConfigureAwait(false))
                {
                    Apply(call.ResponseStream.Current.ToCore());
                }
            }
            catch (RpcException)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // Клиент закрыт.
                return;
            }

            Lost();
            await Pause().ConfigureAwait(false);
        }
    }

    private void Apply(ConnectionStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// Поток состояния оборвался: защита через службу больше не подтверждена — «Ошибка» с кодом
    /// «служба не запущена». «Выключено» и уже ошибка не меняются; вернется служба — придет ее состояние.
    /// </summary>
    private void Lost()
    {
        ConnectionStatus status;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested || _status.State is ConnectionState.Off or ConnectionState.Error)
            {
                return;
            }

            status = _status = NoService((_status with { ErrorCode = ErrorCodes.ServiceNotRunning }).MoveTo(ConnectionState.Error, _time.GetUtcNow()));
        }

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Что делает служба, неизвестно — окно не обещает защиту: проверки как при выключенной.</summary>
    private static ConnectionStatus NoService(ConnectionStatus status) => status with { Protection = ProtectionChecks.For(ConnectionState.Off) };

    private async Task WatchLogAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var call = _client.WatchLog(new V1.WatchLogRequest { Tail = JournalLimit }, cancellationToken: _stopping);
                lock (_gate)
                {
                    // Хвост журнала приходит заново после каждого переподключения.
                    _journal.Clear();
                }

                while (await call.ResponseStream.MoveNext(_stopping).ConfigureAwait(false))
                {
                    var entry = call.ResponseStream.Current;
                    var journal = new JournalEntry(entry.Time?.ToDateTimeOffset() ?? _time.GetUtcNow(),
                        entry.Level == V1.LogLevel.Unspecified ? JournalLevel.Info : entry.Level.ToCore(), entry.Source, entry.Message);
                    lock (_gate)
                    {
                        _journal.Add(journal);
                        if (_journal.Count > JournalLimit)
                        {
                            _journal.RemoveAt(0);
                        }
                    }

                    JournalAdded?.Invoke(this, journal);
                }
            }
            catch (RpcException)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // Клиент закрыт.
                return;
            }

            await Pause().ConfigureAwait(false);
        }
    }

    private async Task Pause()
    {
        try
        {
            await Task.Delay(Retry, _time, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
