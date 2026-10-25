using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Вопросы перед тем, что ослабляет защиту (доска 18): выключить блок при обрыве, открыть
/// интернет без VPN для входа в Wi-Fi. Безопасный вариант — основная кнопка, на нее же фокус,
/// Escape и щелчок мимо.
/// </summary>
internal static class SafetyDialogs
{
    /// <returns>true — пользователь выбрал «Выключить».</returns>
    public static async Task<bool> ConfirmKillSwitchOffAsync(OverlayViewModel overlay) =>
        await overlay.AskAsync(new DialogViewModel(
            DialogKind.Warning,
            "Icon.Shield",
            "Icon.Mark.Alert",
            Loc.Instance["dialog.ksOff.title"],
            Loc.Instance["dialog.ksOff.text"],
            Loc.Instance["dialog.ksOff.keep"],
            Loc.Instance["dialog.ksOff.drop"]) { IsSecondaryDanger = true }).ConfigureAwait(true) == DialogChoice.Secondary;

    /// <param name="overlay">Слой диалогов.</param>
    /// <param name="strict">«Блокировка» строгого режима: после паузы сеть снова закроется, а не вернется VPN.</param>
    /// <returns>true — пользователь выбрал «Открыть на 5 минут».</returns>
    public static async Task<bool> ConfirmCaptivePauseAsync(OverlayViewModel overlay, bool strict = false) =>
        await overlay.AskAsync(new DialogViewModel(
            DialogKind.Warning,
            "Icon.Shield",
            "Icon.Mark.Alert",
            Loc.Instance["pause.dialog.title"],
            Loc.Instance[strict ? "pause.dialog.textStrict" : "pause.dialog.text"],
            Loc.Instance["pause.dialog.keep"],
            Loc.Instance["pause.dialog.open"]) { IsSecondaryDanger = true }).ConfigureAwait(true) == DialogChoice.Secondary;

    /// <summary>Строгий режим делает сеть недоступной без VPN — включать только осознанно.</summary>
    /// <returns>true — пользователь выбрал «Включить».</returns>
    public static async Task<bool> ConfirmStrictModeAsync(OverlayViewModel overlay) =>
        await overlay.AskAsync(new DialogViewModel(
            DialogKind.Info,
            "Icon.Shield",
            "Icon.Mark.Ok",
            Loc.Instance["dialog.strict.title"],
            Loc.Instance["dialog.strict.text"],
            Loc.Instance["dialog.strict.on"],
            Loc.Instance["dialog.cancel"])).ConfigureAwait(true) == DialogChoice.Primary;

    /// <summary>Прокси для локальной сети: чужие устройства пойдут в интернет через сервер пользователя.</summary>
    /// <returns>true — пользователь выбрал «Открыть».</returns>
    public static async Task<bool> ConfirmLanProxyAsync(OverlayViewModel overlay) =>
        await overlay.AskAsync(new DialogViewModel(
            DialogKind.Warning,
            "Icon.Shield",
            "Icon.Mark.Alert",
            Loc.Instance["dialog.lanProxy.title"],
            Loc.Instance["dialog.lanProxy.text"],
            Loc.Instance["dialog.lanProxy.keep"],
            Loc.Instance["dialog.lanProxy.open"]) { IsSecondaryDanger = true }).ConfigureAwait(true) == DialogChoice.Secondary;
}
