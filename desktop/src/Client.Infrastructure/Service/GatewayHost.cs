using System.Net;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Служба для окна — та, что нашлась при запуске или позже. Окно просит подключиться, а службы нет —
/// ищет снова; в режиме прокси запускает службу пользователя сама. Не нашлась — «Ошибка» с
/// PGC-SVC-001 (экран «Служба не запущена»), запись в журнале, проверок и пинга нет. Служба пропала
/// (та же ошибка от нее самой) — следующее подключение ищет заново: могла появиться другая. Остался
/// снимок системного прокси после сбоя службы пользователя — окно сразу поднимает ее, чтобы она
/// вернула настройки: иначе браузеры остались бы направлены на неработающий прокси. Без действующего входа в
/// аккаунт сайта подключение не начинается ни одним путем (кнопка, трей, уведомление, «Подключаться при запуске»):
/// отказ с PGC-ACC-001 (<see cref="Refuse"/>), служба не вызывается и не запускается; «Вернуть защиту» после паузы
/// для входа в Wi-Fi без доступа выключает защиту.
/// </summary>
public sealed class GatewayHost : IGatewayClient, IDisposable
{
    private readonly ServiceLocator _locator;
    private readonly Func<ServiceAddress, IGatewayClient> _attach;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _journal = [];
    private readonly SemaphoreSlim _finding = new(1, 1);
    private readonly IUserProxySettings? _userProxy;
    private readonly Func<ServerProfile?>? _selectedTarget;
    private readonly Func<bool>? _mayConnect;
    private long _connectIntent;
    private IGatewayClient? _service;
    private ConnectionStatus _status;

    // Отказ без доступа поверх состояния службы: служба его не знает, а показать его надо. Снимается, когда служба
    // сменила состояние, при отключении и при разрешенном подключении.
    private ConnectionStatus? _refused;
    private ConnectionState _refusedFrom;

    /// <param name="locator">Где искать службу и как запустить службу пользователя.</param>
    /// <param name="attach">Клиент службы по найденному адресу.</param>
    /// <param name="settings">Текущие настройки: режим решает, запускать ли службу пользователя.</param>
    /// <param name="time">Часы программы.</param>
    /// <param name="staleProxy">Остался ли снимок системного прокси после сбоя службы пользователя.</param>
    /// <param name="userProxy">Системный прокси пользователя — для проверки «Другие VPN и прокси»; null — не смотреть.</param>
    /// <param name="selectedTarget">Текущая цель режима: не подключать старую цель после ожидания запуска службы.</param>
    /// <param name="mayConnect">Есть ли действующий вход в аккаунт сайта; null — вход не нужен.</param>
    public GatewayHost(ServiceLocator locator, Func<ServiceAddress, IGatewayClient> attach, Func<AppSettings> settings, TimeProvider time,
        Func<bool>? staleProxy = null, IUserProxySettings? userProxy = null, Func<ServerProfile?>? selectedTarget = null,
        Func<bool>? mayConnect = null)
    {
        _locator = locator;
        _attach = attach;
        _settings = settings;
        _time = time;
        _userProxy = userProxy;
        _selectedTarget = selectedTarget;
        _mayConnect = mayConnect;
        _status = ConnectionStatus.Initial(time.GetUtcNow());
        if (locator.Find() is { } address)
        {
            Attach(address);
        }
        else if (staleProxy?.Invoke() == true)
        {
            _ = RecoverProxyAsync();
        }
    }

    public ConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _refused ?? _service?.Status ?? _status;
            }
        }
    }

    public ServiceKind Service => Current?.Service ?? ServiceKind.None;

    public bool HasReceivedStatus => Current?.HasReceivedStatus ?? false;

    public int? ManagedUserProxyPort => Current?.ManagedUserProxyPort;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Свои записи окна (поиск и запуск службы) и журнал службы.</summary>
    public IReadOnlyList<JournalEntry> Journal
    {
        get
        {
            lock (_gate)
            {
                return [.. _journal, .. _service?.Journal ?? []];
            }
        }
    }

    public event EventHandler<JournalEntry>? JournalAdded;

    private IGatewayClient? Current
    {
        get
        {
            lock (_gate)
            {
                return _service;
            }
        }
    }

    public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) =>
        ConnectCurrentAsync(server, false, cancellationToken);

    public Task ConnectDespiteOtherVpnAsync(ServerProfile server, CancellationToken cancellationToken = default) =>
        ConnectCurrentAsync(server, true, cancellationToken);

    private async Task ConnectCurrentAsync(ServerProfile server, bool despiteOtherVpn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var settings = _settings();
        var intent = Interlocked.Increment(ref _connectIntent);
        if (_mayConnect?.Invoke() == false)
        {
            Refuse(server.Id, settings.Mode);
            return;
        }

        ClearRefusal();
        var service = await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (intent != Volatile.Read(ref _connectIntent) || ProtectionSync.Affects(settings, _settings())
            || (_selectedTarget is not null && _selectedTarget()?.Id != server.Id))
        {
            return;
        }

        if (service is not null)
        {
            if (despiteOtherVpn)
            {
                await service.ConnectDespiteOtherVpnAsync(server, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await service.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        Missing(server.Id, settings.Mode);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => DisconnectAsync(open: false, cancellationToken);

    /// <summary>Как <see cref="DisconnectAsync(CancellationToken)"/>, но служба снимает и блок строгого режима.</summary>
    public Task DropProtectionAsync(CancellationToken cancellationToken = default) => DisconnectAsync(open: true, cancellationToken);

    /// <summary>Вошли в аккаунт: отказ PGC-ACC-001 больше не правда — показать состояние службы (без нее — «Выключено»).</summary>
    public void AccessGranted()
    {
        ClearRefusal();
        ConnectionStatus status;
        lock (_gate)
        {
            if (_service is not null || _status is not { State: ConnectionState.Error, ErrorCode: ErrorCodes.AccessRequired })
            {
                return;
            }

            status = _status = _status.MoveTo(ConnectionState.Off, _time.GetUtcNow());
        }

        StatusChanged?.Invoke(this, status);
    }

    private async Task DisconnectAsync(bool open, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectIntent);
        ClearRefusal();
        if (Current is { } service)
        {
            await (open ? service.DropProtectionAsync(cancellationToken) : service.DisconnectAsync(cancellationToken)).ConfigureAwait(false);
            return;
        }

        ConnectionStatus status;
        lock (_gate)
        {
            if (_status.State == ConnectionState.Off)
            {
                return;
            }

            status = _status = _status.MoveTo(ConnectionState.Off, _time.GetUtcNow());
        }

        StatusChanged?.Invoke(this, status);
    }

    public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Current?.PauseForCaptivePortalAsync(duration, cancellationToken) ?? Task.FromResult(false);

    /// <summary>Без доступа к аккаунту защиту не вернуть — пауза кончается выключением защиты с открытой сетью.</summary>
    public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) =>
        _mayConnect?.Invoke() == false ? DropProtectionAsync(cancellationToken)
            : Current?.ResumeProtectionAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Проверки — у службы; ее нет — одна проверка «служба» с причиной «не найдена». Службу пользователя ради
    /// проверок не запускает. Системный прокси пользователя служба не видит — его добавляет окно.
    /// </summary>
    public async Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default)
    {
        var report = await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.RunChecksAsync(cancellationToken).ConfigureAwait(false)
            : new DiagnosticReport([new DiagnosticResult(DiagnosticCheck.Service, CheckState.Stopped, "not_found")], _time.GetUtcNow());
        return _userProxy is null
            ? report
            : UserProxy.WithUserProxy(report, await Task.Run(_userProxy.Read, cancellationToken).ConfigureAwait(false), _settings(), ManagedUserProxyPort);
    }

    /// <summary>Пинг меряет служба; в режиме прокси ради него запускается служба пользователя.</summary>
    public async Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.MeasureLatencyAsync(serverIds, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, LatencyResult>();

    /// <summary>Сведения службы — той, что найдена (службу пользователя ради них не запускает); нет — null.</summary>
    public async Task<ServiceDetails?> GetDetailsAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.GetDetailsAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async Task<SubscriptionDownload> DownloadSubscriptionAsync(Uri address, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.DownloadSubscriptionAsync(address, cancellationToken).ConfigureAwait(false)
            : SubscriptionDownload.Failed(DownloadFailure.Connection);

    /// <summary>Обновление ищет найденная служба; службу пользователя ради проверки не запускает.</summary>
    public async Task<UpdateOffer?> CheckForUpdateAsync(UpdateChannel channel, bool force, byte[]? siteFeed, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.CheckForUpdateAsync(channel, force, siteFeed, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Сеть машины читает служба; в режиме прокси ради инспектора запускается служба пользователя.</summary>
    public async Task<NetworkSnapshot?> InspectNetworkAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.InspectNetworkAsync(cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>О провайдере адреса спрашивает служба; как и для инспектора, в режиме прокси запускается служба пользователя.</summary>
    public async Task<AddressInfo?> DescribeAddressAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.DescribeAddressAsync(address, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Замер делает служба; как и для инспектора, в режиме прокси запускается служба пользователя.</summary>
    public async IAsyncEnumerable<ProbeStep> ProbeAsync(ProbeKind kind, string target, int port,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is not { } service)
        {
            yield break;
        }

        await foreach (var step in service.ProbeAsync(kind, target, port, cancellationToken).ConfigureAwait(false))
        {
            yield return step;
        }
    }

    /// <summary>Сокеты читает служба; в режиме прокси ради инспектора запускается служба пользователя.</summary>
    public async Task<IReadOnlyList<NetworkConnection>?> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.ListConnectionsAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async IAsyncEnumerable<TrafficSample> WatchTrafficAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is not { } service)
        {
            yield break;
        }

        await foreach (var sample in service.WatchTrafficAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    /// <summary>Задержку меряет служба, которая держит подключение; ради замера службу не запускать.</summary>
    public async Task<DelayBreakdown?> MeasureDelayAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.MeasureDelayAsync(cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Входы прокси и другие программы проверяет служба, которая есть сейчас; ради этого службу пользователя не запускать:
    /// без службы блок «Что на этом компьютере может мешать» покажет настройки без проверки входов.
    /// </summary>
    public async Task<InterferenceProbe?> CheckEntriesAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.CheckEntriesAsync(addresses, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Сравнение делает служба; в режиме прокси ради инспектора запускается служба пользователя.</summary>
    public async Task<NetworkComparison?> CompareNetworkAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.CompareNetworkAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async Task MeasureIpAsync(CancellationToken cancellationToken = default)
    {
        if (await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service)
        {
            await service.MeasureIpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Скачивает и ставит обновление только найденная служба — та, что его нашла.</summary>
    public IAsyncEnumerable<UpdateProgress> DownloadUpdateAsync(string version, byte[]? siteFeed, CancellationToken cancellationToken = default) =>
        Current?.DownloadUpdateAsync(version, siteFeed, cancellationToken) ?? AsyncEnumerable.Empty<UpdateProgress>();

    public Task<int?> InstallUpdateAsync(string version, CancellationToken cancellationToken = default) =>
        Current?.InstallUpdateAsync(version, cancellationToken) ?? Task.FromResult<int?>(null);

    public void Dispose()
    {
        (Current as IDisposable)?.Dispose();
        _finding.Dispose();
    }

    /// <summary>
    /// Служба для просьбы окна: найденная раньше, если она на связи; иначе — поиск заново, а в режиме
    /// прокси и при <paramref name="launch"/> — запуск службы пользователя. null — службы нет. Просят
    /// VPN, а отвечает служба пользователя, — окно ищет установленную (ее могли только что поставить).
    /// </summary>
    private async Task<IGatewayClient?> EnsureAsync(bool launch, CancellationToken cancellationToken)
    {
        if (Current is { } current && Usable(current))
        {
            return current;
        }

        await _finding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current is { } found && Usable(found))
            {
                return found;
            }

            if (await Task.Run(_locator.Find, cancellationToken).ConfigureAwait(false) is { } address)
            {
                if (Current is { Service: ServiceKind.User } same && !address.System)
                {
                    return same;
                }

                // Служба пользователя уступает установленной: сначала отключиться — системный прокси вернется, как был.
                if (Current is { Service: ServiceKind.User } previous)
                {
                    Log(JournalLevel.Info, "Нашлась установленная служба — окно переходит на нее со службы пользователя");
                    await previous.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }

                return Attach(address);
            }

            // Пропавшая служба остается: ее клиент сам покажет, что она не отвечает.
            if (!launch || _settings().Mode != ConnectionMode.Proxy)
            {
                Log(JournalLevel.Warning, "Служба не найдена: ни установленной, ни службы пользователя (PGC-SVC-001)");
                return Current;
            }

            var (user, launched) = await _locator.StartUserServiceAsync(cancellationToken).ConfigureAwait(false);
            if (user is not null)
            {
                Log(JournalLevel.Info, "Запущена служба пользователя: режим прокси без установленной службы");
                return Attach(user);
            }

            Log(JournalLevel.Error, launched
                ? $"Служба пользователя не ответила за {ServiceLocator.UserServiceStart.TotalSeconds:0} с (PGC-SVC-001)"
                : "Служба пользователя не запустилась: файла службы нет рядом с программой (PGC-SVC-001)");
            return Current;
        }
        finally
        {
            _finding.Release();
        }
    }

    /// <summary>Служба на связи; для режима VPN служба пользователя не годится, если может найтись установленная.</summary>
    private bool Usable(IGatewayClient service) =>
        service.Status.ErrorCode != ErrorCodes.ServiceNotRunning && !(service.Service == ServiceKind.User && _settings().Mode == ConnectionMode.Vpn);

    /// <summary>Служба пользователя возвращает системный прокси, оставшийся после ее сбоя, сразу при запуске.</summary>
    private async Task RecoverProxyAsync()
    {
        await _finding.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is null && (await _locator.StartUserServiceAsync().ConfigureAwait(false)).Address is { } user)
            {
                Log(JournalLevel.Warning, "Системный прокси остался после сбоя службы пользователя — она запущена, чтобы вернуть его");
                Attach(user);
            }
        }
        finally
        {
            _finding.Release();
        }
    }

    /// <summary>Подключиться к службе по адресу; прежняя (пропавшая) закрывается.</summary>
    private IGatewayClient Attach(ServiceAddress address)
    {
        var service = _attach(address);
        IGatewayClient? previous;
        lock (_gate)
        {
            previous = _service;
            _service = service;
            _refused = null;
        }

        if (previous is not null)
        {
            previous.StatusChanged -= OnStatusChanged;
            previous.JournalAdded -= OnJournalAdded;
            (previous as IDisposable)?.Dispose();
        }

        service.StatusChanged += OnStatusChanged;
        service.JournalAdded += OnJournalAdded;
        StatusChanged?.Invoke(this, service.Status);
        return service;
    }

    /// <summary>
    /// Службы не было и нет — «Подключение» и сразу «Ошибка» с PGC-SVC-001. Защиты без службы нет:
    /// проверки — как при выключенной, трафик идет напрямую.
    /// </summary>
    private void Missing(string serverId, ConnectionMode mode)
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

            status = _status = (current with { ErrorCode = ErrorCodes.ServiceNotRunning }).MoveTo(ConnectionState.Error, now) with
            {
                Protection = ProtectionChecks.For(ConnectionState.Off),
            };
        }

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// Входа нет — отказ с PGC-ACC-001, служба не вызывается. Защита, которую служба держит сейчас (подключена,
    /// подключается), не трогается и не перекрывается отказом: ее выключает сервис аккаунта (<c>AccountService</c>),
    /// пока доступа нет. Из «Выключено» — «Ошибка» через «Подключение», как <see cref="Missing"/>; из «Ошибки» — та же
    /// «Ошибка» с этим кодом. «Блокировка» остается «Блокировкой» с кодом причины: иначе щит в трее перестал бы снимать
    /// блок и только подключал бы снова — а подключение без входа опять отказ.
    /// </summary>
    private void Refuse(string serverId, ConnectionMode mode)
    {
        Log(JournalLevel.Warning, "Подключение не начато: нет действующего входа в аккаунт сайта (PGC-ACC-001)");
        ConnectionStatus status;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var current = (_service?.Status ?? _status) with { Mode = mode, ServerId = serverId };
            switch (current.State)
            {
                case ConnectionState.Off:
                    var connecting = current.MoveTo(ConnectionState.Connecting, now) with { ErrorCode = ErrorCodes.AccessRequired };
                    status = connecting.MoveTo(ConnectionState.Error, now) with { Protection = ProtectionChecks.For(ConnectionState.Off) };
                    break;
                case ConnectionState.Blocked:
                    status = current with { ErrorCode = ErrorCodes.AccessRequired };
                    break;
                case ConnectionState.Error:
                    status = current with { ErrorCode = ErrorCodes.AccessRequired, Since = now };
                    break;
                default:
                    return;
            }

            if (_service is null)
            {
                _status = status;
            }
            else
            {
                (_refused, _refusedFrom) = (status, _service.Status.State);
            }
        }

        StatusChanged?.Invoke(this, status);
    }

    private void ClearRefusal()
    {
        ConnectionStatus? status = null;
        lock (_gate)
        {
            if (_refused is not null)
            {
                _refused = null;
                status = _service?.Status;
            }
        }

        if (status is not null)
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    /// <summary>Своя запись окна; та же, что последняя, не повторяется — проверки ищут службу при каждом открытии экрана.</summary>
    public void ReportSubscriptionAttempt(DownloadFailure? failure, int? statusCode, int attempt, TimeSpan elapsed, bool throughCore) =>
        Log(failure is null ? JournalLevel.Info : JournalLevel.Warning,
            $"Загрузка списка серверов: попытка {attempt}, {(throughCore ? "через ядро" : "системная сеть")}, {elapsed.TotalSeconds:0.0} с; "
            + (failure is null ? "загружено" : $"причина: {failure}; HTTP: {statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "нет"}"));

    private void Log(JournalLevel level, string message)
    {
        var entry = new JournalEntry(_time.GetUtcNow(), level, "app", message);
        lock (_gate)
        {
            if (_journal is [.., var last] && last.Message == message)
            {
                return;
            }

            _journal.Add(entry);
        }

        JournalAdded?.Invoke(this, entry);
    }

    // Прежняя служба отписана при смене, но событие, начатое ею в этот же момент в другом потоке, окну не нужно.
    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (!ReferenceEquals(sender, Current))
        {
            return;
        }

        lock (_gate)
        {
            if (_refused is { } refused)
            {
                // То же состояние службы — отказ остается на экране; служба сменила состояние сама — показать ее.
                if (status.State == _refusedFrom)
                {
                    status = refused;
                }
                else
                {
                    _refused = null;
                }
            }
        }

        StatusChanged?.Invoke(this, status);
    }

    private void OnJournalAdded(object? sender, JournalEntry entry)
    {
        if (ReferenceEquals(sender, Current))
        {
            JournalAdded?.Invoke(this, entry);
        }
    }
}
