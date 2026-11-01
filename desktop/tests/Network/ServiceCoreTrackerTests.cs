using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

public sealed class ServiceCoreTrackerTests
{
    [Fact]
    public void Tunnel_pairs_and_proxy_with_a_relay_are_tracked_even_when_new_info_is_discarded()
    {
        var journal = new ServiceJournal(TimeProvider.System);
        using var tracker = new ServiceCoreTracker(journal);
        journal.Add(JournalLevel.Info, "service", "Ядро Xray запущено, процесс 101, ретранслятор sing-box для Hysteria2 и TUIC — процесс 102");
        journal.Add(JournalLevel.Info, "service", "Туннель поднят: Xray — процесс 201, sing-box — процесс 202");
        for (var i = 0; i < ServiceJournal.Capacity + 1; i++)
        {
            journal.Add(JournalLevel.Warning, "service", "предупреждение стенда " + i);
        }

        tracker.Proxy.ShouldBe(101);
        tracker.Tunnel.ShouldBe((201, 202));
        journal.Add(JournalLevel.Info, "service", "Ядро Xray запущено, процесс 301, ретранслятор sing-box для Hysteria2 и TUIC — процесс 302");
        journal.Add(JournalLevel.Info, "service", "Туннель поднят: Xray — процесс 401, sing-box — процесс 402");
        journal.Tail(ServiceJournal.Capacity).ShouldAllBe(entry => entry.Level == JournalLevel.Warning, "свежие Info сразу удалены из хвоста");
        tracker.Proxy.ShouldBe(301, "для прокси берется его PID перед подписью ретранслятора");
        tracker.Tunnel.ShouldBe((401, 402), "пара обновилась целиком по событию нового запуска");

        journal.Add(JournalLevel.Info, "xray", "Ядро Xray запущено, процесс 501");
        journal.Add(JournalLevel.Warning, "service", "Ядро Xray запущено, процесс 502");
        journal.Add(JournalLevel.Info, "service", "Ядро Xray запущено, процесс не число");
        journal.Add(JournalLevel.Info, "service", "Туннель поднят: Xray — процесс 601, sing-box — процесс не число");
        tracker.Proxy.ShouldBe(301, "чужой источник, предупреждение и некорректная строка не меняют известный PID");
        tracker.Tunnel.ShouldBe((401, 402), "некорректное событие не оставляет смешанную пару PID");
    }
}
