using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Нажатия на уведомления ОС: «Повторить» и «Подключить» делают свое без окна; остальное показывает окно на нужном экране —
/// серверы, подписки, пауза для входа в Wi-Fi, поиск службы заново. «Позже» только закрывает уведомление.
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
    ToastViewModel toast)
{
    /// <summary>Слушать нажатия и включить уведомления.</summary>
    public void Start()
    {
        center.Replied += (_, reply) => _ = HandleAsync(reply);
        center.Start();
    }

    public async Task HandleAsync(NoticeReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        switch (reply.Action)
        {
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
