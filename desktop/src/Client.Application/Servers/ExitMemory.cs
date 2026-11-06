using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>
/// Помнит, какой IP сайты видели через сервер (решение 583): у сервера «VPS + прокси» программа подключается к VPS, а сайты
/// видят выход прокси за ним — список серверов показывает оба адреса. Пишется только выход, который служба подтвердила
/// замером через этот сервер; утечка, пауза для входа в Wi-Fi и реальный IP машины не записываются.
/// </summary>
public sealed class ExitMemory(IGatewayClient gateway, IServerCatalog catalog)
{
    public void Start() => gateway.StatusChanged += (_, status) => Remember(status);

    /// <summary>Выход через сервер снимка, который можно запомнить; иначе null.</summary>
    public static string? Exit(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status is { State: ConnectionState.Connected, ServerId: { Length: > 0 }, Exit.Ip: { Length: > 0 } ip }
            && !status.IsPaused && !status.HasExitIpLeak && ip != status.Exit.DirectIp
            ? ip
            : null;
    }

    private void Remember(ConnectionStatus status)
    {
        if (Exit(status) is { } ip)
        {
            catalog.RememberExit(status.ServerId!, ip);
        }
    }
}
