namespace PrivateGateway.Client.Core;

/// <summary>
/// Допустимые переходы между состояниями защиты. Служба меняет состояние только через
/// <see cref="Move"/>, поэтому, например, из «Выключено» нельзя сразу попасть в «Защищено»
/// мимо подключения.
/// </summary>
public static class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Off] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] =
            [ConnectionState.Connected, ConnectionState.Blocked, ConnectionState.Error, ConnectionState.Off],
        [ConnectionState.Connected] =
            [ConnectionState.Reconnecting, ConnectionState.Connecting, ConnectionState.Error, ConnectionState.Off],
        // «Переподключение → Подключение» — пользователь выбрал другой сервер или поменял настройки, пока служба ждала свой.
        [ConnectionState.Reconnecting] =
            [ConnectionState.Connected, ConnectionState.Connecting, ConnectionState.Blocked, ConnectionState.Error, ConnectionState.Off],
        [ConnectionState.Blocked] = [ConnectionState.Connecting, ConnectionState.Error, ConnectionState.Off],
        [ConnectionState.Error] = [ConnectionState.Connecting, ConnectionState.Off],
    };

    /// <summary>Можно ли перейти из <paramref name="from"/> в <paramref name="to"/>.</summary>
    /// <remarks>
    /// «Защищено → Подключение» — смена сервера на ходу. Переход в то же состояние запрещен:
    /// повторное событие не должно выглядеть как новое.
    /// </remarks>
    public static bool CanMove(ConnectionState from, ConnectionState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>Новое состояние или исключение, если переход запрещен.</summary>
    public static ConnectionState Move(ConnectionState from, ConnectionState to) =>
        CanMove(from, to)
            ? to
            : throw new InvalidOperationException($"Переход {from} → {to} запрещен");

    /// <summary>
    /// Идет ли трафик мимо туннеля. Только в «Выключено»: в остальных состояниях он либо в
    /// туннеле, либо заблокирован.
    /// </summary>
    public static bool TrafficBypassesTunnel(ConnectionState state) => state == ConnectionState.Off;
}
