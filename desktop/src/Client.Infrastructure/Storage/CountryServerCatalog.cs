using System.Collections.Concurrent;
using System.Net;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Countries;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Страна адреса подключения из локальной базы, независимо от источника подписки и эмодзи в имени.
/// Имена разрешает только служба через существующий IPC DNS. Частные адреса и неоднозначные ответы — без страны.
/// </summary>
public sealed class CountryServerCatalog : IServerCatalog, IDisposable
{
    private readonly IServerCatalog _inner;
    private readonly IGatewayClient _gateway;
    private readonly TimeProvider _time;
    private readonly Func<IPAddress, string?> _lookup;
    private readonly ConcurrentDictionary<string, CachedCountry> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private bool _disposed;

    public CountryServerCatalog(IServerCatalog inner, IGatewayClient gateway, TimeProvider time, Func<IPAddress, string?>? lookup = null)
    {
        _inner = inner;
        _gateway = gateway;
        _time = time;
        _lookup = lookup ?? CountryDatabase.Lookup;
        inner.Changed += InnerChanged;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<ServerProfile> Servers => [.. _inner.Servers.Select(WithCountry)];
    public IReadOnlyList<ServerSubscription> Subscriptions => _inner.Subscriptions;
    public ServerProfile? Selected => _inner.Selected is { } server ? WithCountry(server) : null;
    public void Choose(string serverId) => _inner.Choose(serverId);
    public void SetFavorite(string serverId, bool isFavorite) => _inner.SetFavorite(serverId, isFavorite);
    public void Rename(string serverId, string name) => _inner.Rename(serverId, name);
    public void Remove(string serverId) => _inner.Remove(serverId);
    public void RememberExit(string serverId, string ip) => _inner.RememberExit(serverId, ip);
    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => _inner.RefreshAsync(cancellationToken);

    /// <summary>Запускается после показа окна. Частота DNS не связана с перерисовкой строк.</summary>
    public void Start() => _worker ??= Task.Run(WatchAsync);

    private ServerProfile WithCountry(ServerProfile server)
    {
        if (_gateway.Service == ServiceKind.Simulated)
        {
            return server;
        }

        var country = IPAddress.TryParse(server.Host, out var ip) ? _lookup(ip)
            : _hosts.TryGetValue(server.Host, out var entry) && entry.Expires > _time.GetUtcNow() ? entry.Country : null;
        return server with { CountryCode = country ?? string.Empty };
    }

    /// <summary>Не более двух DNS одновременно, повтор удачного ответа через 10 минут, неудачного — через минуту.</summary>
    public async Task RefreshCountriesAsync(CancellationToken cancellationToken = default)
    {
        if (_gateway.Service is ServiceKind.Simulated or ServiceKind.None
            || !CanResolve())
        {
            return;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var hosts = _inner.Servers.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in _hosts.Keys.Where(h => !hosts.Contains(h)))
            {
                _hosts.TryRemove(old, out _);
            }

            var pending = hosts.Where(h => !IPAddress.TryParse(h, out _)
                && ProbeTarget.Normalize(h) is not null
                && (!_hosts.TryGetValue(h, out var entry) || entry.Expires <= now)).ToArray();
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                async (host, token) =>
                {
                    if (!CanResolve())
                    {
                        return;
                    }

                    string? country = null;
                    var found = false;
                    var ambiguous = false;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(7));
                    try
                    {
                        await foreach (var step in _gateway.ProbeAsync(ProbeKind.Dns, host, 0, timeout.Token).ConfigureAwait(false))
                        {
                            var next = step.Outcome == ProbeOutcome.Ok && step.Address is { } address ? _lookup(address) : null;
                            ambiguous |= next is null || (found && country != next);
                            country = next;
                            found = true;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        ambiguous = true;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                        ambiguous = true;
                    }

                    token.ThrowIfCancellationRequested();
                    var result = found && !ambiguous ? country : null;
                    _hosts[host] = new CachedCountry(result, _time.GetUtcNow() + TimeSpan.FromMinutes(result is null ? 1 : 10));
                }).ConfigureAwait(false);
            if (pending.Length > 0 && !_disposed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool CanResolve() => _gateway.Status.State is ConnectionState.Connected
        || (_gateway.Status.State is ConnectionState.Off && !_gateway.Status.StrictBlock);

    private async Task WatchAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await RefreshCountriesAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private void InnerChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Changed -= InnerChanged;
        _stop.Cancel();
        // Рабочая задача еще может завершать IPC и освобождать семафор. Освобождаем после ее завершения.
        if (_worker is { } worker)
        {
            _ = worker.ContinueWith(_ => { _stop.Dispose(); _refresh.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            _stop.Dispose();
            _refresh.Dispose();
        }
    }

    private sealed record CachedCountry(string? Country, DateTimeOffset Expires);
}
