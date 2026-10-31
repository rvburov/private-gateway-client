using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Пинг и скорость на главном экране. Служба их не заполняла, и окно всегда показывало «—» и «0,0» (у пользователя на
/// Windows 2026-10-30). Пока «Защищено»: раз в <see cref="TrafficEvery"/> — скорость по счетчикам адаптера своего туннеля
/// (в режиме VPN через него идет весь трафик машины; в режиме прокси общего счетчика нет — скорость не измеряется), раз в
/// <see cref="PingEvery"/> — задержка запроса через ядро и сервер к проверочному адресу по уже открытому соединению: новое
/// соединение — это рукопожатия с сервером и TLS до проверочного адреса, и пинг выходил втрое больше, чем у других
/// клиентов к тому же серверу (480 мс против 124 у пользователя 2026-10-31). Соединение живет, пока живет вход проверки.
/// </summary>
public sealed partial class Gateway
{
    /// <summary>Как часто обновляется скорость.</summary>
    public static readonly TimeSpan TrafficEvery = TimeSpan.FromSeconds(2);

    /// <summary>Как часто замеряется пинг: запрос через сервер — немного трафика, раз в несколько секунд не нужен.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    private ITimer? _trafficTimer;
    private HttpClient? _pingClient;
    private ProbeLink? _pingLink;
    private (long Received, long Sent, long At)? _lastCounters;
    private long _lastPing;
    private int _sampling;

    /// <summary>Замер по таймеру; <see cref="GatewayOptions.TrafficWatch"/> — свой шаг (тесты: бесконечный — только вручную).</summary>
    private void StartTrafficSampling(TimeProvider time)
    {
        var every = _options.TrafficWatch ?? TrafficEvery;
        _trafficTimer = time.CreateTimer(_ => _ = SampleTrafficAsync(), null, every, every);
    }

    /// <summary>Один замер; одновременно — не больше одного: медленный пинг не копит очередь.</summary>
    public async Task SampleTrafficAsync()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            if (Status is not { State: ConnectionState.Connected, IsPaused: false } status)
            {
                _lastCounters = null;
                _lastPing = 0;
                ForgetPingClient();
                if (Status.Traffic != TrafficStats.Empty)
                {
                    Update(s => s with { Traffic = TrafficStats.Empty });
                }

                return;
            }

            var (down, up) = status.Mode == ConnectionMode.Vpn ? Speed() : (0, 0);
            var ping = status.Traffic.LatencyMs;
            var now = _time.GetTimestamp();
            if ((_lastPing == 0 || _time.GetElapsedTime(_lastPing, now) >= PingEvery) && _probe is { } link)
            {
                _lastPing = now;
                ping = _options.Delay is { } delay
                    ? await delay(new ServerProbe(link.Port, new NetworkCredential(link.User, link.Password), _options.LatencyTarget, Timing.ProbeTimeout),
                        CancellationToken.None).ConfigureAwait(false)
                    : await WarmDelayAsync(link).ConfigureAwait(false);
            }

            // Те же числа подписчикам не шлются: окно и так их показывает.
            var traffic = new TrafficStats(ping, down, up);
            var session = status.Since;
            if (traffic != Status.Traffic)
            {
                Update(s => s is { State: ConnectionState.Connected } && s.Since == session ? s with { Traffic = traffic } : s);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or NetworkInformationException or OperationCanceledException or ObjectDisposedException)
        {
            // Замер — подсказка на экране, а не защита: не вышло — в следующий раз.
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    /// <summary>
    /// Пинг по открытому соединению через вход проверки: в первый раз — прогрев (соединение с сервером и TLS до проверочного
    /// адреса), замер — следующий запрос. Нет ответа за время пробы — null, соединение откроется заново.
    /// </summary>
    private async Task<int?> WarmDelayAsync(ProbeLink link)
    {
        var warm = _pingClient is not null && _pingLink == link;
        if (!warm)
        {
            ForgetPingClient();
            _pingLink = link;
            _pingClient = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new LatencyProbe.FixedProxy(new Uri($"socks5://127.0.0.1:{link.Port}")) { Credentials = new NetworkCredential(link.User, link.Password) },
                UseProxy = true,
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionIdleTimeout = PingEvery * 4,
            })
            { Timeout = Timeout.InfiniteTimeSpan };
        }

        try
        {
            if (!warm)
            {
                await SendPingAsync(_pingClient!).ConfigureAwait(false);
            }

            var started = _time.GetTimestamp();
            await SendPingAsync(_pingClient!).ConfigureAwait(false);
            return (int)Math.Max(1, Math.Round(_time.GetElapsedTime(started).TotalMilliseconds));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            ForgetPingClient();
            return null;
        }
    }

    private async Task SendPingAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(Timing.ProbeTimeout, _time);
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.LatencyTarget);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }

    private void ForgetPingClient()
    {
        _pingClient?.Dispose();
        (_pingClient, _pingLink) = (null, null);
    }

    /// <summary>Мбит/с с прошлого замера по счетчикам адаптера туннеля; первого замера или адаптера нет — 0.</summary>
    private (double Down, double Up) Speed()
    {
        if ((_options.TunnelCounters ?? TunnelCounters)() is not { } counters)
        {
            _lastCounters = null;
            return (0, 0);
        }

        var now = _time.GetTimestamp();
        var last = _lastCounters;
        _lastCounters = (counters.Received, counters.Sent, now);
        if (last is not { } before || _time.GetElapsedTime(before.At, now) is not { TotalSeconds: > 0 } elapsed
            || counters.Received < before.Received || counters.Sent < before.Sent)
        {
            return (0, 0);
        }

        // Принятое адаптером туннеля — то, что пришло с сервера (загрузка); отправленное в него — отдача.
        return (Mbps(counters.Received - before.Received, elapsed), Mbps(counters.Sent - before.Sent, elapsed));
    }

    private static double Mbps(long bytes, TimeSpan elapsed) => Math.Round(bytes * 8 / elapsed.TotalSeconds / 1_000_000, 1);

    /// <summary>Счетчики адаптера своего туннеля в ОС; его нет (туннель в другом сетевом пространстве, ОС не дает) — null.</summary>
    private static (long Received, long Sent)? TunnelCounters()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.Name == SingBoxConfigBuilder.InterfaceName);
        if (adapter is null)
        {
            return null;
        }

        var statistics = adapter.GetIPStatistics();
        return (statistics.BytesReceived, statistics.BytesSent);
    }
}
