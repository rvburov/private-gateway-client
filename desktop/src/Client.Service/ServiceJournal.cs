using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Logging;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Журнал службы для экрана «Проверка» (WatchLog): последние записи в памяти и новые по мере
/// появления. Каждая строка проходит через <see cref="SecretMasker"/>, а ключи текущего
/// подключения вырезаются дословно — даже если ядро напечатает их в непривычном виде.
/// </summary>
public sealed class ServiceJournal(TimeProvider time)
{
    /// <summary>Сколько записей хранится.</summary>
    public const int Capacity = 500;

    private readonly Queue<JournalEntry> _entries = new();
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

            entry = new JournalEntry(time.GetUtcNow(), level, source, SecretMasker.Mask(text));
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        Added?.Invoke(this, entry);
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
