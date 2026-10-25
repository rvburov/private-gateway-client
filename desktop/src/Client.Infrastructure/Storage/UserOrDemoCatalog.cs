using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Серверы для интерфейса: демо-серверы, пока своих нет, а после первого импорта — только свои.
/// Демо-серверы в базу не пишутся и после импорта не показываются; с настоящей службой их нет вовсе —
/// подключиться к ним она не может, список пуст до первого импорта. Пинг своих серверов меряет
/// служба (<see cref="IGatewayClient.MeasureLatencyAsync"/>), каталог его только записывает.
/// </summary>
public sealed class UserOrDemoCatalog : IServerCatalog
{
    private readonly SqliteServerCatalog _user;
    private readonly DemoServerCatalog _demo;
    private readonly IGatewayClient _gateway;

    public UserOrDemoCatalog(SqliteServerCatalog user, DemoServerCatalog demo, IGatewayClient gateway)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(gateway);
        _user = user;
        _demo = demo;
        _gateway = gateway;
        user.Changed += (_, e) => Changed?.Invoke(this, e);
        demo.Changed += (_, e) =>
        {
            if (IsDemo)
            {
                Changed?.Invoke(this, e);
            }
        };
    }

    public event EventHandler? Changed;

    /// <summary>Показываются демо-серверы: своих пока нет, а подключение — имитация.</summary>
    public bool IsDemo => _gateway.IsSimulated && _user.Servers.Count == 0;

    public IReadOnlyList<ServerProfile> Servers => Current.Servers;

    public IReadOnlyList<ServerSubscription> Subscriptions => Current.Subscriptions;

    public ServerProfile? Selected => Current.Selected;

    public void Choose(string serverId) => Current.Choose(serverId);

    public void SetFavorite(string serverId, bool isFavorite) => Current.SetFavorite(serverId, isFavorite);

    public void Rename(string serverId, string name) => Current.Rename(serverId, name);

    public void Remove(string serverId) => Current.Remove(serverId);

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDemo)
        {
            return await _demo.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        var measured = await _gateway.MeasureLatencyAsync([.. _user.Servers.Select(s => s.Id)], cancellationToken).ConfigureAwait(false);
        if (measured.Count == 0)
        {
            await _user.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _user.SetLatency(measured.ToDictionary(r => r.Key, r => r.Value.Milliseconds));
        return true;
    }

    private IServerCatalog Current => IsDemo ? _demo : _user;
}
