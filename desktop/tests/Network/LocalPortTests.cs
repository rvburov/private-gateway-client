using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Порт входа ядра: свободен и для TCP, и для UDP. На Windows с занятыми диапазонами (Hyper-V, WinNAT)
/// порт, который ОС дает одному протоколу, у другого бывает закрыт — CI это ловит.
/// </summary>
public sealed class LocalPortTests
{
    [Fact]
    public void Port_takes_both_tcp_and_udp()
    {
        for (var i = 0; i < 50; i++)
        {
            var port = LocalPort.FreeForTcpAndUdp();

            using var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            port.ShouldBeGreaterThan(0);
        }
    }

    /// <summary>
    /// Порт — не из временных портов ОС (решение 478): их берут исходящие сокеты, и порт, выбранный заранее, к запуску ядра
    /// бывал занят (RelayTests, 2026-10-28: «bind: address already in use» у ретранслятора). Временные порты по умолчанию —
    /// Linux 32768–60999, Windows и macOS 49152–65535.
    /// </summary>
    [Fact]
    public void Port_is_outside_the_temporary_ports_of_the_os()
    {
        for (var i = 0; i < 200; i++)
        {
            var port = LocalPort.FreeForTcpAndUdp();

            port.ShouldBeGreaterThanOrEqualTo(1024);
            (port is >= 32768 and <= 60999 or >= 49152).ShouldBeFalse($"порт {port} — из временных портов Linux, Windows или macOS");
        }
    }

    /// <summary>
    /// Порт, выданный недавно, второй раз не выдается, даже если он все еще свободен: вход его пока не занял. Иначе два входа
    /// одного запуска ядра (замер задержки — по входу на сервер) получали один порт, и ядро не запускалось.
    /// </summary>
    [Fact]
    public void Port_given_recently_is_not_given_again()
    {
        var seed = Random.Shared.Next();
        var first = LocalPort.FreeForTcpAndUdp(new Random(seed));

        var second = LocalPort.FreeForTcpAndUdp(new Random(seed));

        second.ShouldNotBe(first, "тот же выбор случайного числа — а порт уже выдан");
        var batch = Enumerable.Range(0, 300).Select(_ => LocalPort.FreeForTcpAndUdp()).ToList();
        batch.Distinct().Count().ShouldBe(batch.Count, "по порту на вход замера — без повторов");
    }
}
