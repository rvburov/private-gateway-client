namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Где служба слушает IPC. Установленная служба (права администратора) — общий адрес системы;
/// служба пользователя (без прав, только режим прокси) — адрес в папке или имени пользователя.
/// Интерфейс ищет сначала установленную службу, потом службу пользователя.
/// </summary>
public static class IpcEndpoints
{
    /// <summary>Имя named pipe установленной службы на Windows.</summary>
    public const string WindowsPipe = "PrivateGateway.Ipc.v1";

    /// <summary>Unix-сокет установленной службы на Linux.</summary>
    public const string LinuxSocket = "/run/private-gateway/ipc.sock";

    /// <summary>Unix-сокет установленной службы на macOS.</summary>
    public const string MacSocket = "/var/run/private-gateway/ipc.sock";

    /// <summary>Адрес установленной службы на этой ОС: имя pipe на Windows, путь к сокету на остальных.</summary>
    public static string System => OperatingSystem.IsWindows() ? WindowsPipe
        : OperatingSystem.IsMacOS() ? MacSocket : LinuxSocket;

    /// <summary>
    /// Адрес службы пользователя: на Windows — pipe с именем учетной записи, на Linux — сокет в
    /// XDG_RUNTIME_DIR (только пользователь), иначе и на macOS — во временной папке пользователя.
    /// </summary>
    public static string User
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsPipe + "." + Environment.UserName;
            }

            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var folder = !OperatingSystem.IsMacOS() && runtime is { Length: > 0 }
                ? Path.Combine(runtime, "private-gateway")
                : Path.Combine(Path.GetTempPath(), "private-gateway-" + Environment.UserName);
            return Path.Combine(folder, "ipc.sock");
        }
    }

    /// <summary>Named pipe — имя без разделителей пути; Unix-сокет — путь.</summary>
    public static bool IsPipe(string endpoint) =>
        OperatingSystem.IsWindows() && !endpoint.Contains('/', StringComparison.Ordinal) && !endpoint.Contains('\\', StringComparison.Ordinal);
}
