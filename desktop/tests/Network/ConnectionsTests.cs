using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Соединения и трафик (этап 7): разбор сокетов каждой ОС на образцах (/proc, таблицы IP Helper, поля lsof), владелец своего
/// сокета на этой машине, счетчики адаптеров, имена процессов и вызовы через IPC. Только чтение — ни один тест ничего не
/// меняет.
/// </summary>
public sealed class ConnectionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Linux_sockets_come_from_proc_net_tables()
    {
        var tcp = ProcSockets.Parse("""
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:9571 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 154 1 0000000081a3f143 100 0 0 10 0
               1: 170200C0:C822 507100CB:01BB 01 00000000:00000000 00:00000000 00000000  1000        0 9001 1 0000000000000000 20 4 30 10 -1
               2: 170200C0:C823 507100CB:01BB 06 00000000:00000000 03:00000F5C 00000000     0        0 0 3 0000000000000000
               3: 170200C0:C824 507100CB:01BB 02 00000000:00000000 01:00000064 00000002  1000        0 9002 1 0000000000000000 100 0 0 10 -1
            """, SocketProtocol.Tcp).ToList();

        tcp.ShouldBe([
            (new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(IPAddress.Loopback, 38257), null, SocketState.Listen, null), 154L),
            (new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51234), Ep("203.0.113.80", 443), SocketState.Established, null), 9001L),
            (new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51235), Ep("203.0.113.80", 443), SocketState.Closing, null), 0L),
            (new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51236), Ep("203.0.113.80", 443), SocketState.Opening, null), 9002L),
        ], "слушает — без другой стороны; TIME_WAIT — закрывается; SYN_SENT — устанавливается");

        ProcSockets.Parse("""
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000001000000:0016 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 77 1 0000000000000000 100 0 0 10 0
               1: B80D0120000000000000000023000000:C822 B80D0120000000000000000080000000:01BB 01 00000000:00000000 00:00000000 00000000     0        0 78 1 0000000000000000 100 0 0 10 0
            """, SocketProtocol.Tcp).Select(s => s.Socket).ShouldBe([
            new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(IPAddress.IPv6Loopback, 22), null, SocketState.Listen, null),
            new SocketEntry(SocketProtocol.Tcp, Ep("2001:db8::23", 51234), Ep("2001:db8::80", 443), SocketState.Established, null),
        ], "IPv6 — четыре слова, каждое младшим байтом вперед");

        ProcSockets.Parse("""
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
             100: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000   101        0 3001 2 0000000000000000 0
             101: 170200C0:C350 5A7100CB:01BB 01 00000000:00000000 00:00000000 00000000  1000        0 3002 2 0000000000000000 0
            """, SocketProtocol.Udp).Select(s => s.Socket).ShouldBe([
            new SocketEntry(SocketProtocol.Udp, Ep("0.0.0.0", 5353), null, SocketState.Open, null),
            new SocketEntry(SocketProtocol.Udp, Ep("192.0.2.23", 50000), Ep("203.0.113.90", 443), SocketState.Open, null),
        ], "UDP без связи — без другой стороны, связанный — с ней");

        ProcSockets.Endpoint("0100007F").ShouldBeNull();
        ProcSockets.Endpoint("ZZ00007F:0016").ShouldBeNull();
    }

    [Fact]
    public void Windows_sockets_come_from_ip_helper_tables()
    {
        var tcp4 = Table(24, Row24(5, "192.0.2.23", 51234, "203.0.113.80", 443, 4120), Row24(2, "127.0.0.1", 10808, "0.0.0.0", 0, 2208));
        WindowsSockets.ParseTcp4(tcp4).ShouldBe([
            new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51234), Ep("203.0.113.80", 443), SocketState.Established, 4120),
            new SocketEntry(SocketProtocol.Tcp, Ep("127.0.0.1", 10808), null, SocketState.Listen, 2208),
        ]);

        var row6 = new byte[56];
        IPAddress.Parse("2001:db8::23").GetAddressBytes().CopyTo(row6, 0);
        BinaryPrimitives.WriteUInt16BigEndian(row6.AsSpan(20), 51234);
        IPAddress.Parse("2001:db8::80").GetAddressBytes().CopyTo(row6, 24);
        BinaryPrimitives.WriteUInt16BigEndian(row6.AsSpan(44), 443);
        BinaryPrimitives.WriteInt32LittleEndian(row6.AsSpan(48), 5);
        BinaryPrimitives.WriteInt32LittleEndian(row6.AsSpan(52), 4120);
        WindowsSockets.ParseTcp6(Table(56, row6)).ShouldBe([
            new SocketEntry(SocketProtocol.Tcp, Ep("2001:db8::23", 51234), Ep("2001:db8::80", 443), SocketState.Established, 4120),
        ]);

        var udp4 = new byte[12];
        IPAddress.Any.GetAddressBytes().CopyTo(udp4, 0);
        BinaryPrimitives.WriteUInt16BigEndian(udp4.AsSpan(4), 5353);
        WindowsSockets.ParseUdp4(Table(12, udp4)).ShouldBe([new SocketEntry(SocketProtocol.Udp, Ep("0.0.0.0", 5353), null, SocketState.Open, null)],
            "процесс 0 — ОС не назвала владельца");

        var udp6 = new byte[28];
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(udp6, 0);
        BinaryPrimitives.WriteUInt16BigEndian(udp6.AsSpan(20), 53);
        BinaryPrimitives.WriteInt32LittleEndian(udp6.AsSpan(24), 900);
        WindowsSockets.ParseUdp6(Table(28, udp6)).ShouldBe([new SocketEntry(SocketProtocol.Udp, new IPEndPoint(IPAddress.IPv6Loopback, 53), null, SocketState.Open, 900)]);

        byte[] truncated = [.. Table(24, Row24(5, "192.0.2.23", 1, "203.0.113.80", 2, 3)), .. new byte[10]];
        BinaryPrimitives.WriteInt32LittleEndian(truncated, 5);
        WindowsSockets.ParseTcp4(truncated).Count().ShouldBe(1, "строк меньше, чем обещано, — сколько есть");
        WindowsSockets.State(4).ShouldBe(SocketState.Opening);
        WindowsSockets.State(12).ShouldBe(SocketState.Closing);
    }

    [Fact]
    public void Macos_sockets_come_from_lsof_fields()
    {
        var found = LsofSockets.Parse("""
            p4120
            cfirefox
            f45
            PTCP
            n192.0.2.23:51234->203.0.113.80:443
            TST=ESTABLISHED
            TQR=0
            f46
            PUDP
            n*:5353
            p880
            csshd
            f3
            PTCP
            n*:22
            TST=LISTEN
            f4
            PTCP
            n[::1]:22
            TST=LISTEN
            f5
            PTCP
            n[fe80::1%5]:631->[fe80::2%5]:50000
            TST=CLOSE_WAIT
            """).ToList();

        found.ShouldBe([
            (new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51234), Ep("203.0.113.80", 443), SocketState.Established, 4120), "firefox"),
            (new SocketEntry(SocketProtocol.Udp, Ep("0.0.0.0", 5353), null, SocketState.Open, 4120), "firefox"),
            (new SocketEntry(SocketProtocol.Tcp, Ep("0.0.0.0", 22), null, SocketState.Listen, 880), "sshd"),
            (new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(IPAddress.IPv6Loopback, 22), null, SocketState.Listen, 880), "sshd"),
            (new SocketEntry(SocketProtocol.Tcp, Ep("fe80::1", 631), Ep("fe80::2", 50000), SocketState.Closing, 880), "sshd"),
        ], "имя процесса — у всех его файлов; зона адреса — не часть адреса");
        LsofSockets.Parse("p1\ncx\nf1\nPRAW\nn*:0\n").ShouldBeEmpty("не TCP и не UDP — мимо");
    }

    [Fact]
    public void This_machine_names_the_owner_of_our_own_listener()
    {
        var details = ServiceHost.NetworkDetails();
        Assert.SkipWhen(details is null, "у этой ОС нет подробностей сети");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var mine = details.Sockets().Where(s => s.Protocol == SocketProtocol.Tcp && s.Local.Port == port && s.State == SocketState.Listen).ToList();

            mine.ShouldNotBeEmpty("свой слушающий сокет ОС видит всегда");
            mine.ShouldAllBe(s => s.ProcessId == Environment.ProcessId, "владелец своего сокета виден и без прав");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Connections_get_process_names_and_a_failed_read_is_empty()
    {
        var journal = new ServiceJournal(TimeProvider.System);
        var own = new SocketEntry(SocketProtocol.Tcp, Ep("127.0.0.1", 10808), null, SocketState.Listen, Environment.ProcessId);
        var gone = new SocketEntry(SocketProtocol.Udp, Ep("0.0.0.0", 5353), null, SocketState.Open, int.MaxValue);
        var unknown = new SocketEntry(SocketProtocol.Udp, Ep("0.0.0.0", 5354), null, SocketState.Open, null);

        new NetworkInspector(new FakeDetails([own, gone, unknown]), TimeProvider.System, journal).Connections().ShouldBe([
            new NetworkConnection(own, Process.GetCurrentProcess().ProcessName),
            new NetworkConnection(gone, null),
            new NetworkConnection(unknown, null),
        ], "процесса нет — без имени, а не отказ");

        new NetworkInspector(new FakeDetails(null), TimeProvider.System, journal).Connections().ShouldBeEmpty();
        journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message.StartsWith("Инспектор: сокеты не прочитаны", StringComparison.Ordinal));
    }

    [Fact]
    public void Traffic_counters_of_this_machine_grow_and_skip_the_loopback()
    {
        var inspector = new NetworkInspector(null, TimeProvider.System, new ServiceJournal(TimeProvider.System));

        var first = inspector.Traffic();

        first.Adapters.ShouldNotBeEmpty("у машины CI есть включенный адаптер");
        first.Adapters.ShouldAllBe(a => a.Received >= 0 && a.Sent >= 0);
        first.Adapters.ShouldNotContain(a => a.Name == "lo" || a.Name == "lo0" || a.Name.StartsWith("Loopback", StringComparison.Ordinal));
        var second = inspector.Traffic();
        second.TakenAt.ShouldBeGreaterThanOrEqualTo(first.TakenAt);
        foreach (var adapter in second.Adapters)
        {
            if (first.Adapters.FirstOrDefault(a => a.Name == adapter.Name) is { } before)
            {
                adapter.Received.ShouldBeGreaterThanOrEqualTo(before.Received, "счетчики только растут");
            }
        }
    }

    [Fact]
    public void Connection_and_traffic_go_through_the_contract_and_come_back_the_same()
    {
        NetworkConnection[] connections =
        [
            new(new SocketEntry(SocketProtocol.Tcp, Ep("192.0.2.23", 51234), Ep("203.0.113.80", 443), SocketState.Established, 4120), "firefox"),
            new(new SocketEntry(SocketProtocol.Udp, new IPEndPoint(IPAddress.IPv6Any, 5353), null, SocketState.Open, null), null),
        ];
        foreach (var connection in connections)
        {
            connection.ToContract().ToCore().ShouldBe(connection);
        }

        foreach (var state in Enum.GetValues<SocketState>())
        {
            state.ToContract().ToCore().ShouldBe(state);
        }

        new V1.NetworkConnection { Protocol = V1.SocketProtocol.Tcp, LocalAddress = "not an address" }.ToCore().ShouldBeNull();
        new V1.NetworkConnection { LocalAddress = "127.0.0.1" }.ToCore().ShouldBeNull("протокол не задан");

        var sample = new TrafficSample(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), [new AdapterCounters("Wi-Fi", 1_288_490_188, 241_172_480)]);
        var back = sample.ToContract().ToCore();
        back.TakenAt.ShouldBe(sample.TakenAt);
        back.Adapters.ShouldBe(sample.Adapters);
    }

    [Fact]
    public async Task Window_lists_connections_and_watches_traffic_through_the_service()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var service = await RunningService.StartAsync(options: o => o with { NetworkDetails = ServiceHost.NetworkDetails() });
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connections = (await client.ListConnectionsAsync(Ct)).ShouldNotBeNull();
            connections.ShouldContain(c => c.Socket.Local.Port == port && c.Socket.State == SocketState.Listen
                && c.Socket.ProcessId == Environment.ProcessId && c.Process == Process.GetCurrentProcess().ProcessName);
        }
        finally
        {
            listener.Stop();
        }

        var samples = new List<TrafficSample>();
        await foreach (var sample in client.WatchTrafficAsync(Ct))
        {
            samples.Add(sample);
            if (samples.Count == 2)
            {
                break;
            }
        }

        (samples[1].TakenAt - samples[0].TakenAt).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500), "раз в секунду, а не потоком");
    }

    private static IPEndPoint Ep(string address, int port) => new(IPAddress.Parse(address), port);

    private static byte[] Table(int size, params byte[][] rows)
    {
        var table = new byte[4 + (size * rows.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(table, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].CopyTo(table, 4 + (i * size));
        }

        return table;
    }

    private static byte[] Row24(int state, string local, int localPort, string remote, int remotePort, int pid)
    {
        var row = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(row, state);
        IPAddress.Parse(local).GetAddressBytes().CopyTo(row, 4);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(8), (ushort)localPort);
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 12);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(16), (ushort)remotePort);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(20), pid);
        return row;
    }

    // Подробности ОС с заданными сокетами; null — сокеты не прочитались.
    private sealed class FakeDetails(IReadOnlyList<SocketEntry>? sockets) : INetworkDetails
    {
        public IReadOnlyList<NetworkRoute> Routes() => [];

        public DhcpLease? Dhcp(string adapter) => null;

        public IReadOnlyList<SocketEntry> Sockets() => sockets ?? throw new IOException("нет прав");
    }
}
