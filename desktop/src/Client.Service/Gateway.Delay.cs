using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// «Задержка» на «Проверке» (решение 509): из чего складывается пинг. «До сервера» — прямое TCP-соединение с сервером (один
/// круг по сети до него); «Через сервер» — запрос к проверочному адресу через ядро и сервер по уже открытому соединению (его
/// показывает «Пинг» на главной); «Новое соединение» — тот же запрос по новому соединению: соединение с сервером, рукопожатие
/// TLS или REALITY, шифрование до сайта и сам запрос — так открывается каждый новый сайт. Каждое — медиана трех замеров.
/// Не измерено (нет подключения, сервер по UDP, не ответил) — null.
/// </summary>
public sealed partial class Gateway
{
    /// <summary>Сколько замеров на каждое число; на экране — медиана.</summary>
    public const int DelaySamples = 3;

    public async Task<DelayBreakdown> MeasureDelayAsync(CancellationToken cancellationToken = default)
    {
        var request = _request;
        var link = _probe;
        if (Status is not { State: ConnectionState.Connected, IsPaused: false } || request is null || link is null)
        {
            return new DelayBreakdown(null, null, null, _time.GetUtcNow());
        }

        var toServer = await ToServerAsync(request.Server, cancellationToken).ConfigureAwait(false);
        var through = await ThroughServerAsync(link, warm: true, cancellationToken).ConfigureAwait(false);
        var fresh = await ThroughServerAsync(link, warm: false, cancellationToken).ConfigureAwait(false);
        return new DelayBreakdown(toServer, through, fresh, _time.GetUtcNow());
    }

    /// <summary>Медиана: середина по порядку, у четного числа — среднее двух средних; замеров нет — null.</summary>
    public static int? Median(IReadOnlyCollection<int> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return null;
        }

        var sorted = samples.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (int)Math.Round((sorted[middle - 1] + sorted[middle]) / 2.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Прямое TCP-соединение с сервером — его адрес тот, к которому соединяется ядро; сервер по UDP — null.</summary>
    private async Task<int?> ToServerAsync(ServerDefinition server, CancellationToken cancellationToken)
    {
        if (server.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic or ProxyProtocol.WireGuard)
        {
            return null;
        }

        var known = _addresses;
        var address = known.Count > 0 ? known[0] : IPAddress.TryParse(server.Host.Trim('[', ']'), out var literal) ? literal : null;
        if (address is null)
        {
            try
            {
                address = (await Dns.GetHostAddressesAsync(server.Host, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            }
            catch (SocketException)
            {
                return null;
            }
        }

        if (address is null)
        {
            return null;
        }

        var samples = new List<int>();
        for (var i = 0; i < DelaySamples; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timing.ProbeTimeout);
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                var started = _time.GetTimestamp();
                await socket.ConnectAsync(new IPEndPoint(address, server.Port), timeout.Token).ConfigureAwait(false);
                samples.Add(Milliseconds(started));
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Не ответил — замер не в счет.
            }
        }

        return Median(samples);
    }

    /// <summary>
    /// Запрос к проверочному адресу через вход проверки ядра: <paramref name="warm"/> — по одному соединению (первый запрос —
    /// прогрев, не в счет), иначе каждый раз по новому.
    /// </summary>
    private async Task<int?> ThroughServerAsync(ProbeLink link, bool warm, CancellationToken cancellationToken)
    {
        var samples = new List<int>();
        var client = warm ? ProbeClient(link) : null;
        try
        {
            if (client is not null && await TimedAsync(client, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            for (var i = 0; i < DelaySamples; i++)
            {
                using var fresh = warm ? null : ProbeClient(link);
                if (await TimedAsync(client ?? fresh!, cancellationToken).ConfigureAwait(false) is { } ms)
                {
                    samples.Add(ms);
                }
            }
        }
        finally
        {
            client?.Dispose();
        }

        return Median(samples);
    }

    private async Task<int?> TimedAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timing.ProbeTimeout);
        try
        {
            var started = _time.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.LatencyTarget);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return Milliseconds(started);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static HttpClient ProbeClient(ProbeLink link) => new(new SocketsHttpHandler
    {
        Proxy = new LatencyProbe.FixedProxy(new Uri($"socks5://127.0.0.1:{link.Port}")) { Credentials = new NetworkCredential(link.User, link.Password) },
        UseProxy = true,
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private int Milliseconds(long started) => (int)Math.Max(1, Math.Round(_time.GetElapsedTime(started).TotalMilliseconds));
}
