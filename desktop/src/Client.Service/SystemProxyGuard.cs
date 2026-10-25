using System.Security;
using System.Text.Json;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Системный прокси на время подключения в режиме прокси. Прежние настройки — снимок в файле: их
/// служба возвращает при отключении, отказе и своей остановке, а после сбоя — при следующем запуске.
/// Ошибка ядра прокси не снимает: программы с прокси остаются без связи, а не уходят напрямую.
/// </summary>
/// <param name="proxy">Настройки ОС.</param>
/// <param name="backupPath">Файл снимка.</param>
/// <param name="journal">Журнал службы.</param>
public sealed class SystemProxyGuard(ISystemProxy proxy, string backupPath, ServiceJournal journal)
{
    private readonly Lock _gate = new();
    private int? _port;

    /// <summary>Включен ли системный прокси этой службой.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _port is not null;
            }
        }
    }

    /// <summary>Включить прокси на <paramref name="port"/> или сменить порт; снимок — только при первом включении.</summary>
    public void Apply(int port)
    {
        lock (_gate)
        {
            if (_port == port)
            {
                return;
            }

            if (!Try(() =>
            {
                // Снимок после сбоя, не возвращенный при запуске, не затирается: в нем настройки до программы.
                if (_port is null && !File.Exists(backupPath))
                {
                    Save(proxy.Read());
                }

                proxy.Enable(port);
            }))
            {
                return;
            }

            _port = port;
        }

        journal.Add(JournalLevel.Info, "service", $"Системный прокси включен ({proxy.Name}): 127.0.0.1:{port}");
    }

    /// <summary>Вернуть настройки, как были до включения; не включен — ничего.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_port is null)
            {
                return;
            }

            _port = null;
            if (!Try(RestoreFromFile))
            {
                return;
            }
        }

        journal.Add(JournalLevel.Info, "service", "Системный прокси возвращен, как был");
    }

    /// <summary>После сбоя службы снимок остался — вернуть настройки по нему. true — было что возвращать.</summary>
    public bool RestoreStale()
    {
        lock (_gate)
        {
            if (_port is not null || !File.Exists(backupPath) || !Try(RestoreFromFile))
            {
                return false;
            }
        }

        journal.Add(JournalLevel.Warning, "service", "Системный прокси остался после сбоя службы — возвращен, как был");
        return true;
    }

    private void RestoreFromFile()
    {
        if (File.Exists(backupPath))
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(backupPath)) ?? [];
            proxy.Restore(new SystemProxySnapshot(values));
            File.Delete(backupPath);
        }
    }

    private void Save(SystemProxySnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        File.WriteAllText(backupPath, JsonSerializer.Serialize(snapshot.Values));
    }

    /// <summary>Настройки ОС не дались — запись в журнале; подключение от этого не ломается.</summary>
    private bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            journal.Add(JournalLevel.Warning, "service", $"Системный прокси ({proxy.Name}) не изменен: {e.Message}");
            return false;
        }
    }
}
