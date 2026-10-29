using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Ранняя готовность службы для systemd: конструктор Gateway уже восстановил блок, а подключение при загрузке еще
/// не началось. systemd только после этой отметки выпускает network-pre.target; файл ServiceReady для установщика
/// по-прежнему появляется позже, когда слушает IPC.
/// </summary>
public static class ServiceStartupReady
{
    /// <summary>
    /// Создать Gateway (его конструктор восстанавливает блок), проверить защиту и только потом отпустить systemd.
    /// Ошибка уведомления не оставляет незакрытые таймеры Gateway. Подключение к серверу сюда не входит.
    /// </summary>
    public static async ValueTask<Gateway> CreateGatewayAsync(GatewayOptions options, TimeProvider time, ServiceJournal journal,
        string? notifySocket, Action<string> notify, SystemProxyGuard? systemProxy = null)
    {
        var gateway = new Gateway(options, time, journal, systemProxy);
        try
        {
            // Старые фильтры могут быть целы, но сохранить разрешение прежнему TUN: отказ их
            // замены в конструкторе тоже не является готовностью нужной политики.
            if (!string.IsNullOrEmpty(notifySocket) && gateway.Status.ErrorCode == ErrorCodes.KillSwitchFailed)
            {
                throw new IOException("Служба не готова: сохраненная политика блока не восстановлена — READY=1 не отправлен");
            }

            NotifySystemd(notifySocket, options.BootBlock, options.KillSwitch, notify);
            return gateway;
        }
        catch
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Отметить готовность, если программу запустил systemd с NOTIFY_SOCKET. Сохраненная защита при загрузке или блок,
    /// оставленный прошлым запуском, должны действовать по-настоящему; загрузка правил без действующего фильтра не
    /// считается готовностью. Отказ не отправляет READY=1 и становится ошибкой запуска службы.
    /// </summary>
    public static void NotifySystemd(string? notifySocket, string? bootBlockFile, IKillSwitch? killSwitch, Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        if (string.IsNullOrEmpty(notifySocket))
        {
            return;
        }

        var saved = bootBlockFile is null ? null : BootBlock.Read(bootBlockFile);
        var required = saved is { Open: false } && (saved.Boot || saved.Paused);
        if ((required || killSwitch?.IsEnabled == true) && killSwitch?.IsIntact != true)
        {
            throw new IOException("Служба не готова: сохраненный блок при обрыве не действует — READY=1 не отправлен");
        }

        notify(notifySocket);
    }
}
