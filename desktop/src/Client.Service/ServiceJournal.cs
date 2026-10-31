using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Logging;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Журнал службы для экрана «Проверка» (WatchLog): последние записи в памяти и новые по мере
/// появления. Каждая строка проходит через <see cref="SecretMasker"/>, а ключи текущего
/// подключения вырезаются дословно — даже если ядро напечатает их в непривычном виде. В выводе
/// ядер (<see cref="CoreSources"/>) скрыты и адреса (<see cref="AddressMasker"/>): адреса сайтов в
/// журнал не попадают и на подробных уровнях «Журнала ядра». <paramref name="file"/> — журнал на диске (<see cref="JournalFile"/>):
/// туда идет то же, уже скрытое; null — только в памяти.
/// </summary>
public sealed class ServiceJournal(TimeProvider time, JournalFile? file = null)
{
    /// <summary>Сколько записей хранится.</summary>
    public const int Capacity = 500;

    /// <summary>Источники — ядра: их вывод проходит еще и через <see cref="AddressMasker"/>.</summary>
    public static readonly IReadOnlySet<string> CoreSources = new HashSet<string>(StringComparer.Ordinal) { "xray", "sing-box" };

    private readonly List<JournalEntry> _entries = [];
    private readonly Lock _gate = new();
    private string[] _secrets = [];

    /// <summary>Новая запись; приходит из любого потока.</summary>
    public event EventHandler<JournalEntry>? Added;

    /// <summary>Ключи подключения, которые нельзя показывать ни в каком виде.</summary>
    public void Hide(IEnumerable<string?> secrets)
    {
        lock (_gate)
        {
            _secrets = [.. secrets.OfType<string>().Where(s => s.Length >= 4).Distinct(StringComparer.Ordinal)];
        }
    }

    public void Add(JournalLevel level, string source, string message)
    {
        JournalEntry entry;
        lock (_gate)
        {
            var text = message;
            foreach (var secret in _secrets)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }

            text = SecretMasker.Mask(text);
            entry = new JournalEntry(time.GetUtcNow(), level, source, CoreSources.Contains(source) ? AddressMasker.Mask(text) : text);
            Keep(entry);
        }

        file?.Append(entry);
        Added?.Invoke(this, entry);
    }

    /// <summary>
    /// Записи прошлого запуска с диска — в начало журнала в памяти, без события и без записи на диск: окно видит, что было до
    /// аварии или перезагрузки.
    /// </summary>
    public void Preload(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            var now = _entries.ToList();
            _entries.Clear();
            foreach (var entry in entries.Concat(now))
            {
                Keep(entry);
            }
        }
    }

    /// <summary>
    /// Не больше <see cref="Capacity"/> записей: уходит самая старая обычная, а ошибки и предупреждения остаются, пока обычных
    /// хватает, — подробный журнал ядра не вытесняет ошибку подключения (у пользователя на Windows 2026-10-31).
    /// </summary>
    private void Keep(JournalEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.FindIndex(e => e.Level is not (JournalLevel.Warning or JournalLevel.Error));
            _entries.RemoveAt(oldest < 0 ? 0 : oldest);
        }
    }

    /// <summary>Последние <paramref name="count"/> записей, от старых к новым.</summary>
    public IReadOnlyList<JournalEntry> Tail(int count)
    {
        lock (_gate)
        {
            return [.. _entries.Skip(Math.Max(0, _entries.Count - Math.Max(0, count)))];
        }
    }
}
