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
        if (status.IsPaused)
        {
            return Tone.Partial;
        }

        if (status.HasExitIpLeak)
        {
            return Tone.Error;
        }

        var partial = status.Mode == ConnectionMode.Proxy;
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

/// <summary>Значок в кружке проверки (главный экран и «Проверка»): ключ ресурса Icon.Chip.*.</summary>
public static class CheckIcon
{
    public static string For(CheckState state) => state switch
    {
        CheckState.Ok => "Icon.Chip.Ok",
        CheckState.Waiting => "Icon.Chip.Dots",
        CheckState.Warning => "Icon.Chip.Warn",
        _ => "Icon.Chip.Dash",
    };
}
