using System.Security;
using System.Text.Json;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Прокси текущего пользователя окна: HKCU установленной службе не принадлежит. Готовность берется из статуса службы,
/// а не из желаемого порта настроек. После сбоя ядра настройки удерживаются, чтобы браузер не переключился напрямую.
/// </summary>
public sealed class UserProxyController : IUserProxyController, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IGatewayClient _gateway;
    private readonly ISystemProxy? _proxy;
    private readonly UserProxyLease? _lease;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private bool _started;
    private bool _changedByOther;
    private bool _explicitDisableRequested;
    private AppSettings? _lastSettings;
    private UserProxyStatus _status = new(UserProxyState.Off);

    public UserProxyController(SettingsService settings, IGatewayClient gateway, ISystemProxy? proxy, IUserProxyBackupStore store, TimeProvider time)
    {
        _settings = settings;
        _gateway = gateway;
        _proxy = proxy;
        _lease = proxy is null ? null : new UserProxyLease(proxy, store);
        _time = time;
    }

    public UserProxyStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event EventHandler<UserProxyStatus>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _lastSettings = _settings.Current;
            _gateway.StatusChanged += OnStatus;
            _settings.Changed += OnSettings;
            // Проверяется факт применения, даже если другая программа сменила HKCU без события нашей службы.
            _timer = _time.CreateTimer(_ => Reconcile(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        Reconcile();
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_gateway.Service == ServiceKind.User)
            {
                try
                {
                    _lease?.Release();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException or FormatException)
                {
                    _status = new UserProxyStatus(UserProxyState.Error);
                }
            }

            _timer?.Dispose();
            _timer = null;
            _gateway.StatusChanged -= OnStatus;
            _settings.Changed -= OnSettings;
            _started = false;
            // Выход окна не означает отключение VPN: работающая установленная служба остается владельцем входа.
            // Снимок переживет аварию/выход окна и будет согласован с реальным статусом при следующем запуске.
        }
    }

    public void Dispose() => Shutdown();

    private void OnStatus(object? sender, ConnectionStatus status)
    {
        Reconcile();
    }

    private void OnSettings(object? sender, AppSettings settings)
    {
        lock (_gate)
        {
            var previous = _lastSettings ?? settings;
            _lastSettings = settings;
            if (previous.Mode != settings.Mode || settings.Connection.UsesSystemProxy(settings.Mode))
            {
                _explicitDisableRequested = false;
            }
            else if (previous.Connection.UsesSystemProxy(settings.Mode))
            {
                // Явное выключение в том же режиме — разрешение вернуть HKCU даже при оборванном IPC.
                // Сохраненный при старте false и переключение режима такого разрешения не дают.
                _explicitDisableRequested = true;
            }
        }

        Reconcile();
    }

    private void Reconcile()
    {
        UserProxyStatus next;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                next = ReconcileCore();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException or FormatException)
            {
                // Снимок не удаляется при ошибке возврата. Повторный запуск сможет восстановить настройки.
                next = new UserProxyStatus(UserProxyState.Error);
            }

            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        Changed?.Invoke(this, next);
    }

    private UserProxyStatus ReconcileCore()
    {
        var settings = _settings.Current;
        var status = _gateway.Status;
        if (!_gateway.HasReceivedStatus && !_explicitDisableRequested && _lease?.Port is not null)
        {
            return new UserProxyStatus(_lease.IsOwned ? UserProxyState.Held : UserProxyState.Waiting, _lease.Port);
        }

        // Смена режима еще не подняла TUN: прежний proxy-only нельзя снимать в 700 мс ожидания ProtectionSync.
        var wanted = settings.Connection.UsesSystemProxy(settings.Mode)
            || settings.Mode != status.Mode && settings.Connection.UsesSystemProxy(status.Mode);
        if (_explicitDisableRequested || !wanted || status.IsPaused || _gateway.HasReceivedStatus && status.State == ConnectionState.Off)
        {
            var changed = _lease?.WasChanged == true;
            var restored = _lease?.Release() == true;
            _changedByOther = false;
            return new UserProxyStatus(changed ? UserProxyState.ChangedByOther : restored ? UserProxyState.Restored : UserProxyState.Off);
        }

        if (_proxy is null || _lease is null)
        {
            return new UserProxyStatus(status.State == ConnectionState.Off ? UserProxyState.Waiting : UserProxyState.ServiceManaged);
        }

        if (!status.UserProxyManagedByWindow && _lease.Port is null)
        {
            // Служба пользователя управляет своим HKCU сама: два владельца одного снимка недопустимы.
            return new UserProxyStatus(status.State == ConnectionState.Connected
                ? _gateway.Service == ServiceKind.User ? UserProxyState.ServiceManaged : UserProxyState.Unsupported
                : UserProxyState.Waiting, status.LocalProxyPort);
        }

        if (_gateway.Service is not (ServiceKind.Installed or ServiceKind.User))
        {
            return new UserProxyStatus(UserProxyState.Waiting);
        }

        if (_changedByOther)
        {
            return new UserProxyStatus(UserProxyState.ChangedByOther);
        }

        if (status.State == ConnectionState.Connected && status.UserProxyManagedByWindow && status.LocalProxyPort is > 0 and var port)
        {
            if (!_lease.Apply(port, _gateway.Service))
            {
                _changedByOther = true;
                return new UserProxyStatus(UserProxyState.ChangedByOther);
            }

            return new UserProxyStatus(UserProxyState.Applied, port);
        }

        if (_lease.IsOwned)
        {
            return new UserProxyStatus(UserProxyState.Held, _lease.Port);
        }

        if (_lease.WasChanged)
        {
            _lease.Release();
            _changedByOther = true;
            return new UserProxyStatus(UserProxyState.ChangedByOther);
        }

        return new UserProxyStatus(status.State == ConnectionState.Connected ? UserProxyState.Unsupported : UserProxyState.Waiting);
    }
}
