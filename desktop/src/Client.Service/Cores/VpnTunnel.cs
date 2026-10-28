using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Platform.Linux;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Почему туннель не поднялся.</summary>
public sealed class TunnelException(string message) : Exception(message);

/// <summary>
/// Туннель режима VPN: Xray со входом только для своего TUN (порт и пароль — на это подключение) и
/// sing-box с TUN. Запуск — Xray, потом TUN; остановка — наоборот: сначала TUN (sing-box убирает свои
/// маршруты), потом Xray. Ядро вышло само — <see cref="Exited"/>. Правила маршрутизации, которые sing-box
/// не убрал (его сняли или он упал), убирает служба (<see cref="RemoveLeftoverRules"/>).
/// </summary>
public sealed class VpnTunnel : IAsyncDisposable
{

    /// <summary>Сколько sing-box дается на то, чтобы убрать маршруты при остановке.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly CoreProcess _xray;
    private readonly CoreProcess _singBox;
    private readonly string? _netns;
    private readonly Action<string, string>? _output;
    private int _stopping;

    private VpnTunnel(CoreProcess xray, CoreProcess singBox, string? netns, Action<string, string>? output, TunRequest tun)
    {
        Ipv6 = SingBoxConfigBuilder.Ipv6Tunneled(tun);
        Subnet = tun.Subnet ?? TunSubnet.Default;
        Ipv6Outside = SingBoxConfigBuilder.Ipv6Outside(tun) && MachineIpv6(netns);
        _xray = xray;
        _singBox = singBox;
        _netns = netns;
        _output = output;
        xray.Exited += (_, _) => OnExited();
        singBox.Exited += (_, _) => OnExited();
    }

    /// <summary>Одно из ядер вышло само.</summary>
    public event EventHandler? Exited;


    /// <summary>Одно из ядер уже не работает — и до подписки на <see cref="Exited"/>.</summary>
    public bool HasExited => _xray.HasExited || _singBox.HasExited;

    /// <summary>Номера процессов ядер — для журнала.</summary>
    public (int Xray, int SingBox) Processes => (_xray.Id, _singBox.Id);

    /// <summary>Номер процесса ретранслятора sing-box для Hysteria2 и TUIC (<see cref="CoreRelay"/>) — для журнала; null — его нет.</summary>
    public int? Relay => _xray.CompanionId;

    /// <summary>IPv6 идет через туннель: у адаптера есть адрес IPv6 и сервер открывает IPv6.</summary>
    public bool Ipv6 { get; }

    /// <summary>Адреса TUN этого туннеля: адрес адаптера и DNS туннеля (его служба дает DNS ОС и правилам блока).</summary>
    public TunSubnet Subnet { get; }

    /// <summary>
    /// IPv6 программ идет мимо туннеля: сервер его не открывает, а «Блокировать IPv6 вне туннеля» и «Строгие маршруты»
    /// выключены. Держит его только блок при обрыве.
    /// </summary>
    public bool Ipv6Outside { get; }

    /// <summary>
    /// У машины есть IPv6 наружу: адрес IPv6 вне частных сетей на работающем адаптере (не петле и не туннеле). Нет — IPv6
    /// мимо туннеля уйти некуда, хоть он и не закрыт. <paramref name="netns"/> — сеть пространства Linux (тесты): ее
    /// адаптеры отсюда не видны, спрашивается ip внутри него. Узнать не вышло — считается, что есть.
    /// </summary>
    public static bool MachineIpv6(string? netns = null)
    {
        if (netns is not null)
        {
            try
            {
                return IpCommand.ForThisMachine(netns) is not { } ip || ip(["-o", "-6", "addr", "show", "scope", "global"])
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Select(words => Array.IndexOf(words, "inet6") is var at and >= 0 && at + 1 < words.Length ? words[at + 1].Split('/')[0] : null)
                    .Any(address => IPAddress.TryParse(address, out var found) && !found.IsIPv6UniqueLocal);
            }
            catch (IOException)
            {
                return true;
            }
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6UniqueLocal
                    && !IPAddress.IsLoopback(a.Address));
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Сети IPv4 адаптеров машины, кроме своего TUN, — адрес и длина префикса: из них <see cref="TunSubnet.Choose"/> узнает,
    /// какие подсети TUN заняты. Свой TUN не в счет: его адрес уходит вместе с ним, а подсеть между переподключениями не скачет.
    /// <paramref name="netns"/> — сеть пространства Linux (тесты): спрашивается ip внутри него. Узнать не вышло — пусто.
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, int Prefix)> MachineNetworks(string? netns = null)
    {
        if (netns is not null)
        {
            try
            {
                return IpCommand.ForThisMachine(netns) is not { } ip ? [] : [.. ip(["-o", "-4", "addr", "show"])
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(words => words.Length > 3 && words[1] != SingBoxConfigBuilder.InterfaceName && words[2] == "inet")
                    // Адрес «точка-точка» («inet A peer B/P») — без длины: занят сам адрес.
                    .Select(words => words[3].Split('/'))
                    .Where(parts => parts.Length <= 2 && IPAddress.TryParse(parts[0], out _) && (parts.Length == 1 || int.TryParse(parts[1], out _)))
                    .Select(parts => (IPAddress.Parse(parts[0]), parts.Length == 1 ? 32 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)))];
            }
            catch (IOException)
            {
                return [];
            }
        }

        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name != SingBoxConfigBuilder.InterfaceName)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (a.Address, a.PrefixLength))];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>В ядре ОС есть IPv6: правила маршрутизации IPv6 ставятся (строгим маршрутам на Linux это нужно).</summary>
    public static bool KernelIpv6() => OperatingSystem.IsLinux() ? Directory.Exists("/proc/sys/net/ipv6") : Socket.OSSupportsIPv6;

    /// <summary>
    /// ОС даст новому адаптеру адрес IPv6: на Linux IPv6 не выключен для новых адаптеров, на Windows и macOS — IPv6 есть в
    /// ОС. Не дала (Windows с выключенным IPv6) — sing-box выйдет при запуске, служба поднимет туннель без IPv6.
    /// </summary>
    public static bool AdapterIpv6()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Socket.OSSupportsIPv6;
        }

        try
        {
            return File.ReadAllText("/proc/sys/net/ipv6/conf/default/disable_ipv6").Trim() == "0";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Поднять туннель. <paramref name="serverAddresses"/> — адреса сервера, разрешенные заранее: к первому
    /// соединяется Xray, все идут мимо TUN. Не поднялось за <paramref name="timeout"/> — <see cref="TunnelException"/>.
    /// </summary>
    /// <param name="xray">Ядро Xray.</param>
    /// <param name="singBox">sing-box.</param>
    /// <param name="request">Сервер и настройки.</param>
    /// <param name="serverAddresses">Адреса сервера.</param>
    /// <param name="timeout">Сколько ждать подъема.</param>
    /// <param name="time">Часы.</param>
    /// <param name="netns">Запустить оба ядра в сетевом пространстве Linux (тесты) — TUN и маршруты там же; null — в системе.</param>
    /// <param name="output">Строка вывода ядра — источник («xray», «sing-box») и текст, с самого запуска; приходит из потока чтения.</param>
    /// <param name="probe">Вход проверки связи с сервером; null — без него.</param>
    /// <param name="serverIpv6">
    /// Открывает ли сервер IPv6 — запросом через вход проверки, после запуска Xray и до TUN; null — не спрашивать: IPv6 через
    /// туннель не идет.
    /// </param>
    /// <param name="routes">Только эти сети через TUN (<see cref="TunRequest.Routes"/>, тесты Windows в CI); null — весь трафик.</param>
    /// <param name="subnet">
    /// Адреса TUN (тесты); null — первая подсеть, не занятая адаптерами машины (<see cref="MachineNetworks"/>,
    /// <see cref="TunSubnet.Choose"/>).
    /// </param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<VpnTunnel> StartAsync(string xray, string singBox, CoreRequest request, IReadOnlyList<IPAddress> serverAddresses,
        TimeSpan timeout, TimeProvider time, string? netns = null, Action<string, string>? output = null, ProbeLink? probe = null,
        Func<ProbeLink, CancellationToken, Task<bool>>? serverIpv6 = null, IReadOnlyList<string>? routes = null, TunSubnet? subnet = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serverAddresses);
        if (serverAddresses.Count == 0)
        {
            throw new TunnelException("Нет адреса сервера");
        }

        var link = NewLink(serverAddresses[0], bypass: XrayConfigBuilder.NeedsBypassInbound(request));

        // Hysteria2 и TUIC — через ретранслятор sing-box (CoreRelay): к серверу он соединяется по тому же адресу, что выведен из
        // маршрутов TUN и открыт блоком.
        var relay = CoreRelay.LinkFor(request.Server, singBox);
        var built = XrayConfigBuilder.Build(request, link, probe, relay);
        if (built.Config is not { } xrayConfig)
        {
            throw new TunnelException("Ядро Xray не возьмется за сервер: " + built.Rejection);
        }

        var started = time.GetTimestamp();
        var xrayStarted = 0;
        var xrayCore = CoreRelay.StartXray(xray, xrayConfig, singBox,
            relay is null ? [] : [new RelayRoute(request.Server, relay, serverAddresses[0].ToString())], request.Connection.CoreLog, out var relayReady, netns,
            line =>
            {
                if (XrayStarted(line))
                {
                    Volatile.Write(ref xrayStarted, 1);
                }

                if (!LinkNotice(line))
                {
                    output?.Invoke("xray", line);
                }
            },
            line => output?.Invoke("sing-box", line));
        CoreProcess? singBoxCore = null;
        try
        {
            // Вход Xray в своем сетевом пространстве отсюда не виден — тогда по строке «Xray … started»: ее ядро
            // печатает, когда входы уже слушают. Ретранслятор — тоже по строке готовности.
            await WaitAsync(() => !xrayCore.HasExited && (netns is null ? Listens(link.Port) : Volatile.Read(ref xrayStarted) == 1) && relayReady(),
                "Xray", xrayCore, timeout - time.GetElapsedTime(started), time, cancellationToken).ConfigureAwait(false);

            // IPv6 через туннель — только если сервер его открывает: иначе соединения IPv6 принимал бы TUN, а рвал сервер, и
            // программа не перешла бы на IPv4. С сервером Xray соединяется по IPv6 — у него IPv6 есть, проверять нечего: без
            // IPv6 у TUN строгие маршруты (настройка пользователя) пришлось бы снять, иначе они отрезали бы Xray.
            var serverOverIpv6 = serverAddresses[0].AddressFamily == AddressFamily.InterNetworkV6;
            var ipv6 = request.Protection.Ipv6Allowed && AdapterIpv6()
                && (serverOverIpv6 || (probe is not null && serverIpv6 is not null
                    && await AskAsync(serverIpv6, probe, (timeout - time.GetElapsedTime(started)) / 2, time, output, cancellationToken).ConfigureAwait(false)));

            // Адреса TUN — в подсети, которую не держит ни один адаптер: 172.19.0.1 бывает занят (Hyper-V, WSL, Docker, TUN
            // других клиентов), и Windows второй раз его не ставит — sing-box выходит при запуске.
            if (subnet is null)
            {
                subnet = TunSubnet.Choose(MachineNetworks(netns));
                if (!subnet.Equals(TunSubnet.Default))
                {
                    output?.Invoke("service", $"Адрес TUN {TunSubnet.Default.Cidr} занят другим адаптером — туннель на {subnet.Cidr}");
                }
            }

            while (true)
            {
                var ready = 0;
                var tun = new TunRequest(link, serverAddresses, request.Protection, request.Connection, new TunIpv6(KernelIpv6(), ipv6),
                    DirectPrograms(request, xray), FakeIpPrograms(request), routes, subnet);
                singBoxCore = CoreProcess.StartSingBox(singBox, SingBoxConfigBuilder.BuildTun(tun), netns,
                    line =>
                    {
                        if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
                        {
                            Volatile.Write(ref ready, 1);
                        }

                        if (SingBoxConfigBuilder.ShownLine(line, request.Connection.CoreLog))
                        {
                            output?.Invoke("sing-box", line);
                        }
                    });

                // Готов, когда запущены все части sing-box: правила маршрутизации TUN появляются раньше, чем выходы и
                // сетевой менеджер, и соединение в эту щель сбрасывается.
                var core = singBoxCore;
                try
                {
                    await WaitAsync(() => Volatile.Read(ref ready) == 1, "sing-box", core, timeout - time.GetElapsedTime(started), time, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TunnelException) when (ipv6 && core.HasExited)
                {
                    // ОС не дала адаптеру адрес IPv6 (Windows с выключенным IPv6): туннель — без IPv6, он закрыт.
                    core.Dispose();
                    singBoxCore = null;
                    ipv6 = false;
                    output?.Invoke("service", "sing-box не поднял адаптер с IPv6 — на это подключение IPv6 закрыт");
                    continue;
                }

                return new VpnTunnel(xrayCore, core, netns, output, tun);
            }
        }
        catch
        {
            if (singBoxCore is not null)
            {
                await singBoxCore.StopAsync(StopGrace, CancellationToken.None).ConfigureAwait(false);
                singBoxCore.Dispose();
            }

            await xrayCore.StopAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            xrayCore.Dispose();
            throw;
        }
    }

    /// <summary>Вход проверки связи на одно подключение: свободный порт на 127.0.0.1, имя и пароль — по 128 случайных бит.</summary>
    public static ProbeLink NewProbe() => new(LocalPort.FreeForTcpAndUdp(), Secret(), Secret());

    /// <summary>
    /// Связь Xray с TUN на одно подключение: порт на 127.0.0.1, свободный для TCP и UDP, пароль Shadowsocks — 128
    /// случайных бит; Xray соединяется с <paramref name="server"/>. <paramref name="bypass"/> — еще порт входа
    /// обхода для FakeDNS (<see cref="XrayConfigBuilder.NeedsBypassInbound"/>).
    /// </summary>
    public static TunLink NewLink(IPAddress server, bool bypass = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new TunLink(LocalPort.FreeForTcpAndUdp(), Secret(), server.ToString(), bypass ? LocalPort.FreeForTcpAndUdp() : 0);
    }

    /// <summary>
    /// Программы мимо туннеля этого подключения (<see cref="SingBoxConfigBuilder.DirectPrograms"/>) — настоящими
    /// путями: sing-box сравнивает их с путем процесса, каким его видит ОС.
    /// </summary>
    public static IReadOnlyList<string> DirectPrograms(CoreRequest request, string xray)
    {
        ArgumentNullException.ThrowIfNull(request);
        return [.. SingBoxConfigBuilder.DirectPrograms(request.Routing, request.Protection, xray).Select(p => RealPath(p)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Программы мимо туннеля, чьи соединения с адресами FakeDNS ведет Xray (<see cref="SingBoxConfigBuilder.FakeIpPrograms"/>), — настоящими путями.</summary>
    public static IReadOnlyList<string> FakeIpPrograms(CoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return [.. SingBoxConfigBuilder.FakeIpPrograms(request.Routing, request.Protection).Select(p => RealPath(p)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Путь, как его видит ОС у процесса: полный и без символических ссылок ни в одной части (на Linux
    /// /bin бывает ссылкой на /usr/bin, а /usr/bin/firefox — на /usr/lib/firefox/firefox; на macOS
    /// /var — ссылка на /private/var). Цель ссылки разрешается заново целиком: в ней самой бывают ссылки.
    /// </summary>
    public static string RealPath(string path) => RealPath(path, depth: 0);

    private static string RealPath(string path, int depth)
    {
        var full = Path.GetFullPath(path);

        // Ссылки по кругу — путь как есть: такой программы все равно не запустить.
        if (OperatingSystem.IsWindows() || depth > 40)
        {
            return full;
        }

        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(resolved, part);
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (IOException)
            {
                // Части пути нет (программу удалили) — путь остается как есть.
                target = null;
            }

            // Цель ссылки — от папки ссылки, если она не полный путь.
            resolved = target is null ? next : RealPath(Path.Combine(resolved, target), depth + 1);
        }

        return resolved;
    }

    /// <summary>Остановить: сначала TUN, потом Xray; что sing-box не убрал за собой — убрать.</summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _singBox.StopAsync(StopGrace).ConfigureAwait(false);
        await _xray.StopAsync().ConfigureAwait(false);
        _singBox.Dispose();
        _xray.Dispose();
        if (RemoveLeftoverRules(_netns) is { Length: > 0 } removed)
        {
            _output?.Invoke("service", removed);
        }
    }

    /// <summary>
    /// Linux: правила маршрутизации туннеля (номера <see cref="SingBoxConfigBuilder.RuleIndex"/>…<see cref="SingBoxConfigBuilder.RuleLast"/>),
    /// которые остались, когда sing-box сняли или он упал. При обычной остановке он убирает их сам; оставшиеся
    /// после аварии держат IPv6 без связи при строгих маршрутах — и после отключения сеть не вернулась бы
    /// прямой. Вызывать, когда своего sing-box не работает. Возвращает запись для журнала; убирать нечего — пусто.
    /// </summary>
    /// <param name="netns">Сетевое пространство Linux (тесты); null — система.</param>
    public static string RemoveLeftoverRules(string? netns)
    {
        if (!OperatingSystem.IsLinux() || IpRules.ForThisMachine(netns) is not { } rules)
        {
            return string.Empty;
        }

        try
        {
            var removed = rules.Remove(SingBoxConfigBuilder.RuleIndex, SingBoxConfigBuilder.RuleLast);
            return removed > 0 ? $"Убраны правила маршрутизации, оставшиеся от sing-box: {removed}" : string.Empty;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return "Правила маршрутизации sing-box не проверены: " + e.Message;
        }
    }

    private void OnExited()
    {
        if (Volatile.Read(ref _stopping) == 0)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Строка Xray, после которой его входы слушают: «[Warning] core: Xray 25.8.3 started» — уровень warning, печатается
    /// при любом «Журнале ядра».
    /// </summary>
    public static bool XrayStarted(string line) =>
        line.Contains("core: Xray ", StringComparison.Ordinal) && line.EndsWith(" started", StringComparison.Ordinal);

    /// <summary>
    /// Предупреждение Xray-core 26.x о Shadowsocks, которое он печатает при каждом запуске из-за входа связи с TUN
    /// (<see cref="TunLink"/>): пользователь Shadowsocks не выбирал, и в журнале оно только сбивает с толку. Сервер Shadowsocks
    /// пользователя дает ту же строку — в режиме VPN она тоже не пишется: это совет Xray, а не ошибка.
    /// </summary>
    public static bool LinkNotice(string line) =>
        line.Contains("The feature Shadowsocks (with no Forward Secrecy, etc.) is deprecated", StringComparison.Ordinal);

    /// <summary>
    /// Открывает ли сервер IPv6 — не дольше <paramref name="budget"/> (половина оставшегося времени подключения: остальное —
    /// запуску sing-box). Не успел — на это подключение IPv6 закрыт.
    /// </summary>
    private static async Task<bool> AskAsync(Func<ProbeLink, CancellationToken, Task<bool>> serverIpv6, ProbeLink probe, TimeSpan budget,
        TimeProvider time, Action<string, string>? output, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(budget > TimeSpan.Zero ? budget : TimeSpan.Zero, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await serverIpv6(probe, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            output?.Invoke("service", "Проверка IPv6 сервера не уложилась во время подключения — на это подключение IPv6 закрыт");
            return false;
        }
    }

    private static async Task WaitAsync(Func<bool> ready, string name, CoreProcess core, TimeSpan left, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (!ready())
        {
            if (core.HasExited)
            {
                throw new TunnelException($"{name} вышел при запуске, код {core.ExitCode}");
            }

            if (time.GetElapsedTime(started) >= left)
            {
                throw new TunnelException($"{name} не поднялся за {left.TotalSeconds:0} с");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Listens(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
