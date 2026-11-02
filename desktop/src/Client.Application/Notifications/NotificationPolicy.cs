using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Notifications;

/// <summary>
/// Какое уведомление дает смена состояния защиты (доска 22) — без окна, ОС и часов: только два состояния подряд.
/// Свежие цифры в том же состоянии, пауза для входа в Wi-Fi и ее конец уведомлений не дают.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>Сколько после смены сети неудачные попытки похожи на сеть, которая ждет вход через браузер.</summary>
    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(2);

    /// <param name="before">Прежнее состояние.</param>
    /// <param name="after">Новое состояние.</param>
    /// <param name="server">Название сервера защиты.</param>
    /// <param name="now">Сейчас — для признака входа в Wi-Fi.</param>
    /// <returns>Уведомление; null — сказать нечего.</returns>
    public static Notice? For(ConnectionStatus before, ConnectionStatus after, string? server, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var moved = before.State != after.State;
        var changed = moved || before.ErrorCode != after.ErrorCode || before.StrictBlock != after.StrictBlock;
        var code = after.ErrorCode;
        if (HasNetworkProblem(after))
        {
            return !HasNetworkProblem(before) || (Stopped(after) && !Stopped(before))
                || (code == ErrorCodes.KillSwitchFailed && before.ErrorCode != code)
                ? Offline(after, server) : null;
        }

        if (Stopped(after) && code == ErrorCodes.NetworkUnavailable)
        {
            return changed ? Offline(after, server) : null;
        }

        // Обычное выключение при физически отсутствующей сети не является новой ошибкой. Восстановление сети в том же
        // Reconnecting тоже еще не подтверждает ни ответ сервера, ни необходимость входа в Wi-Fi.
        if (after is { State: ConnectionState.Off, NetworkUnavailable: true }
            || (before.NetworkUnavailable && !after.NetworkUnavailable && !moved))
        {
            return null;
        }

        Notice? notice = after.State switch
        {
            ConnectionState.Reconnecting when moved && before is { State: ConnectionState.Connecting, IsPaused: false } =>
                new(NoticeKind.ConnectionFailed, "notify.connectFailed." + Traffic(after), [NoticeAction.ChangeServer, NoticeAction.Retry]),
            ConnectionState.Reconnecting when SignInLikely(after, now) && !SignInLikely(before, now) =>
                new(NoticeKind.WifiSignIn, "notify.signIn.text", [NoticeAction.SignIn, NoticeAction.Later]),
            ConnectionState.Reconnecting when moved && before is { State: ConnectionState.Connected, IsPaused: false } =>
                new(NoticeKind.ConnectionLost, "notify.lost." + Traffic(after), [NoticeAction.ChangeServer, NoticeAction.Open]),

            // «Блокировка» строгого режима без ошибки — защиту выключили вручную, сеть закрыта.
            ConnectionState.Blocked when changed && after.StrictBlock && code is null && Traffic(after) == "blocked" =>
                new(NoticeKind.Disconnected, "notify.off.strict", [NoticeAction.Connect]),
            ConnectionState.Blocked when changed && before.State == ConnectionState.Connecting && code == ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ConnectionFailed, "notify.connectRejected." + Traffic(after), [NoticeAction.ChangeServer, NoticeAction.Retry]),
            ConnectionState.Blocked when changed && code is null or ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ServerDown, "notify.down." + Traffic(after), [NoticeAction.Retry, NoticeAction.ChooseServer]),
            ConnectionState.Blocked when changed =>
                new(NoticeKind.ProtectionFailed, "notify.failed." + Traffic(after), [NoticeAction.Open]),

            // Служба пропала, пока защита была включена или включалась; без защиты пропажу заметит следующее подключение.
            ConnectionState.Error when changed && code == ErrorCodes.ServiceNotRunning =>
                before.State is ConnectionState.Off or ConnectionState.Error ? null
                    : new(NoticeKind.ServiceStopped, "notify.service.text", [NoticeAction.Restart]),
            ConnectionState.Error when changed && before.State == ConnectionState.Connecting && code == ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ConnectionFailed, "notify.connectRejected." + Traffic(after), [NoticeAction.ChangeServer, NoticeAction.Retry]),
            ConnectionState.Error when changed && code == ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ServerDown, "notify.down." + Traffic(after), [NoticeAction.Retry, NoticeAction.ChooseServer]),
            ConnectionState.Error when changed =>
                new(NoticeKind.ProtectionFailed, "notify.failed." + Traffic(after), [NoticeAction.Open]),

            // Во время подключения запустился другой VPN или прокси-клиент: трафик может пойти мимо туннеля. Общими словами
            // (просьба пользователя 2026-11-01): какой — на «Проверке».
            ConnectionState.Connected when !moved && after.OtherClients is { Items.Count: > 0 } && before.OtherClients is null =>
                new(NoticeKind.OtherClient, "notify.other.text", [NoticeAction.Open]),
            ConnectionState.Connected when moved =>
                new(NoticeKind.Connected, before.State == ConnectionState.Reconnecting ? "notify.on.back" : after.IsProxy ? "notify.on.proxy" : "notify.on.text", []),
            ConnectionState.Off when moved =>
                new(NoticeKind.Disconnected, "notify.off.text", [NoticeAction.Connect]),
            _ => null,
        };
        return notice is null ? null : notice with
        {
            Server = server,
            ServerId = after.ServerId,
            Code = notice.Kind == NoticeKind.ProtectionFailed ? code ?? "PGC-SVC" : null,
        };
    }

    /// <summary>
    /// Окно открылось при уже известном отказе: переход мог произойти, пока окна не было. Успешное подключение и обычное
    /// выключенное состояние при запуске молчат; блокировка и неудача остаются видны без ожидания следующего события.
    /// </summary>
    public static Notice? Current(ConnectionStatus status, string? server, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsPaused)
        {
            return null;
        }

        if (HasNetworkProblem(status) || (Stopped(status) && status.ErrorCode == ErrorCodes.NetworkUnavailable))
        {
            return Offline(status, server);
        }

        if (status.State == ConnectionState.Reconnecting)
        {
            return new Notice(NoticeKind.ConnectionFailed, "notify.connectFailed." + Traffic(status), [NoticeAction.ChangeServer, NoticeAction.Retry])
            { Server = server, ServerId = status.ServerId };
        }

        if (status.State is ConnectionState.Error or ConnectionState.Blocked && status.ErrorCode == ErrorCodes.ServerNotResponding)
        {
            return new Notice(NoticeKind.ConnectionFailed, "notify.connectRejected." + Traffic(status), [NoticeAction.ChangeServer, NoticeAction.Retry])
            { Server = server, ServerId = status.ServerId };
        }

        if (status.State == ConnectionState.Blocked && status.ErrorCode is null)
        {
            if (Traffic(status) is not "blocked")
            {
                return new Notice(NoticeKind.ProtectionFailed, "notify.blocked." + Traffic(status), [NoticeAction.Open]);
            }

            return new Notice(NoticeKind.NetworkBlocked, status.StrictBlock ? "notify.blocked.strict" : "notify.blocked.saved",
                [NoticeAction.ChooseServer, NoticeAction.Open]);
        }

        return status.State is ConnectionState.Error or ConnectionState.Blocked && status.ErrorCode != ErrorCodes.ServiceNotRunning
            ? For(ConnectionStatus.Initial(now), status, server, now)
            : null;
    }

    /// <summary>
    /// Похоже на сеть, которая ждет вход через браузер: попытки идут не первую, а сеть сменилась только что. Под блоком и
    /// через системный прокси страница входа не откроется — нужна пауза. Мобильная сеть такого входа не просит.
    /// </summary>
    public static bool SignInLikely(ConnectionStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status is { State: ConnectionState.Reconnecting, NetworkUnavailable: false, Attempt: >= 2, NetworkChangedAt: { } changed }
            && now - changed <= SignInWindow
            && status.Network.Kind is not (NetworkKind.Mobile or NetworkKind.UsbModem)
            && ConnectionController.CanPause(status);
    }

    /// <summary>Только подтвержденное наблюдение ОС, а не неизвестное имя/тип сети или отсутствие ответа сервера.</summary>
    public static bool HasNetworkProblem(ConnectionStatus status) =>
        status.NetworkUnavailable && !status.IsPaused && status.State != ConnectionState.Off
        && status.ErrorCode != ErrorCodes.ServiceNotRunning;

    private static bool Stopped(ConnectionStatus status) => status.State is ConnectionState.Blocked or ConnectionState.Error;

    private static Notice Offline(ConnectionStatus status, string? server)
    {
        if (status.ErrorCode == ErrorCodes.KillSwitchFailed)
        {
            return new Notice(NoticeKind.ProtectionFailed, "notify.offline.protectionFailed", [NoticeAction.CheckNetwork])
            { Server = server, ServerId = status.ServerId, Code = status.ErrorCode };
        }

        var phase = Stopped(status) ? "stopped" : status.State == ConnectionState.Reconnecting ? "waiting" : "active";
        return new Notice(Stopped(status) ? NoticeKind.ReconnectStoppedOffline : NoticeKind.NetworkUnavailable,
            "notify.offline." + phase + "." + Traffic(status), [NoticeAction.CheckNetwork])
        { Server = server, ServerId = status.ServerId };
    }

    /// <summary>Что с трафиком: закрыт блоком, идет напрямую или стоит у прокси.</summary>
    private static string Traffic(ConnectionStatus status) =>
        status.Mode == ConnectionMode.Proxy ? "proxy"
        : status.Protection.KillSwitch == CheckState.Off || status.Protection.AllTraffic == CheckState.Off ? "direct"
        : status.Protection.KillSwitch == CheckState.Warning || status.Protection.AllTraffic == CheckState.Warning ? "unverified"
        : "blocked";
}
