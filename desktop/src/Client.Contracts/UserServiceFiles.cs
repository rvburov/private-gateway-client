namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Файлы службы пользователя, о которых знает и окно: снимок системного прокси. Остался после сбоя —
/// окно при запуске поднимает службу пользователя, и она возвращает настройки.
/// </summary>
public static class UserServiceFiles
{
    /// <summary>Папка состояния службы пользователя по умолчанию: локальные данные пользователя.</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateGateway", "user-service");

    /// <summary>Снимок прежних настроек системного прокси в папке состояния.</summary>
    public static string ProxyBackup(string folder) => Path.Combine(folder, "system-proxy.json");
}
