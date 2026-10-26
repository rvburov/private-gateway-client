using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>Уведомления ОС, на которой работает программа.</summary>
public static class SystemNotifiers
{
    /// <summary>
    /// Windows 10 и 11 — всплывающие уведомления; Linux — стандарт freedesktop по шине сеанса; macOS — нет: уведомления там
    /// показывает только программа из пакета .app с подписью (этап упаковки).
    /// </summary>
    /// <param name="icon">Значок для уведомлений Windows (PNG); null — значок Windows по умолчанию.</param>
    public static ISystemNotifier ForCurrentOs(string? icon = null) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) ? new WindowsToastNotifier(icon: icon)
        : OperatingSystem.IsLinux() ? FreedesktopNotifier.ForSession()
        : new NoSystemNotifier();
}
