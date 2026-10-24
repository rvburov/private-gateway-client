namespace PrivateGateway.Client.UI.Services;

/// <summary>Сведения о программе для подписей: версия и имя текущей ОС.</summary>
public static class AppInfo
{
    /// <summary>Версия для шапки, подвала настроек и отчета проверки.</summary>
    public const string Version = "0.1";

    /// <summary>Имя ОС для подписей («Запуск вместе с Windows»): программа одна на три ОС.</summary>
    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
}
