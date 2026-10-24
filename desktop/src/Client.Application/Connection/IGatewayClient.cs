using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Связь интерфейса со службой. Интерфейс сам сеть не трогает: подключение, проверки и пинг —
/// просьбы к службе, а состояние приходит от нее же (<see cref="StatusChanged"/>).
/// </summary>
public interface IGatewayClient
{
    /// <summary>Последнее состояние, присланное службой.</summary>
    ConnectionStatus Status { get; }

    /// <summary>Новое состояние от службы. Может прийти из любого потока.</summary>
    event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Работает ли интерфейс без службы, на имитации (демо-режим этапа 1).</summary>
    bool IsSimulated { get; }

    Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Повторить проверки защиты (DNS, IPv6, внешний IP, блок при обрыве).</summary>
    Task RunChecksAsync(CancellationToken cancellationToken = default);
}
