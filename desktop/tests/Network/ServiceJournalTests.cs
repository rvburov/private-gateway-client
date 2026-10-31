using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Журнал службы ограничен, но ошибки не вытесняются обычными записями: при подробном журнале ядра события sing-box за
/// минуту вытесняли ошибку первой попытки подключения (у пользователя на Windows 2026-10-31).
/// </summary>
public sealed class ServiceJournalTests
{
    [Fact]
    public void Errors_and_warnings_outlive_a_flood_of_ordinary_entries()
    {
        var journal = new ServiceJournal(TimeProvider.System);
        journal.Add(JournalLevel.Error, "service", "PGC-SVC-002: sing-box вышел при запуске, код 1");
        journal.Add(JournalLevel.Warning, "service", "Адрес TUN занят другим адаптером");

        for (var i = 0; i < ServiceJournal.Capacity * 2; i++)
        {
            journal.Add(JournalLevel.Info, "sing-box", $"INFO inbound connection {i}");
        }

        var all = journal.Tail(ServiceJournal.Capacity);
        all.Count.ShouldBe(ServiceJournal.Capacity, "предел тот же");
        all.Take(2).Select(e => e.Level).ShouldBe([JournalLevel.Error, JournalLevel.Warning]);
        all[^1].Message.ShouldBe($"INFO inbound connection {ServiceJournal.Capacity * 2 - 1}", "новые записи на месте");
    }
}
