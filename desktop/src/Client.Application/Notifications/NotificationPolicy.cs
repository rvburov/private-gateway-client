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
        Notice? notice = after.State switch
        {
            ConnectionState.Reconnecting when SignInLikely(after, now) && !SignInLikely(before, now) =>
                new(NoticeKind.WifiSignIn, "notify.signIn.text", [NoticeAction.SignIn, NoticeAction.Later]),
            ConnectionState.Reconnecting when moved && before is { State: ConnectionState.Connected, IsPaused: false } =>
                new(NoticeKind.ConnectionLost, "notify.lost." + Traffic(after), [NoticeAction.ChangeServer, NoticeAction.Open]),

            // «Блокировка» строгого режима без ошибки — защиту выключили вручную, сеть закрыта.
            ConnectionState.Blocked when changed && after.StrictBlock && code is null =>
                new(NoticeKind.Disconnected, "notify.off.strict", [NoticeAction.Connect]),
            ConnectionState.Blocked when changed && code is null or ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ServerDown, "notify.down.blocked", [NoticeAction.Retry, NoticeAction.ChooseServer]),
            ConnectionState.Blocked when changed =>
                new(NoticeKind.ProtectionFailed, "notify.failed.blocked", [NoticeAction.Open]),

            // Служба пропала, пока защита была включена или включалась; без защиты пропажу заметит следующее подключение.
            ConnectionState.Error when changed && code == ErrorCodes.ServiceNotRunning =>
                before.State is ConnectionState.Off or ConnectionState.Error ? null
                    : new(NoticeKind.ServiceStopped, "notify.service.text", [NoticeAction.Restart]),
            ConnectionState.Error when changed && code == ErrorCodes.ServerNotResponding =>
                new(NoticeKind.ServerDown, "notify.down." + Traffic(after), [NoticeAction.Retry, NoticeAction.ChooseServer]),
            ConnectionState.Error when changed =>
                new(NoticeKind.ProtectionFailed, "notify.failed." + Traffic(after), [NoticeAction.Open]),

            // Во время подключения запустилась другая программа VPN или прокси: трафик может пойти мимо туннеля.
            ConnectionState.Connected when !moved && after.OtherClients is { Items.Count: > 0 } others && before.OtherClients is null =>
                Programs(others) is { Length: > 0 } names
                    ? new(NoticeKind.OtherClient, "notify.other.named", [NoticeAction.Open]) { Program = names }
                    : new(NoticeKind.OtherClient, "notify.other.text", [NoticeAction.Open]),
            ConnectionState.Connected when moved =>
                new(NoticeKind.Connected, before.State == ConnectionState.Reconnecting ? "notify.on.back" : after.IsProxy ? "notify.on.proxy" : "notify.on.text", []),
            ConnectionState.Off when moved =>
                new(NoticeKind.Disconnected, "notify.off.text", [NoticeAction.Connect]),
            _ => null,
        };
        return notice is null ? null : notice with { Server = server, Code = notice.Kind == NoticeKind.ProtectionFailed ? code ?? "PGC-SVC" : null };
    }

    /// <summary>
    /// Похоже на сеть, которая ждет вход через браузер: попытки идут не первую, а сеть сменилась только что. Под блоком и
    /// через системный прокси страница входа не откроется — нужна пауза. Мобильная сеть такого входа не просит.
    /// </summary>
    public static bool SignInLikely(ConnectionStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status is { State: ConnectionState.Reconnecting, Attempt: >= 2, NetworkChangedAt: { } changed }
            && now - changed <= SignInWindow
            && status.Network.Kind is not (NetworkKind.Mobile or NetworkKind.UsbModem)
            && ConnectionController.CanPause(status);
    }

    /// <summary>Названия узнанных программ через запятую; ни одной — пусто.</summary>
    private static string Programs(Core.Platform.OtherClientList others) => string.Join(", ", others.Programs(string.Empty).Where(p => p.Length > 0));

    /// <summary>Что с трафиком: закрыт блоком, идет напрямую или стоит у прокси.</summary>
    private static string Traffic(ConnectionStatus status) =>
        status.Mode == ConnectionMode.Proxy ? "proxy"
        : status.Protection.KillSwitch == CheckState.Off || status.Protection.AllTraffic == CheckState.Off ? "direct"
        : "blocked";
}
