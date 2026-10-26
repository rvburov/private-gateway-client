using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Вход в Wi-Fi отеля или кафе» — одинаково на «Защите и утечках» и на главной (строгая «Блокировка»):
/// <list type="number">
/// <item>сеть уже открыта (защита выключена, «Ошибка» без блока) — сразу страница входа в сеть: VPN ей не мешает;</item>
/// <item>пауза уже идет — снова страница, без вопроса (страницу закрыли раньше, чем вошли);</item>
/// <item>пауза бывает — вопрос, пауза на 5 минут, страница и главная;</item>
/// <item>служба отказала в режиме прокси — системный прокси выключен, паузе нечего менять: страница откроется
/// напрямую;</item>
/// <item>служба отказала в режиме VPN (блок не снялся или служба его не знает) — «пауза не началась», без страницы.</item>
/// </list>
/// В «Подключении» и «Переподключении» пауза тоже есть: сервер за порталом не ответит, пока не войти в сеть, — служба
/// останавливает попытки до конца паузы.
/// </summary>
internal static class CaptivePortalFlow
{
    public static async Task RunAsync(
        IGatewayClient gateway,
        ConnectionController controller,
        OverlayViewModel overlay,
        IUrlLauncher launcher,
        INavigator navigator,
        ToastViewModel toast)
    {
        var status = gateway.Status;
        if (ConnectionController.NetworkIsOpen(status))
        {
            await launcher.OpenAsync(CaptivePortal.CheckPage).ConfigureAwait(true);
            toast.Show(Loc.Instance["pause.off"], ToastViewModel.LongDuration);
            return;
        }

        if (status.IsPaused)
        {
            await launcher.OpenAsync(CaptivePortal.CheckPage).ConfigureAwait(true);
            toast.Show(Loc.Instance["pause.running"], ToastViewModel.LongDuration);
            return;
        }

        if (!await SafetyDialogs.ConfirmCaptivePauseAsync(overlay, strict: status.StrictBlock).ConfigureAwait(true))
        {
            return;
        }

        // Служба может ответить, что паузе нечего менять: тогда так и сказать, а не делать вид паузы.
        if (await controller.PauseForCaptivePortalAsync().ConfigureAwait(true))
        {
            await launcher.OpenAsync(CaptivePortal.CheckPage).ConfigureAwait(true);
            navigator.Navigate(Pages.Home);
            toast.Show(Loc.Instance["pause.started"], ToastViewModel.LongDuration);
            return;
        }

        if (gateway.Status.Mode == ConnectionMode.Proxy)
        {
            await launcher.OpenAsync(CaptivePortal.CheckPage).ConfigureAwait(true);
            toast.Show(Loc.Instance["pause.notNeeded"], ToastViewModel.LongDuration);
            return;
        }

        toast.Show(Loc.Instance["pause.failed"], ToastViewModel.LongDuration);
    }
}
