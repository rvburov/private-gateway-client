using System.Net;
using System.Reflection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Demo;

/// <summary>
/// Имитация службы для этапа 1, пока службы нет: подключение идет через те же состояния и
/// переходы, что и настоящее, с задержкой, а данные — заведомо ненастоящие (адреса из диапазонов
/// документации). Интерфейс показывает, что работает на имитации (значок «демо»). Режим (VPN или
/// только прокси) берет из настроек при подключении — как настоящая служба из запроса Connect.
/// </summary>
public sealed class SimulatedGatewayClient(TimeProvider time, SettingsService? settings = null) : IGatewayClient, IDisposable
{
    /// <summary>Код ошибки «сервер не отвечает» в режиме прокси: блока при обрыве там нет.</summary>
    public const string ProxyServerNotResponding = ErrorCodes.ServerNotResponding;

    /// <summary>Сколько длится «подключение» в имитации.</summary>
    public static readonly TimeSpan ConnectDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>Сколько длятся проверки в имитации.</summary>
    public static readonly TimeSpan ChecksDelay = TimeSpan.FromMilliseconds(1400);

    /// <summary>Сколько последних записей журнала держит имитация.</summary>
    public const int JournalLimit = 200;

    private static readonly NetworkInfo DemoNetwork = new(NetworkKind.WiFi, "Home", false, false);

    // Имитация — из той же сборки, что окно: и версия, и коммит совпадают с окном.
    private static readonly string? Informational =
        typeof(SimulatedGatewayClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _journal =
    [
        new(time.GetUtcNow(), JournalLevel.Info, "service", "service started  ipc=ok"),
        // Имя сети в журнал не пишется — как у настоящей службы.
        new(time.GetUtcNow(), JournalLevel.Info, "network", "net up  wifi «•••»  v4+v6"),
    ];

    private CancellationTokenSource? _pending;
    private ITimer? _pauseEnd;

    // Сервер последнего подключения: пауза в «Блокировке», где попытки кончились, кончается подключением к нему.
    private ServerProfile? _server;
    private ConnectionStatus _status = ConnectionStatus.Initial(time.GetUtcNow()) with
    {
        Network = DemoNetwork,
        Exit = new ExitInfo(null, null, null, null, "198.51.100.7"),
    };

    public ConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public ServiceKind Service => ServiceKind.Simulated;

    /// <summary>Имитация — той же версии, что окно (одна сборка), и без настоящих ядер.</summary>
    public Task<ServiceDetails?> GetDetailsAsync(CancellationToken cancellationToken = default) => Task.FromResult<ServiceDetails?>(new ServiceDetails(
        Informational?.Split('+')[0] ?? "0.0.0", Contracts.ContractMapping.ApiVersion, [], SemVer.Build(Informational)));

    /// <summary>
    /// Сеть машины в демо: адреса из диапазонов документации (RFC 5737, RFC 3849) и MAC документации (RFC 7042). Туннель и его
    /// маршруты — только пока имитация защищает в режиме VPN, как у настоящей службы.
    /// </summary>
    public Task<NetworkSnapshot?> InspectNetworkAsync(CancellationToken cancellationToken = default)
    {
        var router = IPAddress.Parse("192.0.2.1");
        var tunnel = Tunneled();
        NetworkCard[] cards =
        [
            new("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", AdapterKind.Wifi, true, "00:00:5e:00:53:01", 1500, 866_000_000,
                [new(IPAddress.Parse("192.0.2.23"), 24), new(IPAddress.Parse("2001:db8:1::23"), 64)], [router], [router],
                new DhcpLease(router, TimeSpan.FromHours(20))),
            new("Ethernet", "Realtek PCIe GbE", AdapterKind.Ethernet, false, "00:00:5e:00:53:02", 1500, null, [], [], [], null),
            .. tunnel
                ? new NetworkCard[]
                {
                    new("Private Gateway", "Wintun Userspace Tunnel", AdapterKind.Tunnel, true, null, 1420, null,
                        [new(IPAddress.Parse("172.19.0.1"), 30)], [], [IPAddress.Parse("172.19.0.2")], null),
                }
                : [],
            new("Loopback", string.Empty, AdapterKind.Loopback, true, null, null, null, [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Loopback, 128)], [], [], null),
        ];
        NetworkRoute[] routes =
        [
            new("default", router, "Wi-Fi", 25),
            new("192.0.2.0/24", null, "Wi-Fi", 281),
            .. tunnel ? new NetworkRoute[] { new("0.0.0.0/1", null, "Private Gateway", 5), new("128.0.0.0/1", null, "Private Gateway", 5) } : [],
            new("2001:db8:1::/64", null, "Wi-Fi", 256),
        ];
        return Task.FromResult<NetworkSnapshot?>(new NetworkSnapshot(time.GetUtcNow(), cards, routes));
    }

    // Туннель имитации поднят: защита в режиме VPN, не на паузе.
    private bool Tunneled()
    {
        lock (_gate)
        {
            return _status is { State: ConnectionState.Connected, Mode: ConnectionMode.Vpn, IsPaused: false };
        }
    }

    /// <summary>
    /// Провайдер адреса в демо — без запроса в интернет: у сервера подключения — хостинг, у реального адреса — домашний
    /// провайдер; ASN — из диапазона документации (RFC 5398). Остальные адреса источник «не знает».
    /// </summary>
    public Task<AddressInfo?> DescribeAddressAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        AddressInfo info;
        lock (_gate)
        {
            var text = address.ToString();
            info = _server is { } server && server.Host == text
                ? new AddressInfo(address, 64500, "Example Hosting B.V.", Network24(address), server.CountryCode, server.City, null)
                : text == _status.Exit.DirectIp
                    ? new AddressInfo(address, 64496, "Example Broadband", Network24(address), "RU", "Москва", null)
                    : AddressInfo.Failed(address, AddressFailure.NotFound);
        }

        return Task.FromResult<AddressInfo?>(info);

        // Демо-адреса — IPv4: сеть /24 — первые три байта.
        static string Network24(IPAddress address) => string.Join('.', address.GetAddressBytes()[..3]) + ".0/24";
    }

    /// <summary>
    /// Замеры в демо — без сети: имя — адреса из диапазонов документации, путь — роутер, провайдер, узел; порт 443 и 80
    /// открыты, остальные закрыты; сертификат — выдуманный, без ошибок. Имя «nxdomain.example» не находится.
    /// </summary>
    public async IAsyncEnumerable<ProbeStep> ProbeAsync(ProbeKind kind, string target, int port,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var literal = IPAddress.TryParse(target, out var parsed);
        if (!literal && target == "nxdomain.example")
        {
            yield return new ProbeStep(1, null, null, ProbeOutcome.NotFound);
            yield break;
        }

        var address = literal ? parsed! : IPAddress.Parse("203.0.113.80");
        static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);
        switch (kind)
        {
            case ProbeKind.Dns:
                yield return new ProbeStep(1, address, Ms(18), ProbeOutcome.Ok);
                if (!literal)
                {
                    yield return new ProbeStep(2, IPAddress.Parse("2001:db8::80"), Ms(18), ProbeOutcome.Ok);
                }

                break;
            case ProbeKind.Ping:
                double[] times = [24.1, 23.6, 25.3, 23.9];
                for (var i = 0; i < times.Length; i++)
                {
                    yield return new ProbeStep(i + 1, address, Ms(times[i]), ProbeOutcome.Ok);
                }

                break;
            case ProbeKind.Trace:
                yield return new ProbeStep(1, IPAddress.Parse("192.0.2.1"), Ms(1.8), ProbeOutcome.Hop);
                yield return new ProbeStep(2, IPAddress.Parse("198.51.100.1"), Ms(7.4), ProbeOutcome.Hop);
                yield return new ProbeStep(3, null, null, ProbeOutcome.TimedOut);
                yield return new ProbeStep(4, IPAddress.Parse("198.51.100.77"), Ms(19.2), ProbeOutcome.Hop);
                yield return new ProbeStep(5, address, Ms(23.8), ProbeOutcome.Ok);
                break;
            case ProbeKind.Tcp:
                yield return port is 443 or 80 ? new ProbeStep(1, address, Ms(24.6), ProbeOutcome.Ok) : new ProbeStep(1, address, null, ProbeOutcome.Refused);
                break;
            case ProbeKind.Tls when port is 443 or 80:
                var now = time.GetUtcNow();
                yield return new ProbeStep(1, address, Ms(61.3), ProbeOutcome.Ok, new TlsFacts(
                    "TLS 1.3", "TLS_AES_128_GCM_SHA256", "h2", "CN=" + target, "CN=Example Demo CA, O=Example",
                    now.AddDays(-30), now.AddDays(60), [target, "www." + target], []));
                break;
            case ProbeKind.Tls:
                yield return new ProbeStep(1, address, null, ProbeOutcome.Refused);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>Сокеты в демо: браузер, мессенджер, ядро и служба на адресах из диапазонов документации.</summary>
    public Task<IReadOnlyList<NetworkConnection>?> ListConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var local = IPAddress.Parse("192.0.2.23");
        static IPEndPoint At(string address, int port) => new(IPAddress.Parse(address), port);
        NetworkConnection[] connections =
        [
            new(new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(local, 51234), At("203.0.113.80", 443), SocketState.Established, 4120), "firefox"),
            new(new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(local, 51240), At("198.51.100.14", 443), SocketState.Established, 4120), "firefox"),
            new(new SocketEntry(SocketProtocol.Udp, new IPEndPoint(local, 50000), At("203.0.113.90", 443), SocketState.Open, 4120), "firefox"),
            new(new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(local, 51302), At("198.51.100.200", 5222), SocketState.Established, 5310), "telegram"),
            new(new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(local, 51310), At("203.0.113.24", 443), SocketState.Closing, 5310), "telegram"),
            new(new SocketEntry(SocketProtocol.Tcp, At("127.0.0.1", 10808), null, SocketState.Listen, 2208), "xray"),
            new(new SocketEntry(SocketProtocol.Udp, new IPEndPoint(IPAddress.Any, 5353), null, SocketState.Open, null), null),
            new(new SocketEntry(SocketProtocol.Tcp, new IPEndPoint(IPAddress.IPv6Any, 22), null, SocketState.Listen, 880), "sshd"),
        ];
        return Task.FromResult<IReadOnlyList<NetworkConnection>?>(connections);
    }

    /// <summary>Трафик в демо: Wi-Fi растет на 1,5 МБ/с приема и 120 КБ/с отправки, каждую секунду имитации; туннель — пока он поднят.</summary>
    public async IAsyncEnumerable<TrafficSample> WatchTrafficAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (long second = 0; ; second++)
        {
            yield return new TrafficSample(time.GetUtcNow(), [
                new AdapterCounters("Wi-Fi", 1_288_490_188 + (second * 1_572_864), 241_172_480 + (second * 122_880)),
                .. Tunneled() ? [new AdapterCounters("Private Gateway", 734_003_200 + (second * 1_468_006), 98_566_144 + (second * 110_592))] : Array.Empty<AdapterCounters>(),
            ]);
            await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// До и после VPN в демо: «до» — домашний Wi-Fi с реальным адресом, «сейчас» — по состоянию имитации (под защитой —
    /// туннель и адрес сервера); адреса — из диапазонов документации.
    /// </summary>
    public Task<NetworkComparison?> CompareNetworkAsync(CancellationToken cancellationToken = default)
    {
        var router = IPAddress.Parse("192.0.2.1");
        var at = time.GetUtcNow();
        var real = new NetworkFacts(at.AddMinutes(-12), "198.51.100.7", "2001:db8:1::7", new IPEndPoint(IPAddress.Parse("198.51.100.7"), 51820),
            "Wi-Fi", router, [router]);
        ConnectionStatus status;
        lock (_gate)
        {
            status = _status;
        }

        var now = status is { State: ConnectionState.Connected, IsPaused: false, Exit.Ip: { } exit }
            ? new NetworkFacts(at, exit, null, new IPEndPoint(IPAddress.Parse(exit), 40112), "Private Gateway", null, [IPAddress.Parse("172.19.0.2")])
            : real with { TakenAt = at };
        return Task.FromResult<NetworkComparison?>(new NetworkComparison(status.State == ConnectionState.Off ? null : real, now));
    }

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public IReadOnlyList<JournalEntry> Journal
    {
        get
        {
            lock (_gate)
            {
                return [.. _journal];
            }
        }
    }

    public event EventHandler<JournalEntry>? JournalAdded;

    /// <summary>Имитация пинг не меряет: у демо-серверов он свой, а свои серверы ждут настоящую службу.</summary>
    public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());

    public async Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var mode = settings?.Current.Mode ?? ConnectionMode.Vpn;
        CancellationToken token;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _pending.Token;

            // Переход заканчивает и паузу для входа в Wi-Fi (подключение со строгой паузы).
            _pauseEnd?.Dispose();
            _pauseEnd = null;
            _server = server;
            // Уже подключается (другой сервер, конец паузы в «Подключении») — то же «Подключение», без перехода в само себя.
            var status = _status with { Mode = mode, ServerId = server.Id };
            _status = status.State == ConnectionState.Connecting
                ? status with { Since = time.GetUtcNow(), CaptivePauseUntil = null, Protection = ProtectionChecks.For(ConnectionState.Connecting, mode) }
                : status.MoveTo(ConnectionState.Connecting, time.GetUtcNow());
        }

        Raise();
        Log(JournalLevel.Info, "service", $"connect  server «{server.Name}»  mode={mode.ToString().ToLowerInvariant()}");
        try
        {
            await Task.Delay(ConnectDelay, time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool blocked;
        lock (_gate)
        {
            if (token.IsCancellationRequested || _status.State != ConnectionState.Connecting)
            {
                return;
            }

            var now = time.GetUtcNow();
            blocked = server.LatencyMs is null;
            if (server.LatencyMs is { } latency)
            {
                _status = _status.MoveTo(ConnectionState.Connected, now) with
                {
                    Protection = ProtectionChecks.For(ConnectionState.Connected, mode) with { CheckedAt = now },
                    Traffic = new TrafficStats(latency, 18.9, 4.0),
                    // Выход — сам сервер: в демо-данных адреса серверов — IP из диапазонов документации.
                    Exit = new ExitInfo(server.Host, server.City, "дата-центр", "AS64500", _status.Exit.DirectIp),
                };
            }
            else if (mode == ConnectionMode.Proxy)
            {
                // В режиме прокси блока при обрыве нет: перекрывать интернет нечем — это ошибка подключения.
                _status = (_status with { ErrorCode = ProxyServerNotResponding }).MoveTo(ConnectionState.Error, now) with
                {
                    Traffic = TrafficStats.Empty,
                    Exit = new ExitInfo(null, null, null, null, _status.Exit.DirectIp),
                };
            }
            else
            {
                // Сервер не отвечает (в списке пинг «нет»): как настоящая служба с блоком при обрыве —
                // интернет перекрыт, другой сервер сам не выбирается, решение за пользователем.
                _status = _status.MoveTo(ConnectionState.Blocked, now) with
                {
                    Protection = ProtectionChecks.For(ConnectionState.Blocked),
                    Traffic = TrafficStats.Empty,
                    Exit = new ExitInfo(null, null, null, null, _status.Exit.DirectIp),
                };
            }
        }

        if (blocked)
        {
            Log(JournalLevel.Warning, "service", mode == ConnectionMode.Proxy
                ? $"server «{server.Name}» not responding  proxy stopped"
                : $"server «{server.Name}» not responding  traffic blocked, waiting for user");
        }
        else if (mode == ConnectionMode.Proxy)
        {
            Log(JournalLevel.Info, "service", "proxy up  127.0.0.1  system proxy set  killswitch unavailable");
        }
        else
        {
            Log(JournalLevel.Info, "network", "route v4/v6 default -> tun");
            Log(JournalLevel.Info, "firewall", $"killswitch on  allow={server.Host}");
            Log(JournalLevel.Info, "service", $"tunnel up  server «{server.Name}»");
        }

        Raise();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pending?.Cancel();
            if (_status.State == ConnectionState.Off)
            {
                return Task.CompletedTask;
            }

            // Строгий режим: защита выключена, сеть закрыта — как у службы.
            if (settings?.Current is { } current && ConnectionController.KeepsBlock(current))
            {
                if (_status is { StrictBlock: true, IsPaused: false })
                {
                    return Task.CompletedTask;
                }

                var blocked = _status.State == ConnectionState.Blocked ? _status : _status.MoveTo(ConnectionState.Blocked, time.GetUtcNow());
                _status = blocked with
                {
                    StrictBlock = true,
                    Traffic = TrafficStats.Empty,
                    CaptivePauseUntil = null,
                    Protection = ProtectionChecks.For(ConnectionState.Blocked),
                };
                _pauseEnd?.Dispose();
                _pauseEnd = null;
                Log(JournalLevel.Info, "service", "strict mode: protection off, network blocked");
                Raise();
                return Task.CompletedTask;
            }

            _status = _status.MoveTo(ConnectionState.Off, time.GetUtcNow()) with
            {
                ServerId = null,
                Traffic = TrafficStats.Empty,
                Exit = new ExitInfo(null, null, null, null, _status.Exit.DirectIp),
                CaptivePauseUntil = null,
            };
            _pauseEnd?.Dispose();
            _pauseEnd = null;
        }

        Log(JournalLevel.Info, "service", "tunnel down  route restored  killswitch off");
        Raise();
        return Task.CompletedTask;
    }

    public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Как у службы: в «Защищено», «Блокировке», «Подключении», «Переподключении» и «Ошибке», где сеть закрыта, — не
            // дважды.
            if (!ConnectionController.CanPause(_status))
            {
                return Task.FromResult(false);
            }

            // Попытки подключения встают до конца паузы: сервер за порталом не ответит.
            if (_status.State is ConnectionState.Connecting or ConnectionState.Reconnecting)
            {
                _pending?.Cancel();
            }

            // Мимо туннеля: DNS у провайдера, IPv6 открыт, блок при обрыве на паузе.
            _status = _status with
            {
                CaptivePauseUntil = time.GetUtcNow() + duration,
                Protection = ProtectionChecks.Paused,
            };
            _pauseEnd?.Dispose();
            _pauseEnd = time.CreateTimer(_ => EndPause("timeout"), null, duration, Timeout.InfiniteTimeSpan);
        }

        Log(JournalLevel.Warning, "firewall", $"captive pause  {duration.TotalMinutes:0} min  direct traffic allowed");
        Raise();
        return Task.FromResult(true);
    }

    public Task ResumeProtectionAsync(CancellationToken cancellationToken = default)
    {
        EndPause("user");
        return Task.CompletedTask;
    }

    public async Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(ChecksDelay, time, cancellationToken).ConfigureAwait(false);
        DiagnosticReport report;
        var raise = false;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_status.State == ConnectionState.Connected)
            {
                _status = _status with { Protection = _status.Protection with { CheckedAt = now } };
                raise = true;
            }

            report = new DiagnosticReport(Checks(_status), now);
        }

        if (raise)
        {
            Raise();
        }

        var warnings = report.Results.Count(r => r.State == CheckState.Warning);
        Log(warnings == 0 ? JournalLevel.Info : JournalLevel.Warning, "service",
            $"checks done  ok={report.Results.Count(r => r.State == CheckState.Ok)}  warning={warnings}");
        return report;
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pauseEnd?.Dispose();
    }

    private void Raise() => StatusChanged?.Invoke(this, Status);

    /// <summary>
    /// Конец паузы — по времени или кнопкой «Вернуть защиту»: туннель и блок снова на месте; в «Блокировке» — тот же
    /// блок, строгий режим остается; где попытки кончились или встали на паузу («Подключение», «Переподключение») —
    /// подключение к тому же серверу, как у службы.
    /// </summary>
    private void EndPause(string reason)
    {
        ServerProfile? again;
        lock (_gate)
        {
            if (_status.CaptivePauseUntil is null)
            {
                return;
            }

            again = _status is { State: ConnectionState.Blocked, StrictBlock: false } or { State: ConnectionState.Connecting or ConnectionState.Reconnecting }
                ? _server
                : null;

            _pauseEnd?.Dispose();
            _pauseEnd = null;
            var connected = _status.State == ConnectionState.Connected;
            _status = _status with
            {
                CaptivePauseUntil = null,
                Protection = ProtectionChecks.For(_status.State, _status.Mode) with { CheckedAt = connected ? time.GetUtcNow() : null },
            };
        }

        Log(JournalLevel.Info, "firewall", $"captive pause end ({reason})  killswitch on");
        Raise();
        if (again is not null)
        {
            _ = ConnectAsync(again);
        }
    }

    /// <summary>Проверки по состоянию имитации. Служба честно отвечает, что она — имитация.</summary>
    private static List<DiagnosticResult> Checks(ConnectionStatus status)
    {
        var state = status.State;
        var protection = state is ConnectionState.Off
            ? ProtectionChecks.For(ConnectionState.Off)
            : status.Protection;
        var running = state switch
        {
            ConnectionState.Connected => CheckState.Ok,
            ConnectionState.Connecting or ConnectionState.Reconnecting => CheckState.Waiting,
            ConnectionState.Blocked or ConnectionState.Error => CheckState.Stopped,
            _ => CheckState.Off,
        };
        return
        [
            new(DiagnosticCheck.Service, CheckState.Warning, "simulated"),
            new(DiagnosticCheck.Core, running, state is ConnectionState.Blocked ? "no_reply" : null),
            new(DiagnosticCheck.AllTraffic, protection.AllTraffic),
            new(DiagnosticCheck.KillSwitch, protection.KillSwitch),
            new(DiagnosticCheck.Dns, protection.Dns),
            new(DiagnosticCheck.Ipv6, protection.Ipv6),
            new(DiagnosticCheck.ExitIp, running),
            new(DiagnosticCheck.OtherVpn, CheckState.Ok),
        ];
    }

    private void Log(JournalLevel level, string source, string message)
    {
        var entry = new JournalEntry(time.GetUtcNow(), level, source, message);
        lock (_gate)
        {
            _journal.Add(entry);
            if (_journal.Count > JournalLimit)
            {
                _journal.RemoveAt(0);
            }
        }

        JournalAdded?.Invoke(this, entry);
    }
}
