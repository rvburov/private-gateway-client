using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Служба для окна — та, что нашлась при запуске или позже. Окно просит подключиться, а службы нет —
/// ищет снова; в режиме прокси запускает службу пользователя сама. Не нашлась — «Ошибка» с
/// PGC-SVC-001 (экран «Служба не запущена»), запись в журнале, проверок и пинга нет. Служба пропала
/// (та же ошибка от нее самой) — следующее подключение ищет заново: могла появиться другая. Остался
/// снимок системного прокси после сбоя службы пользователя — окно сразу поднимает ее, чтобы она
/// вернула настройки: иначе браузеры остались бы направлены на неработающий прокси.
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
    private IGatewayClient? _service;
    private ConnectionStatus _status;

    /// <param name="locator">Где искать службу и как запустить службу пользователя.</param>
    /// <param name="attach">Клиент службы по найденному адресу.</param>
    /// <param name="settings">Текущие настройки: режим решает, запускать ли службу пользователя.</param>
    /// <param name="time">Часы программы.</param>
    /// <param name="staleProxy">Остался ли снимок системного прокси после сбоя службы пользователя.</param>
    public GatewayHost(ServiceLocator locator, Func<ServiceAddress, IGatewayClient> attach, Func<AppSettings> settings, TimeProvider time,
        Func<bool>? staleProxy = null)
    {
        _locator = locator;
        _attach = attach;
        _settings = settings;
        _time = time;
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
                return _service?.Status ?? _status;
            }
        }
    }

    public ServiceKind Service => Current?.Service ?? ServiceKind.None;

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

    public async Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service)
        {
            await service.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            return;
        }

        Missing(server.Id, _settings().Mode);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Current is { } service)
        {
            await service.DisconnectAsync(cancellationToken).ConfigureAwait(false);
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

    public Task PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Current?.PauseForCaptivePortalAsync(duration, cancellationToken) ?? Task.CompletedTask;

    public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) =>
        Current?.ResumeProtectionAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Проверки — у службы; ее нет — одна проверка «служба» с причиной «не найдена». Службу пользователя ради проверок не запускает.</summary>
    public async Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: false, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.RunChecksAsync(cancellationToken).ConfigureAwait(false)
            : new DiagnosticReport([new DiagnosticResult(DiagnosticCheck.Service, CheckState.Stopped, "not_found")], _time.GetUtcNow());

    /// <summary>Пинг меряет служба; в режиме прокси ради него запускается служба пользователя.</summary>
    public async Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
        await EnsureAsync(launch: true, cancellationToken).ConfigureAwait(false) is { } service
            ? await service.MeasureLatencyAsync(serverIds, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, LatencyResult>();

    public void Dispose()
    {
        (Current as IDisposable)?.Dispose();
        _finding.Dispose();
    }

    /// <summary>
    /// Служба для просьбы окна: найденная раньше, если она на связи; иначе — поиск заново, а в режиме
    /// прокси и при <paramref name="launch"/> — запуск службы пользователя. null — службы нет.
    /// </summary>
    private async Task<IGatewayClient?> EnsureAsync(bool launch, CancellationToken cancellationToken)
    {
        if (Current is { } current && current.Status.ErrorCode != ErrorCodes.ServiceNotRunning)
        {
            return current;
        }

        await _finding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current is { } found && found.Status.ErrorCode != ErrorCodes.ServiceNotRunning)
            {
                return found;
            }

            if (await Task.Run(_locator.Find, cancellationToken).ConfigureAwait(false) is { } address)
            {
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

    /// <summary>Своя запись окна; та же, что последняя, не повторяется — проверки ищут службу при каждом открытии экрана.</summary>
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
        if (ReferenceEquals(sender, Current))
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    private void OnJournalAdded(object? sender, JournalEntry entry)
    {
        if (ReferenceEquals(sender, Current))
        {
            JournalAdded?.Invoke(this, entry);
        }
    }
}
