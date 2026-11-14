using System.Security;
using System.Text.Json;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Системный прокси на время подключения. Снимок прежних и примененных настроек переживает сбой службы;
/// ошибка возврата сохраняет его для повторной попытки. Ошибка ядра сама по себе прокси не снимает.
/// </summary>
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

    /// <summary>Включить прокси; исходный снимок сохраняется до изменения ОС, примененный — после проверки.</summary>
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
                var current = proxy.ReadForRestore();
                var previous = ReadBackup();
                if (previous is not null && !Recoverable(previous, current)
                    && !proxy.MatchesRestored(previous.Original, current))
                {
                    if (!Replaced(previous, current))
                    {
                        throw new IOException("Прежний системный прокси еще не восстановлен: принадлежность настроек не подтверждена");
                    }

                    File.Delete(backupPath);
                    previous = null;
                }

                var original = previous?.Original ?? current;
                var expected = proxy.Expected(port);
                if (expected is not null && expected.Values.Keys.Any(key => !original.Values.ContainsKey(key)))
                {
                    throw new IOException("Состав сетей изменился: отключите подключение и повторите включение системного прокси");
                }

                if (previous is null && expected is not null)
                {
                    // Выключенные сети понадобились только для согласованного снимка. Нет их в целях — не менять при возврате.
                    original = new SystemProxySnapshot(expected.Values.Keys.ToDictionary(key => key, key => original.Values[key]));
                }

                var appliedExpected = expected;
                if (expected is not null && previous?.Applied is { } earlier)
                {
                    // При смене порта уже принадлежавшая нам сеть могла выключиться. Ее старый прокси все еще надо вернуть.
                    var combined = earlier.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                    foreach (var pair in expected.Values)
                    {
                        combined[pair.Key] = pair.Value;
                    }
                    appliedExpected = new SystemProxySnapshot(combined);
                }

                var next = new Backup(original, appliedExpected, port, current);
                Save(next);
                if (expected is not null)
                {
                    proxy.Enable(port, expected);
                }
                else
                {
                    proxy.Enable(port);
                }

                var observed = proxy.ReadForRestore();
                if (appliedExpected is not null && !proxy.MatchesRestored(appliedExpected, observed))
                {
                    throw new IOException("Применение системного прокси не подтверждено");
                }

                // Новая сеть могла появиться после Expected: ее настройки не принадлежат этому подключению.
                // Для пропавшей цели сохраняем ожидаемое значение: если она вернется, владение снова будет проверено.
                var applied = appliedExpected is null ? observed : new SystemProxySnapshot(appliedExpected.Values.ToDictionary(pair => pair.Key,
                    pair => observed.Values.TryGetValue(pair.Key, out var value) ? value : pair.Value));
                _port = port;
                Save(next with { Applied = applied, Confirmed = true });
            }))
            {
                return;
            }
        }

        journal.Add(JournalLevel.Info, "service", $"Системный прокси включен ({proxy.Name}): 127.0.0.1:{port}");
    }

    /// <summary>Обычная остановка: записать ошибку, сохранив снимок для следующей попытки.</summary>
    public void Release() => Try(ReleaseConfirmed);

    /// <summary>
    /// Вернуть и проверить прежние настройки. Ошибка не скрывается: выход из аккаунта должен оставить восстановление
    /// доступным. Настройки другой программы не перезаписываются; неоднозначный снимок не считается восстановленным.
    /// </summary>
    public void ReleaseConfirmed() => ReleaseCore();

    private bool ReleaseCore()
    {
        var replaced = false;
        lock (_gate)
        {
            var snapshot = ReadBackup();
            if (snapshot is null)
            {
                if (_port is not null)
                {
                    throw new IOException("Нет снимка прежнего системного прокси");
                }

                return false;
            }

            var current = proxy.ReadForRestore();
            if (!proxy.MatchesRestored(snapshot.Original, current))
            {
                if (!Recoverable(snapshot, current))
                {
                    replaced = Replaced(snapshot, current);
                    if (!replaced)
                    {
                        throw new IOException("Принадлежность системного прокси не подтверждена: требуется восстановление настроек");
                    }
                }

                if (!replaced)
                {
                    // Возврат может оборваться между сетями или отдельными полями. Фаза и оба конца перехода — на диске.
                    Save(snapshot with { Before = current, Restoring = true });
                    proxy.Restore(snapshot.Original);
                    if (!proxy.MatchesRestored(snapshot.Original, proxy.ReadForRestore()))
                    {
                        throw new IOException("Возврат системного прокси не подтвержден");
                    }
                }
            }

            // Даже отказ удаления файла оставляет возможность повторить очистку, не теряя владение в памяти.
            File.Delete(backupPath);
            _port = null;
        }

        journal.Add(JournalLevel.Info, "service", replaced
            ? "Системный прокси сменила другая программа — оставлен, как она поставила"
            : "Системный прокси возвращен, как был");
        return true;
    }

    /// <summary>После сбоя обработать снимок. Ошибка остается в журнале, снимок — для явного восстановления.</summary>
    public bool RestoreStale()
    {
        lock (_gate)
        {
            var restored = false;
            return _port is null && Try(() => restored = ReleaseCore()) && restored;
        }
    }

    private bool Recoverable(Backup snapshot, SystemProxySnapshot current)
    {
        if (snapshot.Restoring)
        {
            return snapshot.Before is { } before && Transition(before, snapshot.Original, current);
        }

        if (snapshot.Applied is not { } applied)
        {
            // Старый файл содержал только исходные настройки. После перезапуска нельзя доказать, кто поставил нынешние.
            return false;
        }

        return snapshot.Confirmed
            ? proxy.MatchesRestored(applied, current)
            : snapshot.Before is { } beforeApply && Transition(beforeApply, applied, current);
    }

    private bool Transition(SystemProxySnapshot before, SystemProxySnapshot after, SystemProxySnapshot current) =>
        current.Values.Keys.All(key => before.Values.ContainsKey(key) || after.Values.ContainsKey(key))
        && before.Values.Keys.Concat(after.Values.Keys).Distinct(StringComparer.Ordinal).All(key =>
            MatchesValue(before, current, key) || MatchesValue(after, current, key));

    private bool Replaced(Backup snapshot, SystemProxySnapshot current) => snapshot is { Applied: { } applied, Confirmed: true }
        && !snapshot.Restoring
        // Не объявлять чужим частичный возврат или смесь своих и чужих настроек: сначала нужна явная проверка.
        && applied.Values.Keys.Where(key => !MatchesValue(snapshot.Original, applied, key))
            .All(key => !MatchesValue(applied, current, key));

    private bool MatchesValue(SystemProxySnapshot expected, SystemProxySnapshot current, string key) =>
        proxy.MatchesRestored(
            new SystemProxySnapshot(expected.Values.TryGetValue(key, out var before)
                ? new Dictionary<string, string?> { [key] = before } : new Dictionary<string, string?>()),
            new SystemProxySnapshot(current.Values.TryGetValue(key, out var after)
                ? new Dictionary<string, string?> { [key] = after } : new Dictionary<string, string?>()));

    private Backup? ReadBackup()
    {
        string text;
        try
        {
            // File.Exists скрывает отказ доступа и ошибки ввода-вывода как «нет файла».
            // Для подтвержденного восстановления отсутствие отличается от недоступного снимка.
            text = File.ReadAllText(backupPath);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Поврежденный снимок системного прокси");
        }

        if (document.RootElement.TryGetProperty(nameof(Backup.Original), out _))
        {
            var snapshot = JsonSerializer.Deserialize<Backup>(text);
            return snapshot is { Original.Values: not null, Port: > 0 and <= 65535 }
                && (snapshot.Applied is null || snapshot.Applied.Values is not null)
                && (snapshot.Before is null || snapshot.Before.Values is not null)
                ? snapshot : throw new JsonException("Поврежденный снимок системного прокси");
        }

        var legacy = JsonSerializer.Deserialize<Dictionary<string, string?>>(text)
            ?? throw new JsonException("Пустой снимок системного прокси");
        return new Backup(new SystemProxySnapshot(legacy), null, null, null);
    }

    private void Save(Backup snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        var temporary = backupPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        File.Move(temporary, backupPath, overwrite: true);
    }

    private sealed record Backup(SystemProxySnapshot Original, SystemProxySnapshot? Applied, int? Port,
        SystemProxySnapshot? Before, bool Confirmed = false, bool Restoring = false);

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
