using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Проба связи с сервером через вход работающего ядра: SOCKS5 на 127.0.0.1:<paramref name="Port"/>
/// (в режиме VPN — с паролем туннеля), запрос к <paramref name="Target"/> не дольше <paramref name="Timeout"/>.
/// </summary>
public sealed record ServerProbe(int Port, NetworkCredential? Credentials, Uri Target, TimeSpan Timeout);

/// <summary>
/// Задержка серверов через настоящее ядро, а не пинг адреса: одно ядро на все серверы
/// (<see cref="XrayConfigBuilder.BuildProbe"/>), через вход каждого — запрос HTTP к проверочному
/// адресу. Задержка — время запроса вместе с установкой соединения через сервер: так ее почувствует
/// пользователь. Ответил — сервер доступен; ответ проверочного адреса, а не его код, и есть проверка.
/// </summary>
public static class LatencyProbe
{
    /// <summary>Проверочный адрес по умолчанию: пустой ответ 204, без cookies и без Google.</summary>
    public static readonly Uri DefaultTarget = new("https://cp.cloudflare.com/generate_204");

    /// <summary>Сколько серверов проверяется одновременно.</summary>
    public const int MaxParallel = 8;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    public static async Task<IReadOnlyList<LatencyResult>> MeasureAsync(
        string xray,
        IReadOnlyList<ServerDefinition> servers,
        ConnectionSettings connection,
        Uri target,
        TimeSpan timeout,
        TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(time);
        var ports = servers.Select(_ => FreePort()).ToList();
        var probe = XrayConfigBuilder.BuildProbe(servers, ports, connection);
        var results = probe.Rejections
            .Select(r => r is null ? null : LatencyResult.Failed(LatencyFailure.Unsupported))
            .ToArray();
        if (probe.Config is not { } config)
        {
            return Done(results);
        }

        var checkedIndexes = Enumerable.Range(0, servers.Count).Where(i => results[i] is null).ToList();
        using var core = CoreProcess.StartXray(xray, config);
        try
        {
            if (!await WaitForPortsAsync(core, [.. checkedIndexes.Select(i => ports[i])], time, cancellationToken).ConfigureAwait(false))
            {
                foreach (var i in checkedIndexes)
                {
                    results[i] = LatencyResult.Failed(LatencyFailure.CoreFailed);
                }

                return Done(results);
            }

            await Parallel.ForEachAsync(
                checkedIndexes,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = cancellationToken },
                async (i, token) => results[i] = await RequestAsync(ports[i], target, timeout, time, token).ConfigureAwait(false))
                .ConfigureAwait(false);
            return Done(results);
        }
        finally
        {
            await core.StopAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Отвечает ли сервер: любой ответ HTTP через ядро — да; нет ответа за время пробы — нет.</summary>
    public static async Task<bool> ReachesAsync(ServerProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return (await RequestAsync(probe.Port, probe.Target, probe.Timeout, TimeProvider.System, cancellationToken, probe.Credentials)
            .ConfigureAwait(false)).Milliseconds is not null;
    }

    private static async Task<LatencyResult> RequestAsync(int port, Uri target, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken,
        NetworkCredential? credentials = null)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new FixedProxy(new Uri($"socks5://127.0.0.1:{port}")) { Credentials = credentials },
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        var started = time.GetTimestamp();
        try
        {
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return LatencyResult.Of(time.GetElapsedTime(started));
        }
        catch (HttpRequestException)
        {
            return LatencyResult.Failed(LatencyFailure.NoResponse);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LatencyResult.Failed(LatencyFailure.NoResponse);
        }
    }

    private static LatencyResult[] Done(LatencyResult?[] results) => [.. results.Select(r => r!)];

    /// <summary>Ждать, пока ядро откроет все входы; false — ядро вышло или не успело.</summary>
    private static async Task<bool> WaitForPortsAsync(CoreProcess core, IReadOnlyList<int> ports, TimeProvider time, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var pending = new Queue<int>(ports);
        while (pending.TryPeek(out var port))
        {
            if (core.HasExited || time.GetElapsedTime(started) > StartTimeout)
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                pending.Dequeue();
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Прокси для всех адресов: WebProxy пропускает localhost мимо прокси, а проверке нужен только вход ядра.</summary>
    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
