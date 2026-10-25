using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Core;

/// <summary>
/// Переподключение — только к выбранному серверу: другой сервер сам не выбирается. Пауза перед
/// попыткой растет вдвое (1, 2, 4… с), но не больше потолка; сдается служба по политике из «Подключения
/// и ядра».
/// </summary>
public static class Reconnect
{
    /// <summary>Пауза перед попыткой <paramref name="attempt"/> (с 1): вдвое больше прежней, не больше <paramref name="max"/>.</summary>
    public static TimeSpan Delay(int attempt, TimeSpan first, TimeSpan max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var delay = first;
        for (var i = 1; i < attempt && delay < max; i++)
        {
            delay += delay;
        }

        return delay < max ? delay : max;
    }

    /// <summary>
    /// Пора ли сдаться: «Не повторять» — сразу, «До 5 минут» — когда с обрыва прошло <paramref name="limit"/>,
    /// «Экспоненц. до 60 с» — никогда: служба пробует, пока пользователь сам не выключит защиту.
    /// </summary>
    public static bool GivesUp(ReconnectPolicy policy, TimeSpan sinceLost, TimeSpan limit) => policy switch
    {
        ReconnectPolicy.Never => true,
        ReconnectPolicy.UpTo5Minutes => sinceLost >= limit,
        _ => false,
    };
}
