namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Одна копия программы на папку данных: первая держит файл-замок, вторая не запускается, а просит
/// первую показать окно — записью в файл, за которым первая следит. Без сокетов и каналов: интерфейс
/// в сеть не ходит. Другая папка данных (PG_DATA_DIR) — другая копия: так работают тесты и отладка.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string LockName = "instance.lock";
    private const string ActivateName = "instance.activate";

    private readonly FileStream _lock;
    private readonly FileSystemWatcher _watcher;

    private SingleInstance(FileStream lockFile, string directory)
    {
        _lock = lockFile;
        _watcher = new FileSystemWatcher(directory, ActivateName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty);
        _watcher.Created += (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Вторая копия попросила показать окно. Приходит из потока наблюдателя.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Стать единственной копией; null — уже работает другая.</summary>
    public static SingleInstance? TryAcquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var lockFile = new FileStream(Path.Combine(paths.DataDirectory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(lockFile, paths.DataDirectory);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Попросить работающую копию показать окно.</summary>
    public static void RequestActivation(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        File.WriteAllText(Path.Combine(paths.DataDirectory, ActivateName), DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _lock.Dispose();
    }
}
