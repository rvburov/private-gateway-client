using System.Globalization;
using System.Net;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Сокеты macOS для инспектора: <c>lsof -nP -iTCP -iUDP -F pcPnT</c> — поля по строке («p» — процесс, «c» — его имя,
/// «f» — новый файл, «P» — протокол, «n» — адреса, «T» — состояние TCP). /proc на macOS нет, а формат полей lsof не
/// меняется с версией. Чужие процессы видны только root.
/// </summary>
public static class LsofSockets
{
    /// <summary>Аргументы lsof: без имен и служб (-n -P), только TCP и UDP, поля для разбора.</summary>
    public static readonly IReadOnlyList<string> Arguments = ["-nP", "-iTCP", "-iUDP", "-F", "pcPnT"];

    /// <summary>Сокеты и имена процессов из полей lsof.</summary>
    public static IEnumerable<(SocketEntry Socket, string? Process)> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        int? pid = null;
        string? command = null;
        string? protocol = null, name = null, state = null;
        var found = new List<(SocketEntry, string?)>();

        void Flush()
        {
            if (protocol is not null && name is not null && Entry(protocol, name, state, pid) is { } entry)
            {
                found.Add((entry, command));
            }

            (protocol, name, state) = (null, null, null);
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var value = line[1..];
            switch (line[0])
            {
                case 'p':
                    Flush();
                    pid = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
                    command = null;
                    break;
                case 'c':
                    command = value;
                    break;
                case 'f':
                    Flush();
                    break;
                case 'P':
                    protocol = value;
                    break;
                case 'n':
                    name = value;
                    break;
                case 'T' when value.StartsWith("ST=", StringComparison.Ordinal):
                    state = value[3..];
                    break;
            }
        }

        Flush();
        return found;
    }

    // «127.0.0.1:8080->203.0.113.10:443», «*:443», «[::1]:631».
    private static SocketEntry? Entry(string protocol, string name, string? state, int? pid)
    {
        var kind = protocol switch
        {
            "TCP" => SocketProtocol.Tcp,
            "UDP" => SocketProtocol.Udp,
            _ => (SocketProtocol?)null,
        };
        if (kind is null)
        {
            return null;
        }

        var ends = name.Split("->");
        if (Endpoint(ends[0]) is not { } local)
        {
            return null;
        }

        var remote = ends.Length > 1 ? Endpoint(ends[1]) : null;
        var socketState = kind == SocketProtocol.Udp ? SocketState.Open : state switch
        {
            "LISTEN" => SocketState.Listen,
            "ESTABLISHED" => SocketState.Established,
            "SYN_SENT" or "SYN_RECEIVED" => SocketState.Opening,
            _ => SocketState.Closing,
        };
        return new SocketEntry(kind.Value, local, socketState == SocketState.Listen ? null : remote, socketState, pid);
    }

    private static IPEndPoint? Endpoint(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > IPEndPoint.MaxPort)
        {
            return null;
        }

        var host = text[..colon].Trim('[', ']');
        var address = host == "*" ? IPAddress.Any : IPAddress.TryParse(host.Split('%')[0], out var parsed) ? parsed : null;
        return address is null ? null : new IPEndPoint(address, port);
    }
}
