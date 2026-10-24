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

    /// <summary>
    /// Пауза для входа в Wi-Fi отеля или кафе: на <paramref name="duration"/> трафик идет мимо
    /// туннеля, чтобы открылась страница авторизации сети; потом защита возвращается сама. Только по
    /// явному действию пользователя. Защита выключена — паузе нечего снимать, ничего не происходит.
    /// </summary>
    Task PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Вернуть защиту раньше конца паузы.</summary>
    Task ResumeProtectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверки защиты (служба, ядро, весь трафик, блок при обрыве, DNS, IPv6, внешний IP, другие
    /// VPN). Только читают состояние; заодно обновляют время проверки на главном экране.
    /// </summary>
    Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default);

    /// <summary>Последние записи журнала службы, от старых к новым; секреты уже скрыты.</summary>
    IReadOnlyList<JournalEntry> Journal { get; }

    /// <summary>Новая запись журнала службы. Может прийти из любого потока.</summary>
    event EventHandler<JournalEntry>? JournalAdded;
}
