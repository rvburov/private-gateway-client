namespace PrivateGateway.Client.Service;

/// <summary>
/// Одна копия службы на папку состояния: вторая сняла бы ядра первой (<see cref="Cores.CoreLedger"/>), убрала бы правила ее туннеля
/// и забрала бы ее адрес IPC. Замок — файл service.lock в папке состояния, открытый без совместного доступа (на Unix — flock):
/// служба упала — ОС отпускает замок сама, файл не мешает следующему запуску.
/// </summary>
public static class ServiceLock
{
    /// <summary>
    /// Взять замок в <paramref name="folder"/>: замок — пока поток открыт; другая копия его держит — null и <paramref name="busy"/>.
    /// Файл не создать (нет прав на папку) — служба работает без замка: не хуже, чем было, и это не повод не защищать.
    /// </summary>
    public static FileStream? TryAcquire(string folder, out bool busy)
    {
        busy = false;
        try
        {
            Directory.CreateDirectory(folder);
            return new FileStream(Path.Combine(folder, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            busy = true;
            return null;
        }
    }
}
