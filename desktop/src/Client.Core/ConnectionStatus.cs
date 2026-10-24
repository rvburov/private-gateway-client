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

/// <summary>Четыре проверки главного экрана.</summary>
public sealed record ProtectionChecks(
    CheckState AllTraffic,
    CheckState Dns,
    CheckState Ipv6,
    CheckState KillSwitch,
    DateTimeOffset? CheckedAt)
{
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
/// уведомления показывают один и тот же снимок.
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
    DateTimeOffset? CaptivePauseUntil)
{
    /// <summary>Начальное состояние: защита выключена.</summary>
    public static ConnectionStatus Initial(DateTimeOffset now) => new(
        ConnectionState.Off, ConnectionMode.Vpn, null, now, 0, ProtectionChecks.For(ConnectionState.Off),
        TrafficStats.Empty, ExitInfo.Unknown, NetworkInfo.Unknown, null, null);

    /// <summary>Переход в новое состояние по правилам <see cref="ConnectionStateMachine"/>.</summary>
    public ConnectionStatus MoveTo(ConnectionState state, DateTimeOffset now) => this with
    {
        State = ConnectionStateMachine.Move(State, state),
        Since = now,
        Protection = ProtectionChecks.For(state),
        Attempt = state == ConnectionState.Reconnecting ? Attempt + 1 : 0,
        ErrorCode = state == ConnectionState.Error ? ErrorCode : null,
    };
}
