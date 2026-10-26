using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Нажатия на уведомления ОС: «Повторить» и «Подключить» делают свое без окна; остальное показывает окно на нужном экране —
/// серверы, подписки, пауза для входа в Wi-Fi, поиск службы заново, «О программе» с загрузкой обновления. «Позже» закрывает
/// уведомление, у обновления — еще и сворачивает его карточку до следующей версии.
/// </summary>
public sealed class NoticeActions(
    NotificationCenter center,
    NoticeWindow window,
    ShellViewModel shell,
    IGatewayClient gateway,
    IServerCatalog catalog,
    ConnectionController controller,
    OverlayViewModel overlay,
    IUrlLauncher launcher,
    ToastViewModel toast,
    UpdateWatcher updates)
{
    /// <summary>Слушать нажатия и включить уведомления.</summary>
    public void Start()
    {
        center.Replied += (_, reply) => _ = HandleAsync(reply);
        updates.Found += (_, offer) => center.Announce(offer);
        center.Start();
    }

    public async Task HandleAsync(NoticeReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        switch (reply.Action)
        {
            case NoticeAction.Later when reply.Tag == Notice.UpdateTag:
                updates.Postpone(updates.Offer?.Version);
                return;
            case NoticeAction.Later:
                return;
            case NoticeAction.Retry when catalog.Selected is { } server:
                await gateway.ConnectAsync(server).ConfigureAwait(true);
                return;
            case NoticeAction.Connect when catalog.Selected is not null:
                if (ConnectionController.WantsConnect(gateway.Status))
                {
                    await controller.ToggleAsync().ConfigureAwait(true);
                }

                return;
        }

        window.Bring();
        switch (reply.Action)
        {
            // Без выбранного сервера «Повторить» и «Подключить» — выбрать его.
            case NoticeAction.ChangeServer or NoticeAction.ChooseServer or NoticeAction.ChooseOther or NoticeAction.Retry or NoticeAction.Connect:
                shell.Navigate(Pages.Servers);
                break;
            case NoticeAction.OpenSubscriptions:
                shell.Navigate(Pages.Profiles);
                break;

            // Карточка «О программе» покажет загрузку и «Перезапустить клиент»; служба не ставит — там же «Открыть выпуск».
            case NoticeAction.Update:
                updates.Postpone(null);
                shell.Navigate(Pages.About);
                await updates.DownloadAsync().ConfigureAwait(true);
                break;
            case NoticeAction.SignIn:
                await CaptivePortalFlow.RunAsync(gateway, controller, overlay, launcher, shell, toast).ConfigureAwait(true);
                break;

            // Служба пропала: окно ищет ее заново (службу пользователя запускает само), не нашло — экран «Служба не запущена».
            case NoticeAction.Restart when catalog.Selected is { } server:
                await gateway.ConnectAsync(server).ConfigureAwait(true);
                break;
        }
    }
}
