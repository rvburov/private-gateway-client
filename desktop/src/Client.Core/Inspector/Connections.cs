using System.Net;

namespace PrivateGateway.Client.Core.Inspector;

/// <summary>Протокол сокета.</summary>
public enum SocketProtocol
{
    Tcp,
    Udp,
}

/// <summary>Состояние сокета TCP — те, что стоит различать пользователю; у UDP — <see cref="Open"/>.</summary>
public enum SocketState
{
    /// <summary>Ждет входящих соединений.</summary>
    Listen,

    /// <summary>Соединение установлено.</summary>
    Established,

    /// <summary>Соединение устанавливается (SYN отправлен или получен).</summary>
    Opening,

    /// <summary>Соединение закрывается (FIN, TIME_WAIT, CLOSE_WAIT и подобные).</summary>
    Closing,

    /// <summary>Сокет UDP: открыт, соединений у UDP нет.</summary>
    Open,
}

/// <summary>Сокет машины, как его видит ОС.</summary>
/// <param name="Protocol">TCP или UDP.</param>
/// <param name="Local">Свой адрес и порт.</param>
/// <param name="Remote">Адрес и порт другой стороны; у слушающих и UDP — null.</param>
/// <param name="State">Состояние.</param>
/// <param name="ProcessId">Процесс-владелец; ОС не сказала (нет прав) — null.</param>
public sealed record SocketEntry(SocketProtocol Protocol, IPEndPoint Local, IPEndPoint? Remote, SocketState State, int? ProcessId);

/// <summary>Сокет с именем процесса — для экрана.</summary>
/// <param name="Socket">Сокет.</param>
/// <param name="Process">Имя процесса; неизвестно — null.</param>
public sealed record NetworkConnection(SocketEntry Socket, string? Process);

/// <summary>Счетчики адаптера: сколько байт принято и отправлено с его включения.</summary>
public sealed record AdapterCounters(string Name, long Received, long Sent);

/// <summary>Счетчики всех адаптеров в одно время — скорость окно считает по двум соседним.</summary>
public sealed record TrafficSample(DateTimeOffset TakenAt, IReadOnlyList<AdapterCounters> Adapters);
