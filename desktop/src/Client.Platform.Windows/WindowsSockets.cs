using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Сокеты Windows для инспектора: таблицы IP Helper с владельцем (<c>GetExtendedTcpTable</c>, <c>GetExtendedUdpTable</c>,
/// IPv4 и IPv6). Номер процесса Windows дает и без прав администратора.
/// </summary>
public static class WindowsSockets
{
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Все сокеты TCP и UDP машины; таблица не прочиталась — <see cref="IOException"/>.</summary>
    public static IReadOnlyList<SocketEntry> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return
        [
            .. ParseTcp4(Table(AddressFamily.InterNetwork, tcp: true)),
            .. ParseTcp6(Table(AddressFamily.InterNetworkV6, tcp: true)),
            .. ParseUdp4(Table(AddressFamily.InterNetwork, tcp: false)),
            .. ParseUdp6(Table(AddressFamily.InterNetworkV6, tcp: false)),
        ];
    }

    /// <summary>MIB_TCPTABLE_OWNER_PID: число строк, строки по 24 байта (состояние, адрес, порт, адрес, порт, процесс).</summary>
    public static IEnumerable<SocketEntry> ParseTcp4(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var row in Rows(table, 24, 4))
        {
            var state = State(BinaryPrimitives.ReadInt32LittleEndian(row));
            yield return new SocketEntry(SocketProtocol.Tcp, V4(row, 4, 8), state == SocketState.Listen ? null : V4(row, 12, 16), state, Pid(row, 20));
        }
    }

    /// <summary>
    /// MIB_TCP6TABLE_OWNER_PID: строки по 56 байт (адрес 16, зона, порт, адрес 16, зона, порт, состояние, процесс).
    /// </summary>
    public static IEnumerable<SocketEntry> ParseTcp6(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var row in Rows(table, 56, 4))
        {
            var state = State(BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(48)));
            yield return new SocketEntry(SocketProtocol.Tcp, V6(row, 0, 20), state == SocketState.Listen ? null : V6(row, 24, 44), state, Pid(row, 52));
        }
    }

    /// <summary>MIB_UDPTABLE_OWNER_PID: строки по 12 байт (адрес, порт, процесс).</summary>
    public static IEnumerable<SocketEntry> ParseUdp4(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var row in Rows(table, 12, 4))
        {
            yield return new SocketEntry(SocketProtocol.Udp, V4(row, 0, 4), null, SocketState.Open, Pid(row, 8));
        }
    }

    /// <summary>MIB_UDP6TABLE_OWNER_PID: строки по 28 байт (адрес 16, зона, порт, процесс).</summary>
    public static IEnumerable<SocketEntry> ParseUdp6(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var row in Rows(table, 28, 4))
        {
            yield return new SocketEntry(SocketProtocol.Udp, V6(row, 0, 20), null, SocketState.Open, Pid(row, 24));
        }
    }

    /// <summary>Состояние MIB_TCP_STATE: 2 — слушает, 5 — установлено, 3–4 — устанавливается, остальное — закрывается.</summary>
    public static SocketState State(int state) => state switch
    {
        2 => SocketState.Listen,
        5 => SocketState.Established,
        3 or 4 => SocketState.Opening,
        _ => SocketState.Closing,
    };

    // Строки таблицы: после числа строк (4 байта) — строки одного размера; таблица короче, чем обещает, — сколько есть.
    private static List<byte[]> Rows(byte[] table, int size, int offset)
    {
        if (table.Length < 4)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        var rows = new List<byte[]>();
        for (var i = 0; i < count && offset + (i + 1) * size <= table.Length; i++)
        {
            rows.Add(table.AsSpan(offset + i * size, size).ToArray());
        }

        return rows;
    }

    // Адрес IPv4 — четыре байта в порядке сети, порт — младшие два байта слова в порядке сети.
    private static IPEndPoint V4(byte[] row, int address, int port) =>
        new(new IPAddress(row.AsSpan(address, 4)), BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(port)));

    private static IPEndPoint V6(byte[] row, int address, int port) =>
        new(new IPAddress(row.AsSpan(address, 16)), BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(port)));

    private static int? Pid(byte[] row, int offset) => BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(offset)) is var value and > 0 ? value : null;

    private static byte[] Table(AddressFamily family, bool tcp)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[Math.Max(size, 4)];
            size = buffer.Length;
            var result = tcp
                ? GetExtendedTcpTable(buffer, ref size, false, (int)family, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(buffer, ref size, false, (int)family, UdpTableOwnerPid, 0);
            if (result == 0)
            {
                return buffer;
            }

            if (result != ErrorInsufficientBuffer)
            {
                throw new IOException("Таблица сокетов не прочиталась", new Win32Exception(result));
            }
        }

        throw new IOException("Таблица сокетов меняется быстрее, чем читается");
    }

    // Без строк в структурах — LibraryImport не нужен.
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExtendedTcpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExtendedUdpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);
}
