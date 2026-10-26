using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>Уведомления ОС, на которой работает программа.</summary>
public static class SystemNotifiers
{
    /// <summary>
    /// Windows 10 и 11 — всплывающие уведомления; Linux — стандарт freedesktop по шине сеанса; macOS — центр уведомлений,
    /// только у программы из пакета .app (запуск из папки сборки — без уведомлений).
    /// </summary>
    /// <param name="icon">Значок для уведомлений Windows (PNG); null — значок Windows по умолчанию.</param>
    public static ISystemNotifier ForCurrentOs(string? icon = null) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) ? new WindowsToastNotifier(icon: icon)
        : OperatingSystem.IsLinux() ? FreedesktopNotifier.ForSession()
        : OperatingSystem.IsMacOS() ? (ISystemNotifier?)MacNotifier.ForThisApp() ?? new NoSystemNotifier()
        : new NoSystemNotifier();
}
