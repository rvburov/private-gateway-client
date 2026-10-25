using System.Diagnostics;
using System.Net;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Свое сетевое пространство Linux (unshare): в нем только то, что поднимет тест, — TUN, правила
/// nftables, маршруты. Сеть машины тесты так не трогают. Нужны root, unshare и nsenter.
/// </summary>
internal sealed class NetworkNamespace : IDisposable
{
    private static readonly string[] Tools = ["unshare", "nsenter"];

    private NetworkNamespace(string path) => Path = path;

    /// <summary>Файл пространства в /run/netns.</summary>
    public string Path { get; }

    /// <summary>Имя для <c>ip … netns</c>.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var octet = Random.Shared.Next(1, 250);
        var host = IPAddress.Parse($"10.252.{octet}.1");
        var neighbor = IPAddress.Parse($"10.252.{octet}.5");
        var @public = IPAddress.Parse($"203.0.113.{octet}");
        var outside = Link("pgnet0", host, IPAddress.Parse($"10.252.{octet}.2"));
        Host("ip", "addr", "add", neighbor + "/32", "dev", outside);
        Host("ip", "addr", "add", @public + "/32", "dev", outside);
        Run(null, "ip", "route", "add", "default", "dev", "pgnet0");
        return (host, neighbor, @public);
    }

    /// <summary>Команда изнутри пространства с <paramref name="input"/> в stdin; не вышла успешно — исключение с ее выводом.</summary>
    public string Run(string? input, params string[] command) => Execute(input, "nsenter", ["--net=" + Path, .. command]);

    /// <summary>
    /// Страница с сайта теста из python3, а не curl (другая программа для правил по процессу); с меткой
    /// <paramref name="mark"/> (SO_MARK) — как пакеты выхода sing-box мимо туннеля, 0 — без метки. Не
    /// соединилось — исключение.
    /// </summary>
    public Task<string> FetchAsync(Uri url, uint mark = 0, int seconds = 10) => RunAsync("python3", "-c", """
        import socket, sys
        s = socket.socket()
        if int(sys.argv[3]):
            s.setsockopt(socket.SOL_SOCKET, 36, int(sys.argv[3]))
        s.settimeout(float(sys.argv[4]))
        s.connect((sys.argv[1], int(sys.argv[2])))
        s.sendall(b"GET / HTTP/1.0\r\n\r\n")
        reply = b""
        while chunk := s.recv(4096):
            reply += chunk
        print(reply.decode().split("\r\n\r\n", 1)[1], end="")
        """, url.Host, url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), mark.ToString(System.Globalization.CultureInfo.InvariantCulture),
        seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
    public Task<string> CurlAsync(Uri url, int seconds = 10) => RunAsync("curl", "-sS", "-m", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), url.ToString());

    /// <summary>Запрос A по UDP к <paramref name="server"/> изнутри пространства: адрес из ответа или «none».</summary>
    public Task<string> ResolveAsync(string name, IPAddress server, int seconds = 10) => RunAsync("python3", "-c", """
        import socket, struct, sys
        name, server, seconds = sys.argv[1], sys.argv[2], int(sys.argv[3])
        query = struct.pack(">HHHHHH", 0x5047, 0x0100, 1, 0, 0, 0)
        query += b"".join(bytes([len(p)]) + p.encode() for p in name.split(".")) + b"\0" + struct.pack(">HH", 1, 1)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(seconds)
        s.sendto(query, (server, 53))
        reply = s.recv(512)
        print(socket.inet_ntoa(reply[-4:]) if struct.unpack(">H", reply[6:8])[0] else "none", end="")
        """, name, server.ToString(), seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        Host("umount", Path);
        File.Delete(Path);
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
