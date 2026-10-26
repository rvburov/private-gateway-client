using System.Collections.Concurrent;
using System.Diagnostics;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// «Провод» пространства: пакеты, которые ушли с адаптера наружу (AF_PACKET, только исходящие), — уже после nftables, то
/// есть то, что ушло бы в сеть. Строка на пакет: «4|6 протокол адрес порт» (у ICMP вместо порта — тип), ARP — «arp».
/// </summary>
internal sealed class WireTap : IDisposable
{
    private const string Script = """
        import socket, sys
        s = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.ntohs(3))
        s.bind((sys.argv[1], 0))
        print("ready", flush=True)
        while True:
            data, address = s.recvfrom(65535)
            if address[2] != socket.PACKET_OUTGOING:
                continue
            kind = int.from_bytes(data[12:14], "big")
            if kind == 0x0806:
                print("arp", flush=True)
            elif kind == 0x0800:
                start = 14 + (data[14] & 15) * 4
                protocol, l4 = data[23], data[start:]
                port = int.from_bytes(l4[2:4], "big") if protocol in (6, 17) else l4[0] if protocol == 1 else 0
                print(4, protocol, socket.inet_ntop(socket.AF_INET, data[30:34]), port, flush=True)
            elif kind == 0x86DD:
                protocol, l4 = data[20], data[54:]
                port = int.from_bytes(l4[2:4], "big") if protocol in (6, 17) else l4[0] if protocol == 58 else 0
                print(6, protocol, socket.inet_ntop(socket.AF_INET6, data[38:54]), port, flush=True)
            else:
                print("other", hex(kind), flush=True)
        """;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private WireTap(NetworkNamespace netns, string device)
    {
        var start = new ProcessStartInfo("nsenter") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--net=" + netns.Path, "python3", "-I", "-c", Script, device })
        {
            start.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == "ready")
            {
                _ready.TrySetResult();
            }
            else if (e.Data is { } line)
            {
                _lines.Enqueue(line);
            }
        };
        _process.Start();
        _process.BeginOutputReadLine();
    }

    /// <summary>Начать слушать адаптер <paramref name="device"/> пространства; вернется, когда сокет привязан.</summary>
    public static async Task<WireTap> StartAsync(NetworkNamespace netns, string device = "pgnet0")
    {
        var tap = new WireTap(netns, device);
        try
        {
            await tap._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return tap;
        }
        catch
        {
            tap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Служебное, что наружу данных не несет: ARP, соседи и роутеры IPv6 (ICMPv6 133–136), MLD (143), DHCP и DHCPv6.
    /// </summary>
    public static bool IsControl(string line)
    {
        var words = line.Split(' ');
        return line == "arp"
            || (words is ["6", "58", _, var type] && type is "133" or "134" or "135" or "136" or "143")
            || (words is ["4", "17", _, "67"] or ["6", "17", _, "547"]);
    }

    /// <summary>Перестать слушать; все, что ушло на провод за это время.</summary>
    public IReadOnlyList<string> Stop()
    {
        // Последние пакеты тестов — до остановки: строки уже в канале, ядро отдает их раньше выхода процесса.
        Thread.Sleep(200);
        Dispose();
        return [.. _lines];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        // Без срока — и до конца вывода: строки, прочитанные из канала, разобраны до возврата.
        _process.WaitForExit();

        _process.Dispose();
    }
}
