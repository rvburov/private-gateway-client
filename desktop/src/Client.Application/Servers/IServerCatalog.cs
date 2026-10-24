using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Серверы пользователя и выбранный из них. Выбор только ручной: сам сервер не меняется.</summary>
public interface IServerCatalog
{
    IReadOnlyList<ServerProfile> Servers { get; }

    /// <summary>Выбранный сервер; null — серверов нет или ни один не выбран.</summary>
    ServerProfile? Selected { get; }

    event EventHandler? Changed;

    void Choose(string serverId);

    /// <summary>Перемерить пинг и обновить списки серверов из подписок.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
