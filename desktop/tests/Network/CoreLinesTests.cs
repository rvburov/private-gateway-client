using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Уровни строк ядер и туннеля в журнале (<see cref="CoreLines"/>): отказ ядра и заметки службы о запуске туннеля — в разделе
/// «Ошибки и предупреждения» отчета; ошибки отдельных соединений и строки запуска — нет.
/// </summary>
public sealed class CoreLinesTests
{
    [Theory]
    [InlineData("sing-box", "FATAL[0000] start service: configure tun interface: set ipv4 address: The object already exists", JournalLevel.Error)]
    [InlineData("sing-box", "FATAL start service: start inbound/tun[tun-in]: open interface take too much time", JournalLevel.Error)]
    [InlineData("sing-box", "PANIC[0001] runtime error", JournalLevel.Error)]
    [InlineData("sing-box", "panic: runtime error: invalid memory address", JournalLevel.Error)]
    [InlineData("xray", "Failed to start: main: failed to create server > app/proxyman/inbound: failed to listen TCP on 43003", JournalLevel.Error)]
    [InlineData("service", "sing-box вышел при запуске (код 1) — еще одна попытка", JournalLevel.Warning)]
    [InlineData("service", "Сняты адреса TUN, оставшиеся от прошлых запусков: 172.19.0.1/30 (Private Gateway)", JournalLevel.Warning)]
    [InlineData("sing-box", "ERROR [3829 1.2s] inbound/tun[tun-in]: process connection from 172.19.0.1:5555: i/o timeout", JournalLevel.Info)]
    [InlineData("sing-box", "WARN inbound/tun[tun-in]: open interface take too much time to finish!", JournalLevel.Info)]
    [InlineData("sing-box", "INFO sing-box started (0.42s)", JournalLevel.Info)]
    [InlineData("xray", "2026/10/31 10:00:00.000000 [Warning] core: Xray 26.10.21 started", JournalLevel.Info)]
    [InlineData("xray", "2026/10/31 10:00:01.000000 [Error] app/dispatcher: failed to process outbound traffic", JournalLevel.Info)]
    public void Level_of_a_line(string source, string line, JournalLevel level) => CoreLines.Level(source, line).ShouldBe(level);

    [Fact]
    public void Reason_of_a_refused_server_is_the_last_outbound_failure_of_xray_and_not_dns()
    {
        // Отчет пользователя 2026-11-06 18:46 (журнал «Отладка»): конец занят строками DNS, отказ сервера — выше.
        var now = new DateTimeOffset(2026, 11, 6, 15, 46, 16, TimeSpan.Zero);
        JournalEntry Xray(int secondsAgo, string line) => new(now.AddSeconds(-secondsAgo), JournalLevel.Info, "xray", line);
        var entries = new List<JournalEntry>
        {
            Xray(300, "2026/11/06 18:41:16.000000 [Warning] [11] app/proxyman/outbound: failed to process outbound traffic > old"),
            Xray(20, "2026/11/06 18:45:56.000000 [Info] [12] proxy/vless/outbound: tunneling request to tcp:***:443 via ***:8443"),
            Xray(15, "2026/11/06 18:46:01.000000 [Warning] [12] app/proxyman/outbound: failed to process outbound traffic > "
                + "proxy/vless/outbound: failed to find an available destination > common/retry: all retry attempts failed"),
            Xray(3, "2026/11/06 18:46:13.000000 [Error] app/dns: failed to retrieve response for ***. > context deadline exceeded"),
            Xray(2, "2026/11/06 18:46:14.000000 [Info] proxy/dns: ip query > app/dns: returning nil for domain *** > app/dns: record not found"),
            new(now.AddSeconds(-1), JournalLevel.Info, "sing-box", "ERROR [1 0ms] outbound/shadowsocks[xray]: failed to process outbound traffic"),
        };

        CoreLines.OutboundFailure(entries, now.AddMinutes(-2)).ShouldBe(
            "[Warning] [12] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available "
            + "destination > common/retry: all retry attempts failed");
        CoreLines.OutboundFailure(entries, now.AddSeconds(-10)).ShouldBeNull("после отказа — только DNS и чужие строки");
        CoreLines.OutboundFailure([Xray(1, "2026/11/06 18:46:15.000000 [Warning] [13] transport/internet/reality: REALITY: failed to verify "
            + new string('x', 400))], now.AddMinutes(-2))!.Length.ShouldBe(CoreLines.ReasonLength + 1, "длинная причина обрезана");
    }
}
