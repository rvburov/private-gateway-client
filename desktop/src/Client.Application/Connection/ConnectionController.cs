using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Кнопка-щит и смена сервера. Решает, что значит нажатие в текущем состоянии, и не дает
/// подключиться без выбранного сервера.
/// </summary>
public sealed class ConnectionController(IGatewayClient gateway, IServerCatalog catalog)
{
    /// <summary>Сколько длится пауза для входа в Wi-Fi (доска 5: «на 5 минут»).</summary>
    public static readonly TimeSpan CaptivePause = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Нажатие кнопки-щита. Во время паузы для входа в Wi-Fi — вернуть защиту, а не отключить; на паузе строгой
    /// «Блокировки» — подключить: защиты там не было, а закрытую сеть возвращает кнопка паузы.
    /// </summary>
    /// <returns>false — подключаться не к чему (нет выбранного сервера).</returns>
    public async Task<bool> ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (gateway.Status is { IsPaused: true, StrictBlock: false })
        {
            await gateway.ResumeProtectionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (WantsConnect(gateway.Status))
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
        if (!WantsConnect(gateway.Status) && catalog.Selected is { } server)
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
        if (status.ServerId == serverId && !WantsConnect(status))
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
        await ReleaseAsync([serverId], cancellationToken).ConfigureAwait(false);
        catalog.Remove(serverId);
    }

    /// <summary>
    /// Перед удалением серверов (одного или всей подписки): если защита идет через один из них —
    /// отключиться. Другой сервер выбирает пользователь.
    /// </summary>
    public async Task ReleaseAsync(IReadOnlyCollection<string> serverIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        var status = gateway.Status;
        if (status.ServerId is { } id && serverIds.Contains(id) && status.State != ConnectionState.Off)
        {
            await gateway.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// «Вход в Wi-Fi отеля или кафе»: открыть трафик мимо туннеля на <see cref="CaptivePause"/>.
    /// </summary>
    /// <returns>
    /// false — паузы нет: сейчас ее не бывает (<see cref="CanPause"/>) или службе нечего менять (режим прокси без
    /// системного прокси, блок, которого служба не знает).
    /// </returns>
    public async Task<bool> PauseForCaptivePortalAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPause(gateway.Status))
        {
            return false;
        }

        return await gateway.PauseForCaptivePortalAsync(CaptivePause, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Пауза для входа в Wi-Fi бывает в «Защищено», в «Блокировке» (строгий режим, блок с загрузки или с прошлого
    /// запуска, попытки кончились) и в «Ошибке», где сеть закрыта (блок при обрыве, системный прокси на остановленном
    /// ядре), и не идет уже. В «Подключении» и «Переподключении» ее нет.
    /// </summary>
    public static bool CanPause(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return !status.IsPaused && (status.State is ConnectionState.Connected or ConnectionState.Blocked
            || (status.State == ConnectionState.Error && !NetworkIsOpen(status)));
    }

    /// <summary>
    /// Сеть уже открыта — страница входа в сеть откроется и без паузы: защита выключена или «Ошибка» без блока
    /// (трафик идет напрямую).
    /// </summary>
    public static bool NetworkIsOpen(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State == ConnectionState.Off || (status.State == ConnectionState.Error && status.Protection.AllTraffic == CheckState.Off);
    }

    /// <summary>«Вернуть защиту» раньше конца паузы.</summary>
    public Task ResumeAsync(CancellationToken cancellationToken = default) => gateway.ResumeProtectionAsync(cancellationToken);

    /// <summary>Нажатие в этом состоянии означает «включить»: защита выключена или сломана.</summary>
    public static bool WantsConnect(ConnectionState state) =>
        state is ConnectionState.Off or ConnectionState.Error;

    /// <summary>
    /// Нажатие подключает: защита выключена, сломана или выключена вручную в строгом режиме («Блокировка» строгого
    /// режима — сеть закрыта, а защиты нет: щит ее включает, выключает ее сам строгий режим в настройках).
    /// </summary>
    public static bool WantsConnect(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return WantsConnect(status.State) || status.StrictBlock;
    }

    /// <summary>Строгий режим действует: режим VPN с блоком при обрыве, ручное отключение оставляет сеть закрытой.</summary>
    public static bool KeepsBlock(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings is { Mode: ConnectionMode.Vpn, KillSwitch: true, Protection.StrictMode: true };
    }
}
