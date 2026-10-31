namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Куда прокрутить «Проверку»: плитка «Пинг» на главной открывает ее на блоке «Задержка» (решение 509). Экран «Проверка»
/// создается один раз и потом показывается снова — просьба приходит событием, а вид забирает ее, когда показан.
/// </summary>
public sealed class CheckFocus
{
    private bool _delay;

    /// <summary>Просьба открыть блок «Задержка».</summary>
    public event EventHandler? DelayRequested;

    public void RequestDelay()
    {
        _delay = true;
        DelayRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Забрать просьбу: true — один раз после <see cref="RequestDelay"/>.</summary>
    public bool TakeDelay()
    {
        var delay = _delay;
        _delay = false;
        return delay;
    }
}
