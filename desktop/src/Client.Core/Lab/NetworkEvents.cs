namespace PrivateGateway.Client.Core.Lab;

/// <summary>Сетевое событие, которое лаборатория объясняет (этап 8).</summary>
public enum NetworkEventKind
{
    /// <summary>Служба поднимает защиту: проверяет сервер, запускает ядро.</summary>
    Connecting,

    /// <summary>Защита в режиме VPN включена: туннель поднят, сервер ответил через него.</summary>
    Connected,

    /// <summary>Защита в режиме прокси включена: через сервер идут программы с прокси.</summary>
    ProxyOn,

    /// <summary>Связь с сервером пропала — служба пробует снова; номер попытки — в подробности.</summary>
    Reconnecting,

    /// <summary>Сервер не отвечает, блок при обрыве держит сеть закрытой; код — в подробности.</summary>
    Blocked,

    /// <summary>Сбой службы или ядра; код — в подробности.</summary>
    Error,

    /// <summary>Защиту выключили: трафик идет напрямую.</summary>
    Off,

    /// <summary>«Блокировка» строгого режима: защиту выключили, а сеть закрыта.</summary>
    StrictBlock,

    /// <summary>Пауза для входа в Wi-Fi: защита ненадолго пропускает трафик мимо туннеля.</summary>
    Paused,

    /// <summary>Пауза закончилась — защита снова держит трафик.</summary>
    Resumed,

    /// <summary>Сеть машины сменилась: другой Wi-Fi, кабель, адрес; имя сети — в подробности.</summary>
    NetworkChanged,

    /// <summary>Служба измерила адрес, который видят сайты; адрес — в подробности.</summary>
    ExitMeasured,

    /// <summary>Проверка защиты нашла неладное: весь трафик, DNS, IPv6 или блок при обрыве — в подробности.</summary>
    Warning,
}

/// <summary>Событие: когда, что и подробность (номер попытки, код ошибки, имя сети, адрес); нет — null.</summary>
public sealed record NetworkEvent(DateTimeOffset At, NetworkEventKind Kind, string? Detail = null);

/// <summary>
/// События из смены состояния защиты: служба присылает снимки состояния, а событие — разница двух соседних. Без прежнего
/// снимка событий нет: состояние на момент запуска окна — не событие.
/// </summary>
public static class NetworkEvents
{
    public static IReadOnlyList<NetworkEvent> Between(ConnectionStatus? before, ConnectionStatus after, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null)
        {
            return [];
        }

        var events = new List<NetworkEvent>();
        if (before.State != after.State || before.StrictBlock != after.StrictBlock || (after.State == ConnectionState.Reconnecting && before.Attempt != after.Attempt))
        {
            if (State(after) is { } kind)
            {
                events.Add(new NetworkEvent(at, kind, kind switch
                {
                    NetworkEventKind.Reconnecting => after.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NetworkEventKind.Blocked or NetworkEventKind.Error or NetworkEventKind.StrictBlock => after.ErrorCode,
                    _ => null,
                }));
            }
        }

        if (!before.IsPaused && after.IsPaused)
        {
            events.Add(new NetworkEvent(at, NetworkEventKind.Paused));
        }
        else if (before.IsPaused && !after.IsPaused && after.State == before.State)
        {
            events.Add(new NetworkEvent(at, NetworkEventKind.Resumed));
        }

        if (after.NetworkChangedAt is { } changed && changed != before.NetworkChangedAt)
        {
            events.Add(new NetworkEvent(at, NetworkEventKind.NetworkChanged, after.Network.Name));
        }

        if (after is { State: ConnectionState.Connected, Exit.Ip: { } ip } && ip != before.Exit.Ip)
        {
            events.Add(new NetworkEvent(at, NetworkEventKind.ExitMeasured, ip));
        }

        foreach (var (name, was, now) in new[]
        {
            ("allTraffic", before.Protection.AllTraffic, after.Protection.AllTraffic),
            ("dns", before.Protection.Dns, after.Protection.Dns),
            ("ipv6", before.Protection.Ipv6, after.Protection.Ipv6),
            ("killSwitch", before.Protection.KillSwitch, after.Protection.KillSwitch),
        })
        {
            if (now == CheckState.Warning && was != CheckState.Warning)
            {
                events.Add(new NetworkEvent(at, NetworkEventKind.Warning, name));
            }
        }

        return events;
    }

    private static NetworkEventKind? State(ConnectionStatus status) => status switch
    {
        { StrictBlock: true } => NetworkEventKind.StrictBlock,
        { State: ConnectionState.Connecting } => NetworkEventKind.Connecting,
        { State: ConnectionState.Connected, Mode: ConnectionMode.Proxy } => NetworkEventKind.ProxyOn,
        { State: ConnectionState.Connected } => NetworkEventKind.Connected,
        { State: ConnectionState.Reconnecting } => NetworkEventKind.Reconnecting,
        { State: ConnectionState.Blocked } => NetworkEventKind.Blocked,
        { State: ConnectionState.Error } => NetworkEventKind.Error,
        { State: ConnectionState.Off } => NetworkEventKind.Off,
        _ => null,
    };
}
