using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Ранняя готовность службы для systemd: конструктор Gateway уже восстановил блок (или попытался), а подключение при
/// загрузке еще не началось. systemd только после этой отметки выпускает network-pre.target; файл ServiceReady для
/// установщика по-прежнему появляется позже, когда слушает IPC.
/// </summary>
public static class ServiceStartupReady
{
    /// <summary>
    /// Создать Gateway (его конструктор восстанавливает блок) и только потом отпустить systemd. Блок не встал — служба
    /// все равно отмечает готовность и остается в «Ошибке» с PGC-SVC-007 (решение 498): отказ запуска не закрыл
    /// бы сеть — network-pre.target ждет только конца запуска, удачного или нет, — а лишил бы окно связи со службой
    /// и возможности выключить защиту; повторяет попытку сторож блока, а не перезапуск systemd.
    /// </summary>
    public static async ValueTask<Gateway> CreateGatewayAsync(GatewayOptions options, TimeProvider time, ServiceJournal journal,
        string? notifySocket, Action<string> notify, SystemProxyGuard? systemProxy = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var gateway = new Gateway(options, time, journal, systemProxy);
        try
        {
            if (!string.IsNullOrEmpty(notifySocket) && gateway.Status.ErrorCode == ErrorCodes.KillSwitchFailed)
            {
                journal.Add(JournalLevel.Error, "service",
                    $"{ErrorCodes.KillSwitchFailed}: сохраненный блок при обрыве не подтвержден до сети — служба работает, блок повторит сторож");
            }

            NotifySystemd(notifySocket, notify);
            return gateway;
        }
        catch
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Отметить готовность, если программу запустил systemd с NOTIFY_SOCKET.</summary>
    public static void NotifySystemd(string? notifySocket, Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        if (!string.IsNullOrEmpty(notifySocket))
        {
            notify(notifySocket);
        }
    }
}
