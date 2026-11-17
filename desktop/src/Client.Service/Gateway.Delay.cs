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

    /// <summary>Последний вывод <see cref="NoteServerTcpAsync"/>: 0 — не было (или подключение удалось), 1 — молчит, 2 — отвечает.</summary>
    private int _serverTcpVerdict;

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

    /// <summary>
    /// Прямое TCP-соединение с сервером — его адрес тот, к которому соединяется ядро; сервер по UDP — null. В режиме VPN сокет
    /// службы без привязки ушел бы в свой же TUN, и соединение за миллисекунды завершал бы sing-box на этой машине — «До
    /// сервера: 2 мс» на раздаче iPhone (отчет пользователя 2026-11-02, HS-10). Поэтому под туннелем замер — через карту
    /// маршрута по умолчанию, как ходит само ядро (сервер блок при обрыве пропускает); карты нет или привязка не удалась — null,
    /// а не число пути внутри машины.
    /// </summary>
    private async Task<int?> ToServerAsync(ServerDefinition server, CancellationToken cancellationToken) =>
        (await ServerTcpAsync(server, DelaySamples, cancellationToken).ConfigureAwait(false))?.Ms;

    /// <summary>Ответ сервера по TCP: <see cref="Ms"/> — медиана, null — ни один замер не ответил.</summary>
    private sealed record TcpAnswer(int? Ms);

    /// <summary>
    /// Соединения TCP с сервером (<paramref name="count"/> замеров, как в <see cref="ToServerAsync"/>). null — путь не
    /// измерить (сервер по UDP, нет карты мимо туннеля, адрес не найден); <see cref="TcpAnswer"/> без числа — сервер не ответил.
    /// <paramref name="addresses"/> — адреса именно этого сервера; null — адреса нынешнего подключения.
    /// </summary>
    private async Task<TcpAnswer?> ServerTcpAsync(ServerDefinition server, int count, CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? addresses = null)
    {
        if (server.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic or ProxyProtocol.WireGuard)
        {
            return null;
        }

        string? outside = null;
        if (Volatile.Read(ref _tunnel) is not null)
        {
            outside = OutsideAdapter();
            if (outside is null)
            {
                return null;
            }
        }

        var known = addresses ?? _addresses;
        var address = outside is null
            ? known.Count > 0 ? known[0] : IPAddress.TryParse(server.Host.Trim('[', ']'), out var literal) ? literal : null
            : known.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (address is null && outside is not null)
        {
            // Привязка к карте — только для IPv4; имя сервера в туннеле не ищется (DNS ОС смотрит в туннель).
            return null;
        }

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
        for (var i = 0; i < count; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timing.ProbeTimeout);
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (outside is not null)
                {
                    DnsProbe.BindTo(socket, outside);
                }

                var started = _time.GetTimestamp();
                await socket.ConnectAsync(new IPEndPoint(address, server.Port), timeout.Token).ConfigureAwait(false);
                samples.Add(Milliseconds(started));
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Не ответил — замер не в счет.
            }
            catch (Exception e) when (e is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException)
            {
                // Привязка к карте не удалась: путь мимо туннеля не измерить.
                return null;
            }
        }

        return new TcpAnswer(Median(samples));
    }

    /// <summary>
    /// Профиль не ответил: отвечает ли сам сервер по TCP — в журнал (а с ним в «Проверку» и отчет) понятным выводом. Так видно,
    /// работает ли ссылка: сервер молчит — выключен, порт закрыт или путь до него режется; отвечает — жив, но через него связи
    /// нет: профиль не принят (ключ, REALITY, flow) или сервер не выпускает в интернет. Пользователь 2026-11-05: при «попытке
    /// 130» из окна было не понять, в ссылке дело или в сети. Соединение — только к адресу и порту сервера, их блок пропускает.
    /// </summary>
    private async Task NoteServerTcpAsync(ServerDefinition server, IReadOnlyList<IPAddress> addresses)
    {
        TcpAnswer? answer;
        try
        {
            answer = await ServerTcpAsync(server, 2, CancellationToken.None, addresses).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            return;
        }

        // Вывод — только когда он сменился: на каждой из сотни попыток подряд одна и та же строка не нужна.
        if (answer is null || Interlocked.Exchange(ref _serverTcpVerdict, answer.Ms is null ? 1 : 2) == (answer.Ms is null ? 1 : 2))
        {
            return;
        }

        // Причина словами ядра — строка Xray об отказе выхода к серверу за последние минуты: обычной записью она тонет среди
        // строк соединений, а в выводе видна и в «Ошибках и предупреждениях» отчета.
        var core = answer.Ms is not null
            ? CoreLines.OutboundFailure(_journal.Tail(ServiceJournal.Capacity), _time.GetUtcNow() - TimeSpan.FromMinutes(2))
            : null;
        _journal.Add(JournalLevel.Warning, "service", answer.Ms is { } ms
            ? $"Сервер отвечает по TCP на порт {server.Port} ({ms} мс), но связи через него нет: профиль не принят (ключ, REALITY, flow) или сервер не выпускает в интернет — проверьте ссылку или выберите другой сервер"
                + (core is null ? string.Empty : ". Ядро: " + core)
            : $"Сервер не отвечает по TCP на порт {server.Port}: он выключен, порт закрыт или путь до него режется — дело не в настройках этого компьютера, если другие серверы отвечают");
    }

    /// <summary>Карта маршрута по умолчанию, не туннель: через нее ядро идет к серверу. Маршрутов служба не читает — null.</summary>
    private string? OutsideAdapter()
    {
        try
        {
            return _options.Routes?.DefaultRoutes().FirstOrDefault(r => !r.IsTunnel)?.Adapter;
        }
        catch (Exception e) when (e is IOException or System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
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
