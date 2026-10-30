using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Пинг и скорость на главном экране. Служба их не заполняла, и окно всегда показывало «—» и «0,0» (у пользователя на
/// Windows 2026-10-30). Пока «Защищено»: раз в <see cref="TrafficEvery"/> — скорость по счетчикам адаптера своего туннеля
/// (в режиме VPN через него идет весь трафик машины; в режиме прокси общего счетчика нет — скорость не измеряется), раз в
/// <see cref="PingEvery"/> — задержка запроса через ядро и сервер к проверочному адресу.
/// </summary>
public sealed partial class Gateway
{
    /// <summary>Как часто обновляется скорость.</summary>
    public static readonly TimeSpan TrafficEvery = TimeSpan.FromSeconds(2);

    /// <summary>Как часто замеряется пинг: запрос через сервер — немного трафика, раз в несколько секунд не нужен.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    private ITimer? _trafficTimer;
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
                var probe = new ServerProbe(link.Port, new NetworkCredential(link.User, link.Password), _options.LatencyTarget, Timing.ProbeTimeout);
                ping = await (_options.Delay ?? LatencyProbe.DelayAsync)(probe, CancellationToken.None).ConfigureAwait(false);
            }

            // Те же числа подписчикам не шлются: окно и так их показывает.
            var traffic = new TrafficStats(ping, down, up);
            var session = status.Since;
            if (traffic != Status.Traffic)
            {
                Update(s => s is { State: ConnectionState.Connected } && s.Since == session ? s with { Traffic = traffic } : s);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or NetworkInformationException or OperationCanceledException)
        {
            // Замер — подсказка на экране, а не защита: не вышло — в следующий раз.
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
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
