namespace PrivateGateway.Client.Core;

/// <summary>Состояние одной проверки защиты: чипы главного экрана и строки экрана «Проверка».</summary>
public enum CheckState
{
    /// <summary>Защищено.</summary>
    Ok,

    /// <summary>Ждет подключения; трафик на паузе.</summary>
    Waiting,

    /// <summary>Трафик остановлен блоком при обрыве — утечки нет.</summary>
    Stopped,

    /// <summary>Защиты нет: VPN выключен.</summary>
    Off,

    /// <summary>Защита неполная: режим прокси или пауза для входа в Wi-Fi.</summary>
    Warning,
}

/// <summary>Режим работы: весь трафик через TUN или только системный прокси.</summary>
public enum ConnectionMode
{
    Vpn,
    Proxy,
}

/// <summary>Тип сети, через которую машина выходит в интернет.</summary>
public enum NetworkKind
{
    None,
    WiFi,
    Ethernet,
    UsbModem,
    Mobile,
}

/// <summary>Сеть машины. Имя Wi-Fi есть, только если ОС его отдает (на Windows 11 — с разрешением на геолокацию).</summary>
public sealed record NetworkInfo(NetworkKind Kind, string? Name, bool IsOpen, bool IsMetered)
{
    public static NetworkInfo Unknown { get; } = new(NetworkKind.None, null, false, false);
}

/// <summary>Четыре проверки главного экрана; <paramref name="Ipv6Tunnel"/> — IPv6 идет через туннель, а не закрыт.</summary>
public sealed record ProtectionChecks(
    CheckState AllTraffic,
    CheckState Dns,
    CheckState Ipv6,
    CheckState KillSwitch,
    DateTimeOffset? CheckedAt,
    bool Ipv6Tunnel = false)
{
    /// <summary>Пауза для входа в Wi-Fi: трафик мимо туннеля — все четыре проверки требуют внимания.</summary>
    public static ProtectionChecks Paused { get; } = new(CheckState.Warning, CheckState.Warning, CheckState.Warning, CheckState.Warning, null);

    /// <summary>Проверки для состояния, когда служба еще ничего не мерила.</summary>
    public static ProtectionChecks For(ConnectionState state) => state switch
    {
        ConnectionState.Connected => new(CheckState.Ok, CheckState.Ok, CheckState.Ok, CheckState.Ok, null),
        ConnectionState.Connecting or ConnectionState.Reconnecting =>
            new(CheckState.Waiting, CheckState.Ok, CheckState.Ok, CheckState.Ok, null),
        ConnectionState.Blocked or ConnectionState.Error =>
            new(CheckState.Stopped, CheckState.Ok, CheckState.Ok, CheckState.Ok, null),
        _ => new(CheckState.Off, CheckState.Off, CheckState.Off, CheckState.Off, null),
    };

    /// <summary>
    /// Проверки с учетом режима: в режиме «Только прокси» защита неполная — через прокси идут только
    /// программы, которые его используют, DNS — частично, IPv6 не контролируется, блока при обрыве нет.
    /// </summary>
    public static ProtectionChecks For(ConnectionState state, ConnectionMode mode) =>
        mode == ConnectionMode.Proxy && state != ConnectionState.Off
            ? new(state == ConnectionState.Connected ? CheckState.Warning : CheckState.Waiting,
                CheckState.Warning, CheckState.Warning, CheckState.Warning, null)
            : For(state);
}

/// <summary>Скорость и задержка туннеля.</summary>
public sealed record TrafficStats(int? LatencyMs, double DownloadMbps, double UploadMbps)
{
    public static TrafficStats Empty { get; } = new(null, 0, 0);
}

/// <summary>Внешний IP — измеренный службой, а не предполагаемый.</summary>
public sealed record ExitInfo(string? Ip, string? City, string? NetworkType, string? Asn, string? DirectIp)
{
    public static ExitInfo Unknown { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Снимок состояния защиты, который служба присылает интерфейсу. Главный экран, трей и
/// уведомления показывают один и тот же снимок. <c>StrictBlock</c> — «Блокировка» строгого режима:
/// защиту выключили вручную, а сеть закрыта до подключения или выключения строгого режима.
/// <c>CaptivePauseUntil</c> — пауза для входа в Wi-Fi: в «Защищено», «Блокировке» (и строгого режима), «Ошибке» под блоком,
/// а также в «Подключении» и «Переподключении» — тогда попытки стоят до конца паузы.
/// </summary>
public sealed record ConnectionStatus(
    ConnectionState State,
    ConnectionMode Mode,
    string? ServerId,
    DateTimeOffset Since,
    int Attempt,
    ProtectionChecks Protection,
    TrafficStats Traffic,
    ExitInfo Exit,
    NetworkInfo Network,
    string? ErrorCode,
    DateTimeOffset? CaptivePauseUntil,
    bool StrictBlock = false)
{
    /// <summary>Пауза для входа в Wi-Fi: трафик идет мимо туннеля до <see cref="CaptivePauseUntil"/>.</summary>
    public bool IsPaused => CaptivePauseUntil is not null && State != ConnectionState.Off;

    /// <summary>Подключено в режиме «Только прокси»: защищен не весь трафик (доска 17).</summary>
    public bool IsProxy => Mode == ConnectionMode.Proxy && State == ConnectionState.Connected;

    /// <summary>Начальное состояние: защита выключена.</summary>
    public static ConnectionStatus Initial(DateTimeOffset now) => new(
        ConnectionState.Off, ConnectionMode.Vpn, null, now, 0, ProtectionChecks.For(ConnectionState.Off),
        TrafficStats.Empty, ExitInfo.Unknown, NetworkInfo.Unknown, null, null);

    /// <summary>
    /// Переход в новое состояние по правилам <see cref="ConnectionStateMachine"/>. Любой переход заканчивает паузу для
    /// входа в Wi-Fi и «Блокировку» строгого режима.
    /// </summary>
    public ConnectionStatus MoveTo(ConnectionState state, DateTimeOffset now) => this with
    {
        State = ConnectionStateMachine.Move(State, state),
        Since = now,
        Protection = ProtectionChecks.For(state, Mode),
        Attempt = state == ConnectionState.Reconnecting ? Attempt + 1 : 0,
        ErrorCode = state == ConnectionState.Error ? ErrorCode : null,
        CaptivePauseUntil = null,
        StrictBlock = false,
    };
}
