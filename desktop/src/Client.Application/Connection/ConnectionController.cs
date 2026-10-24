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

    /// <summary>Нажатие в этом состоянии означает «включить»: защита выключена или сломана.</summary>
    public static bool WantsConnect(ConnectionState state) =>
        state is ConnectionState.Off or ConnectionState.Error;
}
