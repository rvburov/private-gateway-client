using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Почему туннель не поднялся.</summary>
public sealed class TunnelException(string message) : Exception(message);

/// <summary>
/// Туннель режима VPN: Xray со входом только для своего TUN (порт и пароль — на это подключение) и
/// sing-box с TUN. Запуск — Xray, потом TUN; остановка — наоборот: сначала TUN (sing-box убирает свои
/// маршруты), потом Xray. Ядро вышло само — <see cref="Exited"/>.
/// </summary>
public sealed class VpnTunnel : IAsyncDisposable
{
    /// <summary>Сколько sing-box дается на то, чтобы убрать маршруты при остановке.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly CoreProcess _xray;
    private readonly CoreProcess _singBox;
    private int _stopping;

    private VpnTunnel(CoreProcess xray, CoreProcess singBox)
    {
        _xray = xray;
        _singBox = singBox;
        xray.Exited += (_, _) => OnExited();
        singBox.Exited += (_, _) => OnExited();
    }

    /// <summary>Одно из ядер вышло само.</summary>
    public event EventHandler? Exited;

    /// <summary>Одно из ядер уже не работает — и до подписки на <see cref="Exited"/>.</summary>
    public bool HasExited => _xray.HasExited || _singBox.HasExited;

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
    /// <param name="netns">Создать TUN в сетевом пространстве Linux (тесты); null — в системе.</param>
    /// <param name="output">Строка вывода ядра — источник («xray», «sing-box») и текст, с самого запуска; приходит из потока чтения.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<VpnTunnel> StartAsync(string xray, string singBox, CoreRequest request, IReadOnlyList<IPAddress> serverAddresses,
        TimeSpan timeout, TimeProvider time, string? netns = null, Action<string, string>? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serverAddresses);
        if (serverAddresses.Count == 0)
        {
            throw new TunnelException("Нет адреса сервера");
        }

        var link = NewLink(serverAddresses[0]);
        var built = XrayConfigBuilder.Build(request, link);
        if (built.Config is not { } xrayConfig)
        {
            throw new TunnelException("Ядро Xray не возьмется за сервер: " + built.Rejection);
        }

        var started = time.GetTimestamp();
        var xrayCore = CoreProcess.StartXray(xray, xrayConfig);
        xrayCore.Output += (_, line) => output?.Invoke("xray", line);
        CoreProcess? singBoxCore = null;
        try
        {
            await WaitAsync(() => !xrayCore.HasExited && Listens(link.Port), "Xray", xrayCore, timeout - time.GetElapsedTime(started), time,
                cancellationToken).ConfigureAwait(false);
            singBoxCore = CoreProcess.StartSingBox(singBox,
                SingBoxConfigBuilder.BuildTun(new TunRequest(link, serverAddresses, request.Protection, request.Connection, netns)));
            singBoxCore.Output += (_, line) => output?.Invoke("sing-box", line);
            var core = singBoxCore;
            var alive = time.GetTimestamp();

            // TUN поднят — адаптер виден в системе; в своем сетевом пространстве его отсюда не видно — sing-box жив секунду.
            await WaitAsync(() => netns is null && SingBoxConfigBuilder.InterfaceName is { Length: > 0 } name
                    ? HasInterface(name)
                    : time.GetElapsedTime(alive) >= TimeSpan.FromSeconds(1),
                "sing-box", core, timeout - time.GetElapsedTime(started), time, cancellationToken).ConfigureAwait(false);
            return new VpnTunnel(xrayCore, core);
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

    /// <summary>
    /// Связь Xray с TUN на одно подключение: порт на 127.0.0.1, свободный для TCP и UDP, имя и пароль — по 128
    /// случайных бит; Xray соединяется с <paramref name="server"/>.
    /// </summary>
    public static TunLink NewLink(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new TunLink(LocalPort.FreeForTcpAndUdp(), Secret(), Secret(), server.ToString());
    }

    /// <summary>Остановить: сначала TUN, потом Xray.</summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _singBox.StopAsync(StopGrace).ConfigureAwait(false);
        await _xray.StopAsync().ConfigureAwait(false);
        _singBox.Dispose();
        _xray.Dispose();
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
