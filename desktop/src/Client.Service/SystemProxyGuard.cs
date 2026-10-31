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

    // Каким служба поставила системный прокси: другая программа его сменила — возвращать прежний поверх нее нельзя.
    private SystemProxySnapshot? _applied;

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
                _applied = proxy.Read();
            }))
            {
                return;
            }

            _port = port;
        }

        journal.Add(JournalLevel.Info, "service", $"Системный прокси включен ({proxy.Name}): 127.0.0.1:{port}");
    }

    /// <summary>
    /// Вернуть настройки, как были до включения; не включен — ничего. Сменила их за это время другая программа (VPN- или
    /// прокси-клиент, включенный рядом, решение 511) — ее настройки остаются: прежние вернули бы прокси мимо нее.
    /// </summary>
    public void Release()
    {
        var replaced = false;
        lock (_gate)
        {
            if (_port is null)
            {
                return;
            }

            var port = _port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _port = null;
            if (!Try(() =>
            {
                // Сменена — если не та, что ставила служба, и своего порта в ней нет нигде: чтение настроек на macOS и Linux
                // может разниться в мелочах, а оставить прокси на мертвый вход хуже, чем вернуть прежний.
                replaced = _applied is { } applied && proxy.Read() is var now && !Same(now, applied)
                    && !now.Values.Values.Any(v => v is not null && (v == port || v.EndsWith(":" + port, StringComparison.Ordinal)));
                if (replaced)
                {
                    File.Delete(backupPath);
                }
                else
                {
                    RestoreFromFile();
                }
            }))
            {
                return;
            }
        }

        journal.Add(JournalLevel.Info, "service", replaced
            ? "Системный прокси сменила другая программа — оставлен, как она поставила"
            : "Системный прокси возвращен, как был");
    }

    private static bool Same(SystemProxySnapshot left, SystemProxySnapshot right) =>
        left.Values.Count == right.Values.Count && left.Values.All(v => right.Values.TryGetValue(v.Key, out var value) && value == v.Value);

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
