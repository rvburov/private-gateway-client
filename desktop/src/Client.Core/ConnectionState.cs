namespace PrivateGateway.Client.Core;

/// <summary>
/// Состояние защиты. Одно на всю программу: его показывают главный экран, трей и уведомления,
/// а меняет только служба.
/// </summary>
public enum ConnectionState
{
    /// <summary>VPN выключен, трафик идет напрямую.</summary>
    Off,

    /// <summary>Туннель поднимается; при включенном блоке интернет на паузе.</summary>
    Connecting,

    /// <summary>Туннель работает, и сервер ответил на пробу через него.</summary>
    Connected,

    /// <summary>Связь с сервером пропала, служба пробует снова; трафик на паузе.</summary>
    Reconnecting,

    /// <summary>Сервер не отвечает, блок при обрыве держит трафик — утечки нет.</summary>
    Blocked,

    /// <summary>Сбой службы или ядра; трафик заблокирован до перезапуска.</summary>
    Error,
}
