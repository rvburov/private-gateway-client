using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Logging;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Пара удерживает порт для TCP и UDP одновременно. Номер из FreeForTcpAndUdp после возврата уже не удерживается:
/// новый Bind тогда проверял бы межпроцессную гонку, а не выбор порта. На Windows диапазоны TCP и UDP бывают разными.
/// </summary>
public sealed class LocalPortTests(ITestOutputHelper output)
{
    [Fact]
    public void Test_ports_are_separate_from_service_ports_and_never_repeat()
    {
        var ports = Enumerable.Range(0, 100).Select(_ => XrayKit.FreePort()).ToArray();
        ports.Distinct().Count().ShouldBe(ports.Length);
        foreach (var port in ports)
        {
            port.ShouldBeInRange(1024, LocalPort.First - 1,
                "дочерняя служба не должна выбирать внутренний вход в диапазоне серверов и прокси стенда");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reservation_holds_both_tcp_and_udp_while_it_is_alive(bool fromSystem)
    {
        using var lease = Reserve(fromSystem);

        lease.Port.ShouldBeInRange(1, 65535);
        CannotBind(lease.Port, ProtocolType.Tcp, fromSystem);
        CannotBind(lease.Port, ProtocolType.Udp, fromSystem);
    }

    private static LocalPortLease Reserve(bool fromSystem)
    {
        if (!fromSystem)
        {
            return LocalPort.ReserveForTcpAndUdp();
        }

        // Кандидат уже выдан: все 500 обычных попыток получают его же и ведут в запасной путь ОС.
        // Удерживаемая пара не дает случайному первому выбору из ОС совпасть с ней.
        using var previous = LocalPort.ReserveForTcpAndUdp(new FixedPortRandom(LocalPort.First));
        var forced = new FixedPortRandom(LocalPort.First);
        var lease = LocalPort.ReserveForTcpAndUdp(forced);
        try
        {
            forced.Calls.ShouldBe(500, "запасной путь достигнут ограниченным числом обычных попыток");
            lease.Port.ShouldNotBe(previous.Port, "первая пара еще удерживает свой порт");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void CannotBind(int port, ProtocolType protocol, bool fromSystem)
    {
        using var competing = new Socket(AddressFamily.InterNetwork,
            protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol)
        { ExclusiveAddressUse = false };
        competing.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            var error = Should.Throw<SocketException>(() =>
            {
                competing.Bind(new IPEndPoint(IPAddress.Loopback, port));
                if (protocol == ProtocolType.Tcp)
                {
                    competing.Listen(1);
                }
            }, $"пока пара удерживает порт {port}, второй {protocol} вход должен быть отклонен и с запросом совместного использования");
            error.SocketErrorCode.ShouldBeOneOf(SocketError.AddressAlreadyInUse, SocketError.AccessDenied);
        }
        catch (Exception error) when (error is SocketException or ShouldAssertException)
        {
            Diagnose(port, fromSystem ? "запасной путь ОС" : "основной диапазон", protocol, error);
            throw;
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

    private void Diagnose(int port, string source, ProtocolType protocol, Exception error)
    {
        var code = error is SocketException socket ? socket.SocketErrorCode.ToString() : error.GetType().Name;
        Write($"Пара LocalPort: порт={port}, источник={source}, протокол={protocol}, ошибка={code}");
        Snapshot("TCP listeners", () => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == port).Select(SafeEndpoint));
        Snapshot("TCP connections", () => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(connection => connection.LocalEndPoint.Port == port)
            .Select(connection => $"{SafeEndpoint(connection.LocalEndPoint)} state={connection.State}"));
        Snapshot("UDP listeners", () => IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
            .Where(endpoint => endpoint.Port == port).Select(SafeEndpoint));
    }

    private void Snapshot(string kind, Func<IEnumerable<string>> read)
    {
        try
        {
            var endpoints = read().Order(StringComparer.Ordinal).ToArray();
            Write($"{kind}: {(endpoints.Length == 0 ? "нет совпадений" : string.Join(", ", endpoints))}");
        }
        catch (Exception error)
        {
            // Снимок неатомарный и может быть недоступен на этой ОС; исходный SocketException остается причиной отказа.
            Write($"{kind}: снимок недоступен ({error.GetType().Name})");
        }
    }

    private void Write(string text)
    {
        try
        {
            output.WriteLine(SecretMasker.Mask(text));
        }
        catch (Exception)
        {
            // Даже отказ тестового журнала не должен подменить исходную ошибку сокета.
        }
    }

    private static string SafeEndpoint(IPEndPoint endpoint) =>
        IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)
            ? endpoint.ToString()
            : $"[адрес]:{endpoint.Port}";

    private sealed class FixedPortRandom(int port) : Random
    {
        public int Calls { get; private set; }

        public override int Next(int minValue, int maxValue)
        {
            port.ShouldBeInRange(minValue, maxValue - 1);
            Calls++;
            return port;
        }
    }
}
