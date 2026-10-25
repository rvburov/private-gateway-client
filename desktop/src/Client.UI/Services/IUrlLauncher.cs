namespace PrivateGateway.Client.UI.Services;

/// <summary>Открыть адрес в браузере пользователя — сам интерфейс в сеть не ходит.</summary>
public interface IUrlLauncher
{
    Task<bool> OpenAsync(Uri url);
}

/// <summary>
/// Страница, на которой сеть отеля или кафе показывает вход: проверочный адрес самой ОС по http —
/// сеть перехватывает его и отдает страницу авторизации.
/// </summary>
public static class CaptivePortal
{
    public static Uri CheckPage { get; } = new(
        OperatingSystem.IsWindows() ? "http://www.msftconnecttest.com/redirect"
        : OperatingSystem.IsMacOS() ? "http://captive.apple.com"
        : "http://nmcheck.gnome.org/check_network_status.txt");
}
