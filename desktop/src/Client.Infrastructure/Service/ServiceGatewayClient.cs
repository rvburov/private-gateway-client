using System.Net;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
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

    /// <summary>
    /// Как часто окно смотрит системный прокси, пока «Защищено» (решение 511): реестр Windows читается мгновенно, на Linux и
    /// macOS — короткой программой (gsettings, scutil); другая программа, включенная рядом, замечается за это время.
    /// </summary>
    public static readonly TimeSpan ProxyWatch = TimeSpan.FromSeconds(5);

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

    private readonly Func<UserProxy?>? _userProxy;
    private readonly TimeSpan _proxyWatch;
    private int _watchingProxy;
    private readonly bool _manageUserProxy;
    private volatile bool _receivedStatus;
    private DateTimeOffset? _lastServiceSince;
    private int _ownedProxyPort;

    // Подключиться вопреки другому VPN (ConnectDespiteOtherVpnAsync): до отключения другой VPN не мешает подключаться.
    private volatile bool _allowOtherVpn;

    /// <param name="channel">Канал до службы (<see cref="IpcChannel"/>); клиент его закрывает.</param>
    /// <param name="service">Какая это служба: установленная или пользователя.</param>
    /// <param name="definitions">Описание сервера с ключами по id — из хранилища (ключи — из хранилища ОС).</param>
    /// <param name="settings">Текущие настройки: режим, защита, подключение, маршрутизация.</param>
    /// <param name="time">Часы программы.</param>
    /// <param name="userProxy">
    /// Системный прокси пользователя окна (<see cref="IUserProxySettings.Read"/>): поставленный другой программой уходит службе в
    /// запросе подключения — сама установленная служба его не видит; null — не передавать.
    /// </param>
    /// <param name="proxyWatch">
    /// Как часто, пока «Защищено», смотреть системный прокси пользователя: поставила его другая программа — служба об этом узнает
    /// (<see cref="ReportForeignProxyAsync"/>); по умолчанию — <see cref="ProxyWatch"/>.
    /// </param>
    /// <param name="manageUserProxy">Окно Windows умеет управлять HKCU; служба должна подтвердить эту возможность.</param>
    /// <param name="calls">Вызовы IPC; null — настоящий канал. Подмена нужна детерминированным тестам без сокетов.</param>
    public ServiceGatewayClient(GrpcChannel channel, ServiceKind service, Func<string, ServerDefinition?> definitions, Func<AppSettings> settings,
        TimeProvider time, Func<UserProxy?>? userProxy = null, TimeSpan? proxyWatch = null, bool manageUserProxy = false, CallInvoker? calls = null)
    {
        _userProxy = userProxy;
        _manageUserProxy = manageUserProxy;
        _proxyWatch = proxyWatch ?? ProxyWatch;
        _channel = channel;
        Service = service;
        _client = new V1.GatewayService.GatewayServiceClient(calls ?? channel.CreateCallInvoker());
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

    public bool HasReceivedStatus => _receivedStatus;

    public int? ManagedUserProxyPort => Volatile.Read(ref _ownedProxyPort) is > 0 and var port ? port : null;

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
        var windowProxy = _manageUserProxy && (await GetDetailsAsync(cancellationToken).ConfigureAwait(false))?.Capabilities
            ?.Contains("user-proxy-window-v1", StringComparer.Ordinal) == true;
        var request = new V1.ConnectRequest
        {
            // Описания нет (сервер удален) — запрос без сервера: служба откажет и остановит прежнее подключение.
            Server = _definitions(server.Id)?.ToContract(server.Id),
            Mode = settings.Mode.ToContract(),
            Protection = settings.ToContract(),
            Connection = settings.ToConnection(),
            Routing = settings.ToRouting(),
            AllowOtherVpn = _allowOtherVpn || settings.Protection.AllowOtherVpn,
            ForeignProxy = await ForeignProxyAsync(settings).ConfigureAwait(false),
            UserProxyManagedByWindow = windowProxy,
        };
        try
        {
            // Ответ Connect не содержит снимка. После принятой попытки WatchStatus тоже может еще хранить прежнюю
            // ошибку: окно принимает решение сразу после await, поэтому актуальный статус читается при любом ответе.
            await _client.ConnectAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            Apply((await _client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore());
        }
        catch (RpcException)
        {
            Unreachable(server.Id, settings.Mode);
        }
    }

    /// <summary>
    /// Системный прокси другой программы — «адрес:порт» для службы (она проверит, слушает ли его вход на этой машине); свой,
    /// скрипт автонастройки или прокси нет — пусто. На Linux и macOS его читает отдельная программа — в фоне.
    /// </summary>
    private async Task<string> ForeignProxyAsync(AppSettings settings)
    {
        if (_userProxy is not { } read)
        {
            return string.Empty;
        }

        var proxy = await Task.Run(read).ConfigureAwait(false);
        return proxy is { IsScript: false } && proxy.IsForeign(settings, ManagedUserProxyPort)
            ? ProxyAddress.Endpoint(proxy.Address) ?? string.Empty : string.Empty;
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

    /// <summary>Служба без CheckForUpdate (старая) или без связи — null: окно скажет, что проверить не удалось.</summary>
    public async Task<UpdateOffer?> CheckForUpdateAsync(UpdateChannel channel, bool force, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _client.CheckForUpdateAsync(new V1.CheckForUpdateRequest { Channel = channel.ToContract(), Force = force },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.ToCore();
        }
        catch (RpcException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Ответ не по контракту.
            return null;
        }
    }

    /// <summary>Служба без инспектора (старая) или без связи — null.</summary>
    public async Task<NetworkSnapshot?> InspectNetworkAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _client.InspectNetworkAsync(new V1.InspectNetworkRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore();
        }
        catch (RpcException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Ответ не по контракту.
            return null;
        }
    }

    /// <summary>Служба без этого вызова (старая) или без связи — null.</summary>
    public async Task<AddressInfo?> DescribeAddressAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            return (await _client.DescribeAddressAsync(new V1.DescribeAddressRequest { Address = address.ToString() }, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).ToCore();
        }
        catch (RpcException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Ответ не по контракту.
            return null;
        }
    }

    public async Task<SubscriptionDownload> DownloadSubscriptionAsync(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            return (await _client.DownloadSubscriptionAsync(new V1.SubscriptionDownloadRequest { Address = address.AbsoluteUri },
                cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore();
        }
        catch (RpcException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException) when (!cancellationToken.IsCancellationRequested)
        {
            // Старая служба или обрыв: прямой повтор запрещен.
            return SubscriptionDownload.Failed(DownloadFailure.Connection);
        }
    }

    /// <summary>Служба без этого вызова (старая) или без связи — null.</summary>
    public async Task<DelayBreakdown?> MeasureDelayAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _client.MeasureDelayAsync(new V1.MeasureDelayRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DelayBreakdown(Positive(reply.ToServerMs), Positive(reply.ThroughServerMs), Positive(reply.NewConnectionMs),
                reply.MeasuredAt?.ToDateTimeOffset() ?? _time.GetUtcNow());
        }
        catch (RpcException)
        {
            return null;
        }

        static int? Positive(int value) => value > 0 ? value : null;
    }

    /// <summary>Служба без этого вызова (старая) или без связи — null.</summary>
    public async Task<InterferenceProbe?> CheckEntriesAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        try
        {
            var request = new V1.CheckEntriesRequest();
            request.Addresses.AddRange(addresses);
            var reply = await _client.CheckEntriesAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new InterferenceProbe([.. reply.Entries.Select(e => new EntryCheck(e.Address, e.Listens, e.Process.Length > 0 ? e.Process : null))],
                reply.Clients.ToCore());
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>Служба без этого вызова (старая) или без связи — null.</summary>
    public async Task<NetworkComparison?> CompareNetworkAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _client.CompareNetworkAsync(new V1.CompareNetworkRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore();
        }
        catch (RpcException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Ответ не по контракту.
            return null;
        }
    }

    /// <summary>Служба без этого вызова (старая) или без связи — null; строки не по контракту пропускаются.</summary>
    public async Task<IReadOnlyList<NetworkConnection>?> ListConnectionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _client.ListConnectionsAsync(new V1.ListConnectionsRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
            return [.. list.Connections.Select(c => c.ToCore()).OfType<NetworkConnection>()];
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>Служба без этого вызова (старая) или обрыв связи — поток кончается.</summary>
    public async IAsyncEnumerable<TrafficSample> WatchTrafficAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.WatchTraffic(new V1.WatchTrafficRequest(), cancellationToken: cancellationToken);
        var stream = call.ResponseStream;
        while (true)
        {
            TrafficSample sample;
            try
            {
                if (!await stream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                sample = stream.Current.ToCore();
            }
            catch (RpcException)
            {
                yield break;
            }

            yield return sample;
        }
    }

    /// <summary>Служба без замеров (старая), отказ или обрыв связи — поток кончается; отмена окном — тоже.</summary>
    public async IAsyncEnumerable<ProbeStep> ProbeAsync(ProbeKind kind, string target, int port,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.RunProbe(new V1.ProbeRequest { Kind = kind.ToContract(), Target = target, Port = port }, cancellationToken: cancellationToken);
        var stream = call.ResponseStream;
        while (true)
        {
            ProbeStep step;
            try
            {
                if (!await stream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                step = stream.Current.ToCore();
            }
            catch (RpcException)
            {
                yield break;
            }

            yield return step;
        }
    }

    /// <summary>Служба без этого вызова (старая) или без связи — ничего: IP останется прежним.</summary>
    public async Task MeasureIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.MeasureIpAsync(new V1.MeasureIpRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // Нет связи или старая служба.
        }
    }

    public async IAsyncEnumerable<UpdateProgress> DownloadUpdateAsync(string version, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.DownloadUpdate(new V1.DownloadUpdateRequest { Version = version }, cancellationToken: cancellationToken);
        var stream = call.ResponseStream;
        while (true)
        {
            UpdateProgress progress;
            try
            {
                if (!await stream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                progress = stream.Current.ToCore();
            }
            catch (RpcException e) when (e.StatusCode != StatusCode.Cancelled)
            {
                progress = new UpdateProgress(0, 0, Error: e.Status.Detail);
            }

            yield return progress;
            if (progress.Ready || progress.Failed)
            {
                yield break;
            }
        }
    }

    public async Task<int?> InstallUpdateAsync(string version, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _client.InstallUpdateAsync(new V1.InstallUpdateRequest { Version = version }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.InstallerPid > 0 ? reply.InstallerPid : null;
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
            Apply((await _client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore());
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
            if (reply.Until is not null)
            {
                Apply((await _client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore());
            }

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
            Apply((await _client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: cancellationToken).ConfigureAwait(false)).ToCore());
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
            if (_lastServiceSince is { } known && status.Since < known)
            {
                return;
            }

            if (_receivedStatus && status == _status)
            {
                return;
            }

            _status = status;
            _receivedStatus = true;
            _lastServiceSince = status.Since;
            if (status is { UserProxyManagedByWindow: true, LocalProxyPort: > 0 and var activePort })
            {
                Volatile.Write(ref _ownedProxyPort, activePort);
            }
            else if (status.State is ConnectionState.Off or ConnectionState.Connected || status.IsPaused)
            {
                Volatile.Write(ref _ownedProxyPort, 0);
            }
        }

        StatusChanged?.Invoke(this, status);
        if (status.State == ConnectionState.Connected && _userProxy is not null && Interlocked.Exchange(ref _watchingProxy, 1) == 0)
        {
            _ = WatchProxyAsync();
        }
    }

    /// <summary>
    /// Пока «Защищено» — системный прокси пользователя: поставила его другая программа (VPN- или прокси-клиент, включенный
    /// рядом) — служба проверит, слушает ли его вход другая программа, и остановит подключение (решение 511). Разрешена работа
    /// рядом с другим VPN — не смотрит.
    /// </summary>
    private async Task WatchProxyAsync()
    {
        try
        {
            while (Status.State == ConnectionState.Connected)
            {
                await Task.Delay(_proxyWatch, _time, _stopping).ConfigureAwait(false);
                var settings = _settings();
                if (Status.State != ConnectionState.Connected || _allowOtherVpn || settings.Protection.AllowOtherVpn)
                {
                    continue;
                }

                if (await ForeignProxyAsync(settings).ConfigureAwait(false) is { Length: > 0 } address)
                {
                    await ReportForeignProxyAsync(address).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException)
        {
            // Окно закрывается или настройку не прочитать — посмотрим при следующем «Защищено».
        }
        finally
        {
            Volatile.Write(ref _watchingProxy, 0);
        }

        // Пока цикл выходил, могло прийти новое «Защищено».
        if (!_stopping.IsCancellationRequested && Status.State == ConnectionState.Connected && Interlocked.Exchange(ref _watchingProxy, 1) == 0)
        {
            _ = WatchProxyAsync();
        }
    }

    /// <summary>Сказать службе о системном прокси другой программы; служба старая (нет вызова) или не отвечает — ничего.</summary>
    private async Task ReportForeignProxyAsync(string address)
    {
        try
        {
            await _client.ReportForeignProxyAsync(new V1.ForeignProxyReport { Address = address }, cancellationToken: _stopping).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // Служба прежней версии или без связи — подключение идет как было.
        }
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

    /// <summary>
    /// Журнал службы не идет — в журнал окна строка, почему: иначе «Проверка» и отчет показывают пустой журнал, а «служба
    /// не отвечает» без причины (у пользователя на Windows 2026-10-29). Та же причина подряд — одной строкой.
    /// </summary>
    private void NoteUnreachable(RpcException e)
    {
        var detail = e.Status.Detail is { Length: > 200 } text ? text[..200] : e.Status.Detail;
        var message = $"Нет связи со службой: {e.StatusCode} — {detail}";
        JournalEntry entry;
        lock (_gate)
        {
            if (_journal.Count > 0 && _journal[^1].Message == message)
            {
                return;
            }

            entry = new JournalEntry(_time.GetUtcNow(), JournalLevel.Warning, "window", message);
            _journal.Add(entry);
            TrimJournal();
        }

        JournalAdded?.Invoke(this, entry);
    }

    /// <summary>
    /// Журнал окна — не длиннее <see cref="JournalLimit"/>: уходит самая старая обычная запись, а ошибки и предупреждения
    /// остаются, пока обычных хватает. При подробном «Журнале ядра» события ядер за минуту вытесняли ошибку первой попытки
    /// подключения, и отчет ее уже не показывал (у пользователя на Windows 2026-10-31). Вызывать под _gate.
    /// </summary>
    private void TrimJournal()
    {
        while (_journal.Count > JournalLimit)
        {
            var oldest = _journal.FindIndex(e => !e.IsProblem());
            _journal.RemoveAt(oldest < 0 ? 0 : oldest);
        }
    }

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
                        TrimJournal();
                    }

                    JournalAdded?.Invoke(this, journal);
                }
            }
            catch (RpcException e)
            {
                NoteUnreachable(e);
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
