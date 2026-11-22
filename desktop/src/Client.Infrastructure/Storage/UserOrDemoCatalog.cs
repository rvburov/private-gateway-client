using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Серверы для интерфейса: демо-серверы, пока своих нет, а после первого импорта — только свои.
/// Демо-серверы в базу не пишутся и после импорта не показываются; с настоящей службой их нет вовсе —
/// подключиться к ним она не может, список пуст до первого импорта. Пинг своих серверов меряет
/// служба (<see cref="IGatewayClient.MeasureLatencyAsync"/>), каталог его только записывает.
/// </summary>
public sealed class UserOrDemoCatalog : IServerCatalog, IDisposable
{
    private readonly SqliteServerCatalog _user;
    private readonly DemoServerCatalog _demo;
    private readonly IGatewayClient _gateway;
    private readonly Lock _latencyGate = new();
    private ConnectionStatus _lastStatus;
    private CancellationTokenSource? _refreshStop;
    private long _latencyGeneration;
    private string _catalogStamp;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private long _automaticGeneration;
    private long _lastMeasuredGeneration = -1;
    private bool _automaticAllowed;
    private bool _disposed;

    public UserOrDemoCatalog(SqliteServerCatalog user, DemoServerCatalog demo, IGatewayClient gateway, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(gateway);
        _user = user;
        _demo = demo;
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;
        _lastStatus = gateway.Status;
        _automaticAllowed = gateway.CanAutomaticallyMeasureLatency;
        _catalogStamp = CatalogStamp();
        _user.InvalidateLatency();
        gateway.StatusChanged += OnStatusChanged;
        user.Changed += OnUserChanged;
        demo.Changed += OnDemoChanged;
        QueueAutomaticRefresh();
    }

    public event EventHandler? Changed;

    /// <summary>Показываются демо-серверы: своих пока нет, а подключение — имитация.</summary>
    public bool IsDemo => _gateway.Service == ServiceKind.Simulated && _user.Servers.Count == 0;

    public IReadOnlyList<ServerProfile> Servers => Current.Servers;

    public IReadOnlyList<ServerSubscription> Subscriptions => Current.Subscriptions;

    public ServerProfile? Selected => Current.Selected;

    public void Choose(string serverId) => Current.Choose(serverId);

    public void SetFavorite(string serverId, bool isFavorite) => Current.SetFavorite(serverId, isFavorite);

    public void Rename(string serverId, string name) => Current.Rename(serverId, name);

    public void Remove(string serverId) => Current.Remove(serverId);

    public void RememberExit(string serverId, string ip) => Current.RememberExit(serverId, ip);

    private int _measuring;

    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(null, cancellationToken);

    private async Task<bool> RefreshCoreAsync(long? automaticGeneration, CancellationToken cancellationToken)
    {
        if (IsDemo)
        {
            return await _demo.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _measuring, 1) != 0)
        {
            return false;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        long generation;
        lock (_latencyGate)
        {
            if (automaticGeneration is { } expected && expected != Volatile.Read(ref _automaticGeneration))
            {
                Volatile.Write(ref _measuring, 0);
                return false;
            }
            _refreshStop = stop;
            generation = _latencyGeneration;
        }
        _user.SetMeasuringLatency(true);
        try
        {
            var any = false;
            await foreach (var item in _gateway.MeasureLatencyProgressAsync([.. _user.Servers.Select(s => s.Id)], stop.Token).ConfigureAwait(false))
            {
                lock (_latencyGate)
                {
                    if (generation != _latencyGeneration || stop.IsCancellationRequested)
                    {
                        break;
                    }
                    _user.SetLatency(new Dictionary<string, LatencyResult> { [item.Key] = item.Value });
                    any = true;
                }
            }
            if (any && generation == Volatile.Read(ref _latencyGeneration))
            {
                Volatile.Write(ref _lastMeasuredGeneration, generation);
            }
            return any;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Смена сети или подключения отменила старый замер; это не отказ серверов и не ошибка окна.
            return false;
        }
        finally
        {
            lock (_latencyGate)
            {
                if (ReferenceEquals(_refreshStop, stop))
                {
                    _refreshStop = null;
                }
            }
            _user.SetMeasuringLatency(false);
            Volatile.Write(ref _measuring, 0);
        }
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        var schedule = false;
        lock (_latencyGate)
        {
            var automaticAllowed = _gateway.CanAutomaticallyMeasureLatency;
            var accessChanged = automaticAllowed != _automaticAllowed;
            _automaticAllowed = automaticAllowed;
            var changed = _lastStatus.State != status.State || _lastStatus.Mode != status.Mode || _lastStatus.ServerId != status.ServerId
                || _lastStatus.Since != status.Since
                || _lastStatus.NetworkChangedAt != status.NetworkChangedAt || _lastStatus.NetworkUnavailable != status.NetworkUnavailable
                || _lastStatus.LocalSiteFilter != status.LocalSiteFilter
                || _lastStatus.CaptivePauseUntil != status.CaptivePauseUntil;
            var connectionFailure = status.ErrorCode switch
            {
                ErrorCodes.ServerNotResponding => new LatencyResult(null, LatencyFailure.NoResponse, "Connection"),
                ErrorCodes.ServerNotFound => new LatencyResult(null, LatencyFailure.NoResponse, "NameResolution"),
                ErrorCodes.UnsupportedServer => LatencyResult.Failed(LatencyFailure.Unsupported),
                ErrorCodes.CoreFailed => LatencyResult.Failed(LatencyFailure.CoreFailed),
                _ => null,
            };
            var failed = connectionFailure is not null && status.ServerId is not null
                && (_lastStatus.ErrorCode != status.ErrorCode || _lastStatus.ServerId != status.ServerId || _lastStatus.Since != status.Since);
            _lastStatus = status;
            if (changed || accessChanged && !automaticAllowed)
            {
                _latencyGeneration++;
                _refreshStop?.Cancel();
                _user.InvalidateLatency();
            }
            else
            {
                _user.InvalidateLatency(expiredOnly: true);
            }
            if (failed)
            {
                _user.SetLatency(new Dictionary<string, LatencyResult>
                {
                    [status.ServerId!] = connectionFailure!,
                });
            }
            schedule = changed || accessChanged;
        }
        if (schedule)
        {
            QueueAutomaticRefresh();
        }
    }

    private void OnUserChanged(object? sender, EventArgs e)
    {
        var schedule = false;
        lock (_latencyGate)
        {
            var stamp = CatalogStamp();
            if (_catalogStamp != stamp)
            {
                _catalogStamp = stamp;
                _latencyGeneration++;
                _refreshStop?.Cancel();
                _user.InvalidateLatency();
                schedule = true;
            }
        }
        Changed?.Invoke(this, e);
        if (schedule)
        {
            QueueAutomaticRefresh();
        }
    }

    private void OnDemoChanged(object? sender, EventArgs e)
    {
        if (IsDemo)
        {
            Changed?.Invoke(this, e);
        }
    }

    private void QueueAutomaticRefresh()
    {
        var generation = Interlocked.Increment(ref _automaticGeneration);
        if (!_disposed && _gateway.Service is not (ServiceKind.Simulated or ServiceKind.None) && _gateway.HasReceivedStatus)
        {
            _ = AutomaticallyRefreshAsync(generation);
        }
    }

    private async Task AutomaticallyRefreshAsync(long generation)
    {
        try
        {
            // Один замер после успокоения изменений; тики счетчиков и результат самого замера его не запускают.
            await Task.Delay(TimeSpan.FromSeconds(1), _time, _lifetimeToken).ConfigureAwait(false);
            for (var waiting = 0; waiting < 150 && generation == Volatile.Read(ref _automaticGeneration); waiting++)
            {
                if (_disposed || !_gateway.CanAutomaticallyMeasureLatency || _gateway.Service == ServiceKind.Simulated
                    || _user.Servers.Count == 0 || _gateway.Status.NetworkUnavailable || _gateway.Status.IsPaused
                    || _gateway.Status.State is not (ConnectionState.Connected or ConnectionState.Off or ConnectionState.Blocked))
                {
                    return;
                }
                if (Volatile.Read(ref _measuring) == 0)
                {
                    if (Volatile.Read(ref _lastMeasuredGeneration) != Volatile.Read(ref _latencyGeneration))
                    {
                        await RefreshCoreAsync(generation, _lifetimeToken).ConfigureAwait(false);
                    }
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time, _lifetimeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Фоновая попытка не открывает диалог и не повторяется по собственному отказу. Кнопка остается доступна.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Interlocked.Increment(ref _automaticGeneration);
        _gateway.StatusChanged -= OnStatusChanged;
        _user.Changed -= OnUserChanged;
        _demo.Changed -= OnDemoChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // Только память процесса: адреса и имена в журнал не попадают. Изменение состава/настроек подписки отменяет старую пробу.
    private string CatalogStamp() => string.Join('|', _user.Subscriptions.Select(s => s.Id + ":" + s.UpdatedAt?.ToUnixTimeMilliseconds()))
        + ";" + string.Join('|', _user.Servers.Select(s => $"{s.Id}:{s.Host}:{s.Port}:{s.Protocol}:{s.Transport}:{s.Security}"));

    private IServerCatalog Current => IsDemo ? _demo : _user;
}
