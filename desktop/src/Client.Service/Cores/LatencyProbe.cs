using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
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

    /// <summary>Проверочный адрес внешнего IP: Cloudflare по адресу (имя не нужно разрешать), ответ — строки «ключ=значение».</summary>
    public static readonly Uri DefaultTrace = new("https://1.1.1.1/cdn-cgi/trace");

    /// <summary>Тот же проверочный адрес по IPv6: ответил через сервер — сервер открывает IPv6.</summary>
    public static readonly Uri DefaultTrace6 = new("https://[2606:4700:4700::1111]/cdn-cgi/trace");

    /// <summary>DoH в формате JSON — найти адрес сервера по имени через туннель (служба, пока туннель поднят).</summary>
    public static readonly Uri DefaultDoh = new("https://1.1.1.1/dns-query");

    private static readonly string[] DohTypes = ["A", "AAAA"];

    /// <summary>Сколько серверов проверяется одновременно.</summary>
    public const int MaxParallel = 8;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Задержка каждого из <paramref name="servers"/>. Hysteria2 и TUIC — через ретранслятор <paramref name="singBox"/>
    /// (<see cref="CoreRelay"/>); null или нет сборки — у них «не поддерживается».
    /// </summary>
    public static async Task<IReadOnlyList<LatencyResult>> MeasureAsync(
        string xray,
        string? singBox,
        IReadOnlyList<ServerDefinition> servers,
        ConnectionSettings connection,
        Uri target,
        TimeSpan timeout,
        TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(time);
        var ports = servers.Select(_ => LocalPort.FreeForTcpAndUdp()).ToList();
        var relays = servers.Select(server => CoreRelay.LinkFor(server, singBox)).ToList();

        // Входы замера — с паролем на этот замер: служба работает от SYSTEM или root, и без пароля любая программа на машине,
        // пока идет замер, прошла бы через них к серверам в обход правил.
        var account = new SocksAccount(VpnTunnel.Secret(), VpnTunnel.Secret());
        var probe = XrayConfigBuilder.BuildProbe(servers, ports, connection, relays, account);
        var results = probe.Rejections
            .Select(r => r is null ? null : LatencyResult.Failed(LatencyFailure.Unsupported))
            .ToArray();
        if (probe.Config is not { } config)
        {
            return Done(results);
        }

        var checkedIndexes = Enumerable.Range(0, servers.Count).Where(i => results[i] is null).ToList();
        using var core = CoreRelay.StartXray(xray, config, singBox,
            [.. checkedIndexes.Where(i => relays[i] is not null).Select(i => new RelayRoute(servers[i], relays[i]!))], CoreLogLevel.Warning, out var relayReady);
        try
        {
            if (!await WaitForPortsAsync(core, [.. checkedIndexes.Select(i => ports[i])], relayReady, time, cancellationToken).ConfigureAwait(false))
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
                async (i, token) => results[i] = await RequestAsync(ports[i], target, timeout, time, token, new NetworkCredential(account.User, account.Password))
                    .ConfigureAwait(false))
                .ConfigureAwait(false);
            return Done(results);
        }
        finally
        {
            await core.StopAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Задержка до проверочного адреса через ядро и сервер, мс; нет ответа за время пробы — null.</summary>
    public static async Task<int?> DelayAsync(ServerProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return (await RequestAsync(probe.Port, probe.Target, probe.Timeout, TimeProvider.System, cancellationToken, probe.Credentials)
            .ConfigureAwait(false)).Milliseconds;
    }

    /// <summary>Отвечает ли сервер: любой ответ HTTP через ядро — да; нет ответа за время пробы — нет.</summary>
    public static async Task<bool> ReachesAsync(ServerProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return (await RequestAsync(probe.Port, probe.Target, probe.Timeout, TimeProvider.System, cancellationToken, probe.Credentials)
            .ConfigureAwait(false)).Milliseconds is not null;
    }

    /// <summary>
    /// Внешний IP по ответу проверочного адреса (строка «ip=…»): через вход ядра (порт не 0) — IP выхода сервера;
    /// при порте 0 — без прокси, как у любой программы: до туннеля — реальный IP, в туннеле — IP, который видят
    /// сайты. Нет ответа или IP в нем — null.
    /// </summary>
    public static async Task<string?> ExitIpAsync(ServerProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        using var handler = new SocketsHttpHandler
        {
            Proxy = probe.Port == 0 ? null : new FixedProxy(new Uri($"socks5://127.0.0.1:{probe.Port}")) { Credentials = probe.Credentials },
            UseProxy = probe.Port != 0,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var http = new HttpClient(handler) { Timeout = probe.Timeout };
        try
        {
            return TraceIp(await http.GetStringAsync(probe.Target, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Адреса имени — запросами DoH (JSON, A и AAAA) через вход ядра <paramref name="probe"/>: через сервер, мимо DNS ОС.
    /// Нет ответа — пусто.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> LookupAsync(ServerProbe probe, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        using var handler = new SocketsHttpHandler
        {
            Proxy = new FixedProxy(new Uri($"socks5://127.0.0.1:{probe.Port}")) { Credentials = probe.Credentials },
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var http = new HttpClient(handler) { Timeout = probe.Timeout };
        return await QueryAsync(http, probe.Target, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Адреса имени — запросами DoH (JSON, A и AAAA) напрямую, мимо туннеля и DNS ОС: поиск самой службы под блоком при обрыве.
    /// Сокет на Linux помечен <paramref name="mark"/> — по метке его пропускает блок (метку ставит только root). Нет ответа — пусто.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> LookupDirectAsync(Uri doh, string name, uint mark, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doh);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (OperatingSystem.IsLinux() && mark != 0)
                    {
                        // SOL_SOCKET = 1, SO_MARK = 36.
                        socket.SetRawSocketOption(1, 36, BitConverter.GetBytes(mark));
                    }

                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        return await QueryAsync(http, doh, name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(HttpClient http, Uri doh, string name, CancellationToken cancellationToken)
    {
        var answers = await Task.WhenAll(DohTypes.Select(async type =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(doh, $"?name={Uri.EscapeDataString(name)}&type={type}"));
            request.Headers.Accept.ParseAdd("application/dns-json");
            try
            {
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? DohAnswers(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) : [];
            }
            catch (HttpRequestException)
            {
                return [];
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        })).ConfigureAwait(false);
        return [.. answers.SelectMany(a => a)];
    }

    /// <summary>Адреса из ответа DoH в формате JSON (записи раздела Answer с адресом — A и AAAA); не разобрать — пусто.</summary>
    public static IReadOnlyList<IPAddress> DohAnswers(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json)?["Answer"] is JsonArray answer
                ? [.. answer.OfType<JsonObject>()
                    .Select(a => a["data"]?.GetValueKind() == JsonValueKind.String && IPAddress.TryParse(a["data"]!.GetValue<string>(), out var ip) ? ip : null)
                    .OfType<IPAddress>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>IP из ответа проверочного адреса: строка «ip=…»; нет ее или в ней не адрес — null.</summary>
    public static string? TraceIp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ip=", StringComparison.Ordinal)).Select(l => l[3..])
            .FirstOrDefault(v => IPAddress.TryParse(v, out _));
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

    /// <summary>
    /// Ждать, пока ядро откроет все входы, а ретранслятор Hysteria2 и TUIC, если он есть, скажет, что готов; false — ядро вышло
    /// или не успело.
    /// </summary>
    private static async Task<bool> WaitForPortsAsync(CoreProcess core, IReadOnlyList<int> ports, Func<bool> relayReady, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var pending = new Queue<int>(ports);
        while (pending.Count > 0 || !relayReady())
        {
            if (core.HasExited || time.GetElapsedTime(started) > StartTimeout)
            {
                return false;
            }

            if (pending.TryPeek(out var port))
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                    pending.Dequeue();
                    continue;
                }
                catch (SocketException)
                {
                    // Еще не открыт.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Прокси для всех адресов: WebProxy пропускает localhost мимо прокси, а проверке нужен только вход ядра.</summary>
    internal sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
