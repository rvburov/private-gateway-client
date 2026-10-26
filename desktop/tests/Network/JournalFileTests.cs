using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Журнал службы на диске (<see cref="JournalFile"/>): строки JSON, смена файла по размеру, битые строки, права.</summary>
public sealed class JournalFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-journal-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Entries_come_back_in_order_across_the_size_switch_and_broken_lines_are_skipped()
    {
        var at = DateTimeOffset.Parse("2026-10-26T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using (var file = new JournalFile(_root))
        {
            // Строка ~1 КБ: файл сменится после ~1000 записей.
            for (var i = 0; i < 1500; i++)
            {
                file.Append(new JournalEntry(at.AddSeconds(i), JournalLevel.Info, "service", $"запись {i} " + new string('x', 1000)));
            }
        }

        File.Exists(Path.Combine(_root, "journal.1.jsonl")).ShouldBeTrue("дорос до 1 МБ — стал прежним");
        new FileInfo(Path.Combine(_root, "journal.jsonl")).Length.ShouldBeLessThan(JournalFile.MaxSize);

        // Авария посреди записи: строка оборвалась.
        File.AppendAllText(Path.Combine(_root, "journal.jsonl"), "{\"time\":\"2026-10-26T1");
        using var again = new JournalFile(_root);
        again.Append(new JournalEntry(at.AddHours(1), JournalLevel.Warning, "service", "после аварии"));

        var tail = again.Tail(3);
        tail.Select(e => e.Message[..Math.Min(10, e.Message.Length)]).ShouldBe(["запись 149", "запись 149", "после авар"]);
        tail[^1].ShouldBe(new JournalEntry(at.AddHours(1), JournalLevel.Warning, "service", "после аварии"));
        again.Tail(10_000).Count.ShouldBeGreaterThan(1000, "хвост читается и из прежнего файла");
        File.ReadLines(again.Current).Last().ShouldContain("\"message\":\"после аварии\"", Case.Sensitive, "русский текст в файле — как есть");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(again.Current).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, "журнал читает только владелец");
        }
    }
}
