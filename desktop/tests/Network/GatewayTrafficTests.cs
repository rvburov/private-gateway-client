using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Пинг и скорость главного экрана — от службы. Она их не заполняла, и окно всегда показывало «—» и «0,0» (у пользователя
/// на Windows 2026-10-31). Ядра и адаптер здесь — подделки: замер пинга и счетчики адаптера туннеля.
/// </summary>
public sealed class GatewayTrafficTests
{
    [Fact]
    public async Task Vpn_reports_ping_through_the_server_and_speed_from_the_tunnel_adapter()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counters = (Received: 1_000_000L, Sent: 500_000L);
        var pings = 0;
        await using var gateway = Connected(time, ConnectionMode.Vpn, () => counters, () => ++pings * 10);

        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.ShouldBe(new TrafficStats(10, 0, 0), "первый замер скорости — точка отсчета, пинг — сразу");

        time.Advance(TimeSpan.FromSeconds(2));
        counters = (counters.Received + 2_500_000, counters.Sent + 250_000);
        await gateway.SampleTrafficAsync();

        // 2,5 МБ за 2 с — 10 Мбит/с загрузки; 0,25 МБ — 1 Мбит/с отдачи. Пинг — раз в 15 с, не на каждом замере.
        gateway.Status.Traffic.ShouldBe(new TrafficStats(10, 10, 1));
        pings.ShouldBe(1);

        time.Advance(Gateway.PingEvery);
        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.LatencyMs.ShouldBe(20);
    }

    [Fact]
    public async Task Proxy_has_a_ping_but_no_adapter_to_count_and_off_clears_the_numbers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counted = false;
        await using var gateway = Connected(time, ConnectionMode.Proxy, () =>
        {
            counted = true;
            return (1, 1);
        }, () => 35);

        await gateway.SampleTrafficAsync();

        gateway.Status.Traffic.ShouldBe(new TrafficStats(35, 0, 0));
        counted.ShouldBeFalse("в режиме прокси адаптер туннеля — не его трафик");

        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await gateway.SampleTrafficAsync();
        gateway.Status.Traffic.ShouldBe(TrafficStats.Empty, "выключено — ни пинга, ни скорости");
    }

    private static Gateway Connected(FakeTimeProvider time, ConnectionMode mode, Func<(long, long)?> counters, Func<int?> ping)
    {
        var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"), BlockWatch: TimeSpan.FromHours(1),
            Delay: (_, _) => Task.FromResult(ping()), TunnelCounters: counters, TrafficWatch: Timeout.InfiniteTimeSpan), time, new ServiceJournal(time));
        // Подключенный снимок задается напрямую: настоящий запуск ядер здесь не нужен.
        var status = ConnectionStatus.Initial(time.GetUtcNow()) with { Mode = mode };
        status = status.MoveTo(ConnectionState.Connecting, time.GetUtcNow()).MoveTo(ConnectionState.Connected, time.GetUtcNow());
        typeof(Gateway).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, status);
        typeof(Gateway).GetField("_probe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, new ProbeLink(10809, "user", "password"));
        return gateway;
    }
}
