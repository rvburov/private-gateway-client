using System.Diagnostics;
using System.Globalization;
using System.Net;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Свое сетевое пространство Linux (unshare): в нем только то, что поднимет тест, — TUN, правила
/// nftables, маршруты. Сеть машины тесты так не трогают. Нужны root, unshare и nsenter.
/// </summary>
internal sealed class NetworkNamespace : IDisposable
{
    private static readonly string[] Tools = ["unshare", "nsenter"];

    // Номера сетей живых пространств процесса: тесты идут параллельно, и одинаковый адрес на двух парах veth
    // машины уводил бы пакеты одного теста в пространство другого.
    private static readonly HashSet<int> Taken = [];

    private int _octet;

    // Конец пары LinkAsMachine в сети машины.
    private string? _machineLink;

    // Концы всех пар пространства в сети машины: удаляются сразу, а не вместе с пространством, — его ядро Linux разбирает
    // позже, и пара с тем же адресом мешала бы следующему стенду с тем же номером сети.
    private readonly List<string> _links = [];

    private NetworkNamespace(string path) => Path = path;

    /// <summary>Файл пространства в /run/netns.</summary>
    public string Path { get; }

    /// <summary>Имя для <c>ip … netns</c>.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Номер сети пространства (1–249) для адресов его пар veth (10.252–10.254.N.x, 203.0.113.N): у живых
    /// пространств процесса не повторяется, освобождается вместе с пространством. Занятые на машине номера тоже
    /// пропускаются: пару veth прерванного прогона держит живой оставшееся ядро, и ответы с тем же адресом машина
    /// отправила бы в нее — запрос теста висел до тайм-аута.
    /// </summary>
    public int Octet
    {
        get
        {
            lock (Taken)
            {
                if (_octet == 0)
                {
                    var machine = MachineOctets();
                    var free = Enumerable.Range(1, 249).Where(o => !Taken.Contains(o) && !machine.Contains(o)).ToList();
                    _octet = free[Random.Shared.Next(free.Count)];
                    Taken.Add(_octet);
                }

                return _octet;
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Номера сетей, адреса которых (10.252–10.254.N.x, 203.0.113.N) уже есть в сети машины.</summary>
    public static HashSet<int> MachineOctets() => Octets(Host("ip", "-o", "-4", "addr", "show"));

    /// <summary>Номера сетей из вывода <c>ip -o -4 addr show</c>.</summary>
    public static HashSet<int> Octets(string addresses) =>
    [
        .. System.Text.RegularExpressions.Regex.Matches(addresses, @"inet (?:10\.25[234]\.(\d+)\.\d+|203\.0\.113\.(\d+))/")
            .Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>Пропустить тест, если пространства не создать: не Linux, не root или нет нужных программ.</summary>
    public static void SkipUnlessAvailable(params string[] tools)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "сетевые пространства есть только в Linux");
        Assert.SkipWhen(PeerCredentials.CurrentUid() != 0, "нужен root: сетевое пространство");
        var missing = Tools.Concat(tools).Where(tool =>
            !(Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':').Any(dir => File.Exists(System.IO.Path.Combine(dir, tool)))).ToList();
        Assert.SkipWhen(missing.Count > 0, "нет " + string.Join(", ", missing));
    }

    public static NetworkNamespace Create()
    {
        Directory.CreateDirectory("/run/netns");
        var path = "/run/netns/pg-test-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(path, string.Empty);
        Host("unshare", "--net=" + path, "true");
        var netns = new NetworkNamespace(path);
        netns.Run(null, "ip", "link", "set", "lo", "up");
        return netns;
    }

    /// <summary>Команда в сети машины (для второго конца veth); не вышла успешно — исключение с ее выводом.</summary>
    public static string Host(string program, params string[] arguments) => Execute(null, program, arguments);

    /// <summary>
    /// Пара veth: конец <paramref name="inside"/> в пространстве с адресом <paramref name="insideAddress"/>,
    /// второй — в сети машины с <paramref name="hostAddress"/> (оба /30). Уйдет вместе с пространством.
    /// Возвращает имя второго конца.
    /// </summary>
    public string Link(string inside, IPAddress hostAddress, IPAddress insideAddress)
    {
        var outside = "pgv" + Guid.NewGuid().ToString("N")[..8];
        Host("ip", "link", "add", outside, "type", "veth", "peer", "name", inside, "netns", Name);
        _links.Add(outside);
        Host("ip", "addr", "add", hostAddress + "/30", "dev", outside);
        Host("ip", "link", "set", outside, "up");
        Run(null, "ip", "addr", "add", insideAddress + "/30", "dev", inside);
        Run(null, "ip", "link", "set", inside, "up");
        return outside;
    }

    /// <summary>
    /// Пространство как машина в сети: пара veth к сети машины и маршрут по умолчанию в нее без шлюза.
    /// Ядра в пространстве выходят по паре к серверу теста на адресе машины <c>Host</c>; <c>Neighbor</c> —
    /// еще один адрес машины на той же паре, как другое устройство локальной сети; <c>Public</c> — адрес
    /// машины из диапазона документации, как сайт в интернете. К остальным адресам пакеты дальше пары не
    /// уходят — на них никто не отвечает.
    /// </summary>
    public (IPAddress Host, IPAddress Neighbor, IPAddress Public) LinkAsMachine()
    {
        var octet = Octet;
        var host = IPAddress.Parse($"10.252.{octet}.1");
        var neighbor = IPAddress.Parse($"10.252.{octet}.5");
        var @public = IPAddress.Parse($"203.0.113.{octet}");
        var outside = Link("pgnet0", host, IPAddress.Parse($"10.252.{octet}.2"));
        _machineLink = outside;
        Host("ip", "addr", "add", neighbor + "/32", "dev", outside);
        Host("ip", "addr", "add", @public + "/32", "dev", outside);
        Run(null, "ip", "route", "add", "default", "dev", "pgnet0");
        return (host, neighbor, @public);
    }

    /// <summary>
    /// Пропустить тест, если в ядре машины нет IPv6 или он выключен для новых адаптеров (контейнер облака). Не на Linux —
    /// причина та же, что у <see cref="SkipUnlessAvailable"/>: файла ядра там нет, но дело не в IPv6.
    /// </summary>
    public static void SkipUnlessIpv6()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "сетевые пространства есть только в Linux");
        const string Default = "/proc/sys/net/ipv6/conf/default/disable_ipv6";
        Assert.SkipUnless(File.Exists(Default) && File.ReadAllText(Default).Trim() == "0", "в ядре машины нет IPv6 или он выключен");
    }

    /// <summary>
    /// IPv6 на паре <see cref="LinkAsMachine"/>, как у сети провайдера с IPv6: адрес машины <c>Host</c>, адрес пространства и
    /// маршрут IPv6 по умолчанию в пару. Адреса — из 2001:db8::/32 (документация).
    /// </summary>
    public (IPAddress Host, IPAddress Inside) LinkIpv6()
    {
        var outside = _machineLink ?? throw new InvalidOperationException("сначала LinkAsMachine");
        var host = IPAddress.Parse($"2001:db8:77:{Octet:x}::1");
        var inside = IPAddress.Parse($"2001:db8:77:{Octet:x}::2");
        Host("ip", "-6", "addr", "add", host + "/64", "dev", outside, "nodad");
        Run(null, "ip", "-6", "addr", "add", inside + "/64", "dev", "pgnet0", "nodad");
        Run(null, "ip", "-6", "route", "add", "default", "dev", "pgnet0");
        return (host, inside);
    }

    /// <summary>Команда изнутри пространства с <paramref name="input"/> в stdin; не вышла успешно — исключение с ее выводом.</summary>
    public string Run(string? input, params string[] command) => Execute(input, "nsenter", ["--net=" + Path, .. command]);

    /// <summary>
    /// Страница с сайта теста из python3, а не curl (другая программа для правил по процессу); с меткой
    /// <paramref name="mark"/> (SO_MARK) — как пакеты выхода sing-box мимо туннеля, 0 — без метки; с привязкой к
    /// интерфейсу <paramref name="device"/> (SO_BINDTODEVICE) — как проверка связи NetworkManager и DNS systemd-resolved
    /// для карты: маршруты TUN такой сокет обходит. Не соединилось — исключение.
    /// </summary>
    public Task<string> FetchAsync(Uri url, uint mark = 0, int seconds = 10, string? device = null) => RunAsync("python3", "-c", """
        import socket, sys
        s = socket.socket()
        if int(sys.argv[3]):
            s.setsockopt(socket.SOL_SOCKET, 36, int(sys.argv[3]))
        if sys.argv[5]:
            s.setsockopt(socket.SOL_SOCKET, 25, sys.argv[5].encode())
        s.settimeout(float(sys.argv[4]))
        s.connect((sys.argv[1], int(sys.argv[2])))
        s.sendall(b"GET / HTTP/1.0\r\n\r\n")
        reply = b""
        while chunk := s.recv(4096):
            reply += chunk
        print(reply.decode().split("\r\n\r\n", 1)[1], end="")
        """, url.Host, url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), mark.ToString(System.Globalization.CultureInfo.InvariantCulture),
        seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), device ?? string.Empty);

    /// <summary>
    /// Страница с сайта теста из сокета, привязанного к адресу <paramref name="source"/>, — как кандидаты WebRTC и
    /// <c>curl --interface</c>: такие сокеты маршруты по адресу источника могут увести мимо TUN. Не соединилось — исключение.
    /// </summary>
    public Task<string> FetchFromAsync(Uri url, IPAddress source, int seconds = 5) => RunAsync("python3", "-c", """
        import socket, sys
        s = socket.create_connection((sys.argv[1], int(sys.argv[2])), float(sys.argv[4]), (sys.argv[3], 0))
        s.sendall(b"GET / HTTP/1.0\r\n\r\n")
        reply = b""
        while chunk := s.recv(4096):
            reply += chunk
        print(reply.decode().split("\r\n\r\n", 1)[1], end="")
        """, url.Host.Trim('[', ']'), url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), source.ToString(),
        seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Датаграмма UDP из пространства на <paramref name="address"/>:<paramref name="port"/>, групповая — через адрес
    /// <paramref name="source"/>; ответ — «sent». Отброшена блоком (EPERM) — исключение.
    /// </summary>
    public Task<string> SendUdpAsync(IPAddress address, int port, IPAddress source) => RunAsync("python3", "-c", """
        import socket, sys
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(sys.argv[3]))
        s.sendto(b"pg", (sys.argv[1], int(sys.argv[2])))
        print("sent", end="")
        """, address.ToString(), port.ToString(System.Globalization.CultureInfo.InvariantCulture), source.ToString());

    /// <summary>
    /// Датаграмма UDP к <paramref name="address"/> и ответ на нее — эхо; ответа нет за <paramref name="seconds"/> с или
    /// датаграмма отброшена — исключение.
    /// </summary>
    public Task<string> UdpEchoAsync(IPAddress address, int port, int seconds = 10) => RunAsync("python3", "-c", """
        import socket, sys
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(float(sys.argv[3]))
        s.sendto(b"echo", (sys.argv[1], int(sys.argv[2])))
        print(s.recvfrom(100)[0].decode(), end="")
        """, address.ToString(), port.ToString(System.Globalization.CultureInfo.InvariantCulture), seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Адреса имени — DoH (JSON, A и AAAA) изнутри пространства сокетом с меткой <paramref name="mark"/> (как поиск службы под
    /// блоком). Не соединилось — исключение.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> DohAsync(Uri doh, string name, uint mark)
    {
        var bodies = await RunAsync("python3", "-c", """
            import socket, sys
            for kind in ("A", "AAAA"):
                s = socket.socket()
                s.setsockopt(socket.SOL_SOCKET, 36, int(sys.argv[5]))
                s.settimeout(3)
                s.connect((sys.argv[1], int(sys.argv[2])))
                s.sendall(f"GET {sys.argv[3]}?name={sys.argv[4]}&type={kind} HTTP/1.0\r\nAccept: application/dns-json\r\n\r\n".encode())
                reply = b""
                while chunk := s.recv(4096):
                    reply += chunk
                print(reply.decode().split("\r\n\r\n", 1)[1].replace("\n", " "))
            """, doh.Host, doh.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), doh.AbsolutePath, name,
            mark.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return [.. bodies.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(PrivateGateway.Client.Service.Cores.LatencyProbe.DohAnswers)];
    }

    /// <summary>Эхо-запрос ICMP (как ping): «reply» — ответ пришел, «none» — нет за 2 с или отброшен.</summary>
    public Task<string> PingAsync(IPAddress address) => RunAsync("python3", "-c", """
        import socket, struct, sys
        s = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_ICMP)
        s.settimeout(2)
        body = struct.pack("!BBHHH", 8, 0, 0, 1, 1) + b"pg"
        total = sum(int.from_bytes(body[i:i + 2], "big") for i in range(0, len(body), 2))
        total = (total >> 16) + (total & 0xFFFF)
        body = body[:2] + struct.pack("!H", ~(total + (total >> 16)) & 0xFFFF) + body[4:]
        try:
            s.sendto(body, (sys.argv[1], 0))
            s.recv(100)
            print("reply", end="")
        except OSError:
            print("none", end="")
        """, address.ToString());

    /// <summary>
    /// Адрес имени через разрешитель ОС (getaddrinfo, <c>getent ahostsv4</c>), как у любой программы: свой resolv.conf с
    /// DNS <paramref name="nameserver"/> (роутер сети) — через <c>ip netns exec</c>, в своем пространстве монтирования.
    /// </summary>
    public async Task<string> ResolveWithSystemAsync(string name, IPAddress nameserver)
    {
        var etc = "/etc/netns/" + Name;
        Directory.CreateDirectory(etc);
        try
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(etc, "resolv.conf"), $"nameserver {nameserver}\noptions timeout:5 attempts:1\n", Ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(etc, "nsswitch.conf"), "hosts: files dns\n", Ct);
            return Execute(null, "ip", ["netns", "exec", Name, "getent", "ahostsv4", name]);
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    /// <summary>Команда изнутри пространства; не вышла успешно — исключение с ее выводом.</summary>
    public async Task<string> RunAsync(params string[] command)
    {
        var start = new ProcessStartInfo("nsenter") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--net=" + Path);
        foreach (var argument in command)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return process.ExitCode == 0 ? await output : throw new InvalidOperationException(command[0] + ": " + error);
    }

    /// <summary>Страница по HTTP изнутри пространства; не ответило за <paramref name="seconds"/> с — исключение.</summary>
    public Task<string> CurlAsync(Uri url, int seconds = 10) =>
        RunAsync("curl", "-sS", "-g", "-m", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), url.ToString());

    /// <summary>Запрос A по UDP к <paramref name="server"/> изнутри пространства: адрес из ответа или «none».</summary>
    /// <summary>Запрос A к DNS <paramref name="server"/> по UDP или по TCP; ответ — адрес или «none».</summary>
    public Task<string> ResolveAsync(string name, IPAddress server, int seconds = 10, bool tcp = false, string? device = null) => RunAsync("python3", "-c", """
        import socket, struct, sys
        name, server, seconds, tcp, device = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4] == "tcp", sys.argv[5]
        query = struct.pack(">HHHHHH", 0x5047, 0x0100, 1, 0, 0, 0)
        query += b"".join(bytes([len(p)]) + p.encode() for p in name.split(".")) + b"\0" + struct.pack(">HH", 1, 1)
        if tcp:
            s = socket.create_connection((server, 53), timeout=seconds)
            s.sendall(struct.pack(">H", len(query)) + query)
            data = b""
            while len(data) < 2 or len(data) < 2 + struct.unpack(">H", data[:2])[0]:
                chunk = s.recv(4096)
                if not chunk:
                    raise SystemExit("соединение закрыто без ответа")
                data += chunk
            reply = data[2:2 + struct.unpack(">H", data[:2])[0]]
        else:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            if device:
                s.setsockopt(socket.SOL_SOCKET, 25, device.encode())
            s.settimeout(seconds)
            s.sendto(query, (server, 53))
            reply = s.recv(512)
        print(socket.inet_ntoa(reply[-4:]) if struct.unpack(">H", reply[6:8])[0] else "none", end="")
        """, name, server.ToString(), seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), tcp ? "tcp" : "udp", device ?? string.Empty);

    public void Dispose()
    {
        foreach (var link in _links)
        {
            try
            {
                Host("ip", "link", "del", link);
            }
            catch (InvalidOperationException)
            {
                // Пары уже нет.
            }
        }

        Host("umount", Path);
        File.Delete(Path);
        lock (Taken)
        {
            Taken.Remove(_octet);
        }
    }

    private static string Execute(string? input, string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(input ?? string.Empty);
        process.StandardInput.Close();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.GetAwaiter().GetResult()
            : throw new InvalidOperationException($"{program} {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
    }
}

/// <summary>
/// Тесты со своими сетевыми пространствами и настоящими ядрами — по одному: параллельные стенды делят ядро ОС
/// (veth машины, процессы sing-box), и первое соединение через туннель сразу после «Подключено» у соседнего
/// стенда сбрасывалось. С другими тестами они идут параллельно.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NamespaceSerial
{
    public const string Name = "Сетевые пространства";
}
