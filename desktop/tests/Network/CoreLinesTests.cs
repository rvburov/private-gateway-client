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
}
