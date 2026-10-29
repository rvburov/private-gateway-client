namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Папка данных пользователя: база, журналы. Своя на каждой ОС; при запуске из репозитория
/// (разработка) — <c>.localdata/</c> в корне, чтобы не трогать данные установленной программы.
/// </summary>
public sealed record AppPaths(string DataDirectory)
{
    /// <summary>Переменная окружения, которой можно задать папку данных (тесты, отладка).</summary>
    public const string OverrideVariable = "PG_DATA_DIR";

    public string Database => Path.Combine(DataDirectory, "app.db");

    /// <summary>Зашифрованные DPAPI секреты Windows (<see cref="Secrets.SecretFile"/>) — отдельно от базы.</summary>
    public string Secrets => Path.Combine(DataDirectory, "secrets.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Значок программы для уведомлений Windows (рисует окно при запуске).</summary>
    public string NotifyIcon => Path.Combine(DataDirectory, "notify.png");

    public static AppPaths ForCurrentUser()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        if (FindRepository(AppContext.BaseDirectory) is { } repo)
        {
            return new AppPaths(Path.Combine(repo, ".localdata"));
        }

        return new AppPaths(OperatingSystem.IsLinux()
            ? Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "private-gateway")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateGateway"));
    }

    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        return this;
    }

    private static string? FindRepository(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
