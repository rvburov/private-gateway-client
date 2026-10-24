using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Кнопка-щит и смена сервера. Решает, что значит нажатие в текущем состоянии, и не дает
/// подключиться без выбранного сервера.
/// </summary>
public sealed class ConnectionController(IGatewayClient gateway, IServerCatalog catalog)
{
    /// <summary>Нажатие кнопки-щита.</summary>
    /// <returns>false — подключаться не к чему (нет выбранного сервера).</returns>
    public async Task<bool> ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (WantsConnect(gateway.Status.State))
        {
            var server = catalog.Selected;
            if (server is null)
            {
                return false;
            }

            await gateway.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await gateway.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ручная смена сервера. Если защита включена — переподключение к новому серверу; если
    /// выключена — только выбор, без подключения.
    /// </summary>
    public async Task SwitchServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        if (catalog.Selected?.Id == serverId)
        {
            return;
        }

        catalog.Choose(serverId);
        if (!WantsConnect(gateway.Status.State) && catalog.Selected is { } server)
        {
            await gateway.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// «Подключиться» в карточке сервера: выбрать его и включить защиту через него. Если защита
    /// уже идет через этот сервер — ничего не делать.
    /// </summary>
    public async Task ConnectToAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var status = gateway.Status;
        if (status.ServerId == serverId && !WantsConnect(status.State))
        {
            return;
        }

        catalog.Choose(serverId);
        if (catalog.Selected is { } server && server.Id == serverId)
        {
            await gateway.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Удалить сервер. Если защита шла через него — сначала отключиться: молча переходить на
    /// другой сервер нельзя, следующий выбирает пользователь.
    /// </summary>
    public async Task RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var status = gateway.Status;
        if (status.ServerId == serverId && status.State != ConnectionState.Off)
        {
            await gateway.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        catalog.Remove(serverId);
    }

    /// <summary>Нажатие в этом состоянии означает «включить»: защита выключена или сломана.</summary>
    public static bool WantsConnect(ConnectionState state) =>
        state is ConnectionState.Off or ConnectionState.Error;
}
