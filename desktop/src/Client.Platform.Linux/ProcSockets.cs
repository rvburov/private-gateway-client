using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Сокеты Linux для инспектора: таблицы <c>/proc/net/tcp</c>, <c>tcp6</c>, <c>udp</c>, <c>udp6</c>, владелец — по номеру
/// inode сокета среди открытых файлов <c>/proc/[pid]/fd</c> (чужие процессы видны только root).
/// </summary>
public static class ProcSockets
{
    /// <summary>Все сокеты машины с владельцами; таблиц нет (не Linux) — <see cref="IOException"/>.</summary>
    public static IReadOnlyList<SocketEntry> Read(string proc = "/proc")
    {
        var sockets = new List<(SocketEntry Socket, long Inode)>();
        foreach (var (file, protocol) in new[] { ("tcp", SocketProtocol.Tcp), ("tcp6", SocketProtocol.Tcp), ("udp", SocketProtocol.Udp), ("udp6", SocketProtocol.Udp) })
        {
            var path = Path.Combine(proc, "net", file);
            try
            {
                sockets.AddRange(Parse(File.ReadAllText(path), protocol));
            }
            catch (FileNotFoundException) when (file.EndsWith('6'))
            {
                // IPv6 в ядре выключен.
            }
            catch (DirectoryNotFoundException e)
            {
                throw new IOException("нет " + path, e);
            }
        }

        var owners = Owners(proc);
        return [.. sockets.Select(s => owners.TryGetValue(s.Inode, out var pid) ? s.Socket with { ProcessId = pid } : s.Socket)];
    }

    /// <summary>
    /// Строки таблицы <c>/proc/net/*</c>: адреса — шестнадцатеричные слова в порядке байт машины, порт — число; состояние
    /// TCP — код ядра (01 — установлено, 0A — слушает); у UDP «07» — не связан, «01» — связан с одной стороной.
    /// </summary>
    public static IEnumerable<(SocketEntry Socket, long Inode)> Parse(string table, SocketProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var line in table.Split('\n').Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 10 || Endpoint(columns[1]) is not { } local || Endpoint(columns[2]) is not { } remote
                || !int.TryParse(columns[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state)
                || !long.TryParse(columns[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                continue;
            }

            var connected = remote.Port != 0 || !remote.Address.Equals(IPAddress.Any) && !remote.Address.Equals(IPAddress.IPv6Any);
            var socketState = protocol == SocketProtocol.Udp ? SocketState.Open : state switch
            {
                0x01 => SocketState.Established,
                0x02 or 0x03 => SocketState.Opening,
                0x0A => SocketState.Listen,
                _ => SocketState.Closing,
            };
            // У слушающего сокета другой стороны нет — в таблице нули.
            yield return (new SocketEntry(protocol, local, connected ? remote : null, socketState, null), inode);
        }
    }

    /// <summary>«0100007F:9571» → 127.0.0.1:38257; IPv6 — четыре слова по восемь знаков.</summary>
    public static IPEndPoint? Endpoint(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split(':');
        if (parts.Length != 2 || parts[0].Length is not (8 or 32)
            || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        var bytes = new byte[parts[0].Length / 2];
        for (var word = 0; word < parts[0].Length / 8; word++)
        {
            if (!uint.TryParse(parts[0].AsSpan(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            // Ядро печатает слово адреса так, как оно лежит в памяти машины x86/ARM — младшим байтом вперед.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word * 4, 4), value);
        }

        return new IPEndPoint(new IPAddress(bytes), port);
    }

    /// <summary>Владельцы сокетов: inode → номер процесса по ссылкам «socket:[inode]» в <c>/proc/[pid]/fd</c>.</summary>
    public static Dictionary<long, int> Owners(string proc = "/proc")
    {
        var owners = new Dictionary<long, int>();
        foreach (var folder in SafeDirectories(proc))
        {
            if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            foreach (var fd in SafeFiles(Path.Combine(folder, "fd")))
            {
                string? target;
                try
                {
                    target = new FileInfo(fd).LinkTarget;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']')
                    && long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
                {
                    owners.TryAdd(inode, pid);
                }
            }
        }

        return owners;
    }

    // Процесс мог выйти, а к чужому может не быть прав — тогда его просто не видно.
    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
