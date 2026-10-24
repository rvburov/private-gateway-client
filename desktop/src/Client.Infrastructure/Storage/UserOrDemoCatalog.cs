using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Серверы для интерфейса: демо-серверы, пока своих нет, а после первого импорта — только свои.
/// Демо-серверы в базу не пишутся и после импорта не показываются.
/// </summary>
public sealed class UserOrDemoCatalog : IServerCatalog
{
    private readonly SqliteServerCatalog _user;
    private readonly DemoServerCatalog _demo;

    public UserOrDemoCatalog(SqliteServerCatalog user, DemoServerCatalog demo)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(demo);
        _user = user;
        _demo = demo;
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

    /// <summary>Показываются демо-серверы: своих пока нет.</summary>
    public bool IsDemo => _user.Servers.Count == 0;

    public IReadOnlyList<ServerProfile> Servers => Current.Servers;

    public IReadOnlyList<ServerSubscription> Subscriptions => Current.Subscriptions;

    public ServerProfile? Selected => Current.Selected;

    public void Choose(string serverId) => Current.Choose(serverId);

    public void SetFavorite(string serverId, bool isFavorite) => Current.SetFavorite(serverId, isFavorite);

    public void Rename(string serverId, string name) => Current.Rename(serverId, name);

    public void Remove(string serverId) => Current.Remove(serverId);

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Current.RefreshAsync(cancellationToken);

    private IServerCatalog Current => IsDemo ? _demo : _user;
}
