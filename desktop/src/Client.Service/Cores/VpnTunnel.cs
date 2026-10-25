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

    private VpnTunnel(CoreProcess xray, CoreProcess singBox, string? netns, Action<string, string>? output)
    {
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
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<VpnTunnel> StartAsync(string xray, string singBox, CoreRequest request, IReadOnlyList<IPAddress> serverAddresses,
        TimeSpan timeout, TimeProvider time, string? netns = null, Action<string, string>? output = null, ProbeLink? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serverAddresses);
        if (serverAddresses.Count == 0)
        {
            throw new TunnelException("Нет адреса сервера");
        }

        var link = NewLink(serverAddresses[0]);
        var built = XrayConfigBuilder.Build(request, link, probe);
        if (built.Config is not { } xrayConfig)
        {
            throw new TunnelException("Ядро Xray не возьмется за сервер: " + built.Rejection);
        }

        var started = time.GetTimestamp();
        var xrayCore = CoreProcess.StartXray(xray, xrayConfig, netns);
        xrayCore.Output += (_, line) => output?.Invoke("xray", line);
        CoreProcess? singBoxCore = null;
        try
        {
            // Вход Xray в своем сетевом пространстве отсюда не виден — Xray жив секунду.
            await WaitAsync(() => !xrayCore.HasExited && (netns is null ? Listens(link.Port) : time.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)),
                "Xray", xrayCore, timeout - time.GetElapsedTime(started), time, cancellationToken).ConfigureAwait(false);
            singBoxCore = CoreProcess.StartSingBox(singBox,
                SingBoxConfigBuilder.BuildTun(new TunRequest(link, serverAddresses, request.Protection, request.Connection, DirectPrograms(request, xray))),
                netns);
            singBoxCore.Output += (_, line) => output?.Invoke("sing-box", line);
            var core = singBoxCore;
            var alive = time.GetTimestamp();

            // TUN поднят — адаптер виден в системе; в своем сетевом пространстве его отсюда не видно — sing-box жив секунду.
            await WaitAsync(() => netns is null && SingBoxConfigBuilder.InterfaceName is { Length: > 0 } name
                    ? HasInterface(name)
                    : time.GetElapsedTime(alive) >= TimeSpan.FromSeconds(1),
                "sing-box", core, timeout - time.GetElapsedTime(started), time, cancellationToken).ConfigureAwait(false);
            return new VpnTunnel(xrayCore, core, netns, output);
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
    /// Связь Xray с TUN на одно подключение: порт на 127.0.0.1, свободный для TCP и UDP, имя и пароль — по 128
    /// случайных бит; Xray соединяется с <paramref name="server"/>.
    /// </summary>
    public static TunLink NewLink(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new TunLink(LocalPort.FreeForTcpAndUdp(), Secret(), Secret(), server.ToString());
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

    private static bool HasInterface(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == name && n.OperationalStatus != OperationalStatus.Down);

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
