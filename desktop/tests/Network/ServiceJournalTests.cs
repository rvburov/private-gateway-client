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

    [Fact]
    public void The_window_tail_brings_an_error_that_hundreds_of_ordinary_entries_followed()
    {
        // Окно берет хвост в 200 записей: ошибку первой попытки, за которой шли сотни строк ядра, оно тоже получает.
        var journal = new ServiceJournal(TimeProvider.System);
        journal.Add(JournalLevel.Info, "service", "до ошибки");
        journal.Add(JournalLevel.Error, "sing-box", "FATAL[0000] start service: set ipv4 address: The object already exists");
        for (var i = 0; i < 400; i++)
        {
            journal.Add(JournalLevel.Info, "sing-box", $"INFO inbound connection {i}");
        }

        var tail = journal.Tail(200);
        tail.Count.ShouldBe(200);
        tail[0].Level.ShouldBe(JournalLevel.Error, "ошибка — в хвосте окна");
        tail.ShouldNotContain(e => e.Message == "до ошибки", "место ей уступила самая старая обычная запись");
        tail[^1].Message.ShouldBe("INFO inbound connection 399");
        tail.Skip(1).Select(e => e.Message).ShouldBe(Enumerable.Range(201, 199).Select(i => $"INFO inbound connection {i}"), "обычные — последние и по порядку");
    }

    [Fact]
    public void A_tail_shorter_than_the_problems_keeps_the_newest_problems()
    {
        var at = DateTimeOffset.Parse("2026-10-31T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        JournalEntry[] entries =
        [
            new(at, JournalLevel.Error, "service", "ошибка 1"),
            new(at, JournalLevel.Info, "service", "обычная"),
            new(at, JournalLevel.Warning, "service", "предупреждение 2"),
            new(at, JournalLevel.Error, "service", "ошибка 3"),
        ];

        JournalTail.Take(entries, 2).Select(e => e.Message).ShouldBe(["предупреждение 2", "ошибка 3"]);
        JournalTail.Take(entries, 3).Select(e => e.Message).ShouldBe(["ошибка 1", "предупреждение 2", "ошибка 3"]);
        JournalTail.Take(entries, 10).ShouldBe(entries, "все поместились — все, по порядку");
        JournalTail.Take(entries, 0).ShouldBeEmpty();
    }
}
