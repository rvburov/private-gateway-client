using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Цвет состояния защиты — ключ ресурсов темы PgBrush.State.&lt;Tone&gt;.* (токены state.*).
/// </summary>
public enum Tone
{
    Connected,
    Connecting,
    Reconnecting,
    Blocked,
    Partial,
    Error,
    Off,
}

public static class ToneOf
{
    public static Tone State(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var partial = status.Mode == ConnectionMode.Proxy || status.CaptivePauseUntil is not null;
        return status.State switch
        {
            ConnectionState.Connected => partial ? Tone.Partial : Tone.Connected,
            ConnectionState.Connecting => Tone.Connecting,
            ConnectionState.Reconnecting => Tone.Reconnecting,
            ConnectionState.Blocked => Tone.Blocked,
            ConnectionState.Error => Tone.Error,
            _ => Tone.Off,
        };
    }
}
