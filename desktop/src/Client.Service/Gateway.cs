using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Что умеет эта служба. <paramref name="CanVpn"/> — запущена с правами администратора;
/// <paramref name="LatencyTarget"/> — проверочный адрес задержки (в тестах — свой сайт);
/// <paramref name="SingBoxPath"/> — TUN режима VPN; <paramref name="KillSwitch"/> — блок при обрыве
/// этой ОС, без него режима VPN нет; <paramref name="TunnelNetns"/> — ядра режима VPN в сетевом пространстве Linux (тесты);
/// <paramref name="Routes"/> — маршруты машины, чтобы найти чужой VPN; null — проверки нет;
/// <paramref name="Reconnect"/> — проба связи с сервером и паузы переподключения; null — без проб
/// (только выход ядра), паузы по умолчанию; <paramref name="Probe"/> — проба (тесты); null — через ядро;
/// <paramref name="Network"/> — смена сети машины: проверить сервер сразу; null — не следить;
/// <paramref name="BootBlock"/> — файл «Защиты при загрузке» (<see cref="Service.BootBlock"/>); null — без нее;
/// <paramref name="Trace"/> — проверочный адрес внешнего IP (<see cref="LatencyProbe.DefaultTrace"/>); null — IP не мерить;
/// <paramref name="ExitIp"/> — запрос внешнего IP (тесты); null — <see cref="LatencyProbe.ExitIpAsync"/>;
/// <paramref name="BootConnect"/> — последнее подключение для «Защиты при загрузке» (<see cref="BootConnection"/>):
/// служба подключается при загрузке сама; null — только блок.
/// </summary>
public sealed record GatewayOptions(
    string? XrayPath,
    bool CanVpn,
    Uri LatencyTarget,
    string? SingBoxPath = null,
    IKillSwitch? KillSwitch = null,
    string? TunnelNetns = null,
    IRouteTable? Routes = null,
    ReconnectTiming? Reconnect = null,
    Func<ServerProbe, CancellationToken, Task<bool>>? Probe = null,
    INetworkEvents? Network = null,
    string? BootBlock = null,
    Uri? Trace = null,
    Func<ServerProbe, CancellationToken, Task<string?>>? ExitIp = null,
    BootConnection? BootConnect = null);

/// <summary>
/// Проба связи с сервером и переподключение. <paramref name="Every"/> — как часто, пока «Защищено»,
/// проверять, что сервер отвечает (null — не проверять, переподключаться только при выходе ядра);
/// <paramref name="ProbeTimeout"/> — сколько ждать ответа; паузы между попытками — от
/// <paramref name="FirstPause"/> вдвое до <paramref name="MaxPause"/>; <paramref name="GiveUpAfter"/> —
/// предел политики «До 5 минут».
/// </summary>
public sealed record ReconnectTiming(TimeSpan? Every, TimeSpan ProbeTimeout, TimeSpan FirstPause, TimeSpan MaxPause, TimeSpan GiveUpAfter)
{
    public static ReconnectTiming Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5));
}

/// <summary>
/// Защита на стороне службы: запрос Connect — конфиг ядра собирается здесь же из описания сервера
/// и настроек, ядро запускается отдельным процессом, состояние меняется только по
/// <see cref="ConnectionStateMachine"/> и уходит подписчикам WatchStatus. Служба — единственный
/// источник состояния: и отказ в подключении — ее «Ошибка». Режим прокси — Xray с входом на
/// 127.0.0.1; режим VPN — блок при обрыве до запуска ядер, затем туннель (<see cref="VpnTunnel"/>).
/// Блок держится и при сбое ядра, и после остановки службы; снимают его отключение и отказ. Связь с
/// сервером пропала — переподключение к нему же (Gateway.Reconnect.cs).
/// </summary>
public sealed partial class Gateway : IAsyncDisposable
{
    private static readonly TimeSpan LatencyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Дольше пауза для входа в Wi-Fi не бывает: защита не должна забыться выключенной.</summary>
    public static readonly TimeSpan MaxCaptivePause = TimeSpan.FromMinutes(15);

    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ServiceJournal _journal;
    private readonly SystemProxyGuard? _systemProxy;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly Lock _gate = new();
    private ConnectionStatus _status;
    private CoreProcess? _core;
    private VpnTunnel? _tunnel;
    private readonly List<ChannelWriter<ConnectionStatus>> _watchers = [];

    // Правила блока при обрыве стоят: поставлены подключением или остались с прошлого запуска.
    private volatile bool _blocking;

    // Номер подключения: выход старого ядра и запоздалая готовность не трогают новое.
    private int _session;

    // Что просили в текущем подключении: режим и системный прокси нужны, когда ядро готово.
    private CoreRequest? _request;

    // Вход проверки связи с сервером в ядре этого подключения.
    private ProbeLink? _probe;

    // Пользователь выбрал подключение, хотя работает другой VPN: на ходу чужой VPN тоже не проверять.
    private volatile bool _allowOtherVpn;

    // Реальный IP — измеренный напрямую, пока блока и туннеля нет; с ним сравнивается IP для сайтов. Сеть сменилась — забыт.
    private string? _directIp;

    // Подключение при загрузке (ConnectAtBootAsync): отказ не снимает блок, поставленный при запуске.
    private volatile bool _holdBlock;

    // Режим VPN: адреса сервера и блок при обрыве — чтобы поднять туннель снова после паузы.
    private IReadOnlyList<IPAddress> _addresses = [];
    private bool _killSwitchOn;

    // Блок, который держит служба (и после паузы, когда правила сняты): вернуть его после паузы и при остановке,
    // узнать тот же сервер, когда имя под блоком не найти. Поставлен этим запуском или прочитан из файла блока.
    private HeldBlock? _held;

    // Конец паузы для входа в Wi-Fi; любая смена состояния паузу заканчивает.
    private ITimer? _pauseEnd;

    /// <param name="options">Что умеет служба.</param>
    /// <param name="time">Часы.</param>
    /// <param name="journal">Журнал службы.</param>
    /// <param name="systemProxy">Системный прокси ОС для режима прокси; null — служба его не трогает (установленная служба).</param>
    public Gateway(GatewayOptions options, TimeProvider time, ServiceJournal journal, SystemProxyGuard? systemProxy = null)
    {
        _options = options;
        _time = time;
        _journal = journal;
        _systemProxy = systemProxy;
        _status = ConnectionStatus.Initial(time.GetUtcNow());
        if (options.Network is { } network)
        {
            network.Changed += OnNetworkChanged;
        }

        var held = options.BootBlock is { } path ? Service.BootBlock.Read(path) : null;
        if (options.KillSwitch is { } killSwitch && Try(() => killSwitch.IsEnabled, "Не прочитать правила блока при обрыве"))
        {
            // Служба упала или остановилась с включенной защитой: трафик заблокирован — так и показывать. Какой это
            // блок и строгий ли режим — из файла блока (на Windows постоянные фильтры WFP стоят и после перезагрузки).
            HoldAtStart(held);
            journal.Add(JournalLevel.Warning, "service", held is { Strict: true }
                ? "Строгий режим: блок при обрыве остался с прошлого запуска — сеть закрыта до подключения или выключения строгого режима"
                : "Блок при обрыве остался с прошлого запуска: трафик заблокирован до подключения или выключения защиты");
        }
        else if (options.KillSwitch is { } boot && held is { Boot: true }
            && Try(() => { boot.Enable(held.Policy); return true; }, "«Защита при загрузке»: блок не включился"))
        {
            // Защита была включена, когда машина выключилась: блок — раньше сети, до подключения или выключения.
            HoldAtStart(held);
            RememberBlock(_held);
            journal.Add(JournalLevel.Warning, "service", held.Strict
                ? "Защита при загрузке, строгий режим: сеть закрыта до подключения или выключения строгого режима"
                : "Защита при загрузке: защита была включена — трафик заблокирован до подключения или выключения защиты");
        }
        else if (options.KillSwitch is { } paused && held is { Paused: true }
            && Try(() => { paused.Enable(held.Policy); return true; }, "Блок при обрыве не вернулся после паузы"))
        {
            // Служба упала на паузе для входа в Wi-Fi: пауза кончилась вместе с ней — блок возвращается.
            HoldAtStart(held);
            RememberBlock(_held);
            journal.Add(JournalLevel.Warning, "service", "Служба остановилась на паузе для входа в Wi-Fi: блок при обрыве возвращен");
        }
        else if (held is { Boot: false })
        {
            // Блок без «Защиты при загрузке» перезагрузку не пережил — помнить нечего.
            RememberBlock(null);
        }

        // Своего sing-box еще нет: правила маршрутизации, оставшиеся от его аварии в прошлом запуске, — убрать.
        if (options.CanVpn && VpnTunnel.RemoveLeftoverRules(options.TunnelNetns) is { Length: > 0 } removed)
        {
            journal.Add(JournalLevel.Warning, "service", removed);
        }
    }

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

    /// <summary>
    /// Текущее состояние и все следующие — атомарно: снимок и подписка под той же блокировкой, что
    /// и смена состояния, поэтому изменение не теряется и не приходит дважды. Подписка снимается
    /// завершением <paramref name="stop"/>.
    /// </summary>
    public (ConnectionStatus Current, ChannelReader<ConnectionStatus> Updates) Watch(CancellationToken stop)
    {
        var channel = Channel.CreateUnbounded<ConnectionStatus>(new UnboundedChannelOptions { SingleReader = true });
        ConnectionStatus current;
        lock (_gate)
        {
            _watchers.Add(channel.Writer);
            current = _status;
        }

        stop.Register(() =>
        {
            lock (_gate)
            {
                _watchers.Remove(channel.Writer);
            }

            channel.Writer.TryComplete();
        });
        return (current, channel.Reader);
    }

    public Task<V1.ConnectReply> ConnectAsync(V1.ConnectRequest request, CancellationToken cancellationToken = default) =>
        ConnectAsync(request, null, cancellationToken);

    /// <summary>
    /// «Защита при загрузке» с запомненным подключением (<see cref="GatewayOptions.BootConnect"/>): служба сама
    /// подключается к последнему серверу, до входа пользователя, по адресам, найденным тогда, — сети при загрузке
    /// может еще не быть. Блок стоит с запуска; отказ его не снимает — «Блокировка», как без подключения.
    /// Подключения нет или блока нет (защиту выключили) — false.
    /// </summary>
    public async Task<bool> ConnectAtBootAsync(CancellationToken cancellationToken = default)
    {
        // Строгий режим: защиту выключили вручную — при загрузке сеть закрыта, а не подключена.
        if (!_blocking || Status.StrictBlock || _options.BootConnect?.Load() is not { } stored)
        {
            return false;
        }

        _journal.Add(JournalLevel.Info, "service", "Защита при загрузке: подключение к последнему серверу до входа пользователя");
        _holdBlock = true;
        try
        {
            return (await ConnectAsync(stored.Request, stored.Addresses, cancellationToken).ConfigureAwait(false)).Accepted;
        }
        finally
        {
            _holdBlock = false;
        }
    }

    /// <param name="request">Запрос окна.</param>
    /// <param name="known">Адреса сервера, найденные раньше (подключение при загрузке); null — найти сейчас.</param>
    /// <param name="cancellationToken">Отмена.</param>
    private async Task<V1.ConnectReply> ConnectAsync(V1.ConnectRequest request, IReadOnlyList<IPAddress>? known, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoreRequest core;
        ProtectionSettings protection;
        bool killSwitchOn;
        try
        {
            (protection, killSwitchOn) = (request.Protection ?? throw new ArgumentException("Нет настроек защиты")).ToCore();
            core = new CoreRequest(
                (request.Server ?? throw new ArgumentException("Нет сервера")).ToCore(),
                request.Mode.ToCore(),
                protection,
                (request.Connection ?? throw new ArgumentException("Нет настроек подключения")).ToCore(),
                (request.Routing ?? throw new ArgumentException("Нет маршрутизации")).ToCore());
        }
        catch (ArgumentException e)
        {
            return await RejectAsync(request, null, ErrorCodes.BadRequest, e.Message, cancellationToken).ConfigureAwait(false);
        }

        var serverId = request.Server.Id;
        var vpn = core.Mode == ConnectionMode.Vpn;
        if (vpn && !_options.CanVpn)
        {
            return await RejectAsync(request, core.Mode, ErrorCodes.AdminRequired, "Режиму VPN нужны права администратора", cancellationToken)
                .ConfigureAwait(false);
        }

        if (vpn && (_options.KillSwitch is null || _options.SingBoxPath is not { } singBox || !File.Exists(singBox)))
        {
            return await RejectAsync(request, core.Mode, ErrorCodes.VpnNotReady,
                _options.KillSwitch is null ? "Режим VPN на этой ОС еще не готов: нет блока при обрыве" : "Нет сборки sing-box", cancellationToken)
                .ConfigureAwait(false);
        }

        // Вход проверки связи с сервером — свой на каждое подключение.
        var probe = VpnTunnel.NewProbe();
        var built = XrayConfigBuilder.Build(core, probe: probe);
        if (built.Config is not { } config)
        {
            return await RejectAsync(request, core.Mode, ErrorCodes.UnsupportedServer, "Ядро Xray не возьмется за сервер: " + built.Rejection,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.XrayPath is not { } xray || !File.Exists(xray))
        {
            return await RejectAsync(request, core.Mode, ErrorCodes.CoreFailed, "Нет сборки ядра Xray", cancellationToken).ConfigureAwait(false);
        }

        // Чужой туннель с маршрутом ко всему интернету: два VPN сразу — трафик может пойти мимо нашего.
        if (vpn && OtherVpnAdapters() is { Count: > 0 } other)
        {
            if (!request.AllowOtherVpn)
            {
                return await RejectAsync(request, core.Mode, ErrorCodes.OtherVpn,
                    $"Работает другой VPN: маршрут ко всему интернету через {string.Join(", ", other)}", cancellationToken).ConfigureAwait(false);
            }

            _journal.Add(JournalLevel.Warning, "service", $"Работает другой VPN ({string.Join(", ", other)}) — подключение по выбору пользователя");
        }

        // Реальный IP — до блока и туннеля, пока прямой путь открыт: один раз на сеть, вместе с поиском адреса сервера,
        // чтобы не ждать дважды. Под блоком и в туннеле прежнего подключения прямого пути нет.
        var direct = vpn && _directIp is null && !_blocking && Volatile.Read(ref _tunnel) is null && core.Protection.CheckLeaksAfterConnect
            ? DirectIpAsync()
            : Task.FromResult<string?>(null);
        IReadOnlyList<IPAddress> addresses = vpn ? known ?? await ResolveAsync(core, cancellationToken).ConfigureAwait(false) : [];
        _directIp = await direct.ConfigureAwait(false) ?? _directIp;
        if (vpn && addresses.Count == 0 && HeldAddresses(core) is { Count: > 0 } held)
        {
            // Под блоком DNS мимо туннеля закрыт, имени не найти: тот же сервер — по адресам, найденным раньше.
            addresses = held;
            _journal.Add(JournalLevel.Info, "service", "Адрес сервера под блоком не найден — взят найденный при прежнем подключении");
        }

        if (vpn && addresses.Count == 0)
        {
            // Отказ снимает блок (в строгом режиме — нет), повторное подключение найдет адрес.
            return await RejectAsync(request, core.Mode, ErrorCodes.ServerNotFound, "Адрес сервера не найден", cancellationToken).ConfigureAwait(false);
        }

        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        int session;
        try
        {
            // Сначала новый номер: выход старого ядра при остановке не должен стать ошибкой нового подключения.
            session = ++_session;
            _request = core;
            _probe = probe;
            _allowOtherVpn = request.AllowOtherVpn;
            await StopCoreAsync().ConfigureAwait(false);
            var credentials = core.Server.Credentials;
            _journal.Hide([credentials.Uuid, credentials.Password, credentials.ObfsPassword, credentials.PrivateKey, credentials.PresharedKey]);
            if (vpn)
            {
                (_addresses, _killSwitchOn) = (addresses, killSwitchOn);

                // Блок — до запуска ядер: в «Подключение» трафик уже не уходит мимо туннеля.
                if (killSwitchOn && !Block(core, addresses))
                {
                    BeginConnecting(core.Mode, serverId);
                    Fail(session, ErrorCodes.KillSwitchFailed, "Блок при обрыве не включился");
                    return new V1.ConnectReply { Accepted = true };
                }

                if (killSwitchOn && core.Protection.BootProtection)
                {
                    RememberBootConnection(request, addresses);
                }

                if (!killSwitchOn)
                {
                    ReleaseBlock();
                }

                BeginConnecting(core.Mode, serverId);
                _journal.Add(JournalLevel.Info, "service",
                    $"Подключение в режиме VPN: {core.Server.Protocol}, блок при обрыве {(killSwitchOn ? "включен" : "выключен")}");
                _ = StartTunnelAsync(session, core, addresses, killSwitchOn);
                return new V1.ConnectReply { Accepted = true };
            }

            // Режиму прокси блок VPN мешает: ядро соединяется с сервером мимо туннеля.
            ReleaseBlock();
            BeginConnecting(core.Mode, serverId);
            _journal.Add(JournalLevel.Info, "service", $"Подключение в режиме прокси: {core.Server.Protocol}, порт {core.Connection.ProxyPort}");

            // Порт занят другой программой — ядро на нем не откроется, а проверка готовности приняла бы чужой вход за свой.
            if (!PortIsFree(core.Connection.ProxyPort, core.Connection.ProxyAllowLan))
            {
                Fail(session, ErrorCodes.CoreFailed, $"Порт {core.Connection.ProxyPort} занят другой программой — выберите другой в «Подключении и ядре»");
                return new V1.ConnectReply { Accepted = true };
            }

            try
            {
                var process = CoreProcess.StartXray(xray, config);
                process.Output += (_, line) => _journal.Add(JournalLevel.Info, "xray", line);
                process.Exited += (_, _) => OnCoreExited(session, process);
                _core = process;
                _journal.Add(JournalLevel.Info, "service", "Ядро Xray запущено, процесс " + process.Id);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Fail(session, ErrorCodes.CoreFailed, "Ядро не запустилось: " + e.Message);
                return new V1.ConnectReply { Accepted = true };
            }
        }
        finally
        {
            _switch.Release();
        }

        _ = WaitReadyAsync(session, core.Connection.ProxyPort, TimeSpan.FromSeconds(core.Connection.ConnectTimeoutSeconds));
        return new V1.ConnectReply { Accepted = true };
    }

    /// <summary>
    /// Адаптеры чужих туннелей с маршрутом ко всему интернету — свой туннель не в счет; пусто — чужого
    /// VPN нет. Маршрутов служба не читает или прочитать не вышло — null: проверки нет (это в журнале).
    /// </summary>
    public IReadOnlyList<string>? OtherVpnAdapters()
    {
        if (_options.Routes is not { } routes)
        {
            return null;
        }

        try
        {
            return OtherVpn.Find(routes.DefaultRoutes(), SingBoxConfigBuilder.InterfaceName);
        }
        catch (IOException e)
        {
            _journal.Add(JournalLevel.Warning, "service", "Маршруты не прочитаны, другой VPN не проверен: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Выключить защиту. <paramref name="keepBlock"/> — строгий режим: в режиме VPN с блоком при обрыве сеть
    /// остается закрытой — ядра остановлены, блок без туннеля, «Блокировка» до подключения или выключения
    /// строгого режима; так же — с блоком, оставшимся с прошлого запуска. Без блока или в режиме прокси
    /// закрывать нечем — защита просто выключается.
    /// </summary>
    public async Task DisconnectAsync(bool keepBlock = false, CancellationToken cancellationToken = default)
    {
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session++;
            await StopCoreAsync().ConfigureAwait(false);
            _systemProxy?.Release();
            _journal.Hide([]);
            // Блок, которого служба не знает (правила остались, файла блока нет), тоже остается: строгий режим просил
            // оставить сеть закрытой — вернуть его после паузы нечем, но и открывать нельзя.
            if (keepBlock && Status.State != ConnectionState.Off && (HoldStrictBlock() || _blocking))
            {
                if (Status.State == ConnectionState.Blocked)
                {
                    // И пауза для входа в Wi-Fi кончается: сеть снова закрыта.
                    EndPauseTimer();
                    Update(s => s with { StrictBlock = true, CaptivePauseUntil = null, Protection = ProtectionChecks.For(ConnectionState.Blocked) });
                }
                else
                {
                    Move(s => s, ConnectionState.Blocked, after: s => s with { StrictBlock = true });
                }

                // Защиту выключили вручную: после перезагрузки — блок, а не подключение.
                RememberBootConnection(null, []);
                _journal.Add(JournalLevel.Info, "service", "Строгий режим: защита выключена вручную, сеть закрыта до подключения");
                return;
            }

            ReleaseBlock();
            if (Status.State != ConnectionState.Off)
            {
                Move(s => s, ConnectionState.Off);
                _journal.Add(JournalLevel.Info, "service", "Защита выключена");
            }
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Пауза для входа в Wi-Fi: иначе страница входа в сеть не откроется. В «Защищено» режима VPN на это время
    /// блок при обрыве снимается и туннель останавливается, режима прокси — системный прокси возвращается к
    /// прямому подключению; по истечении защита возвращается сама. В «Блокировке» (строгий режим, блок с
    /// загрузки или с прошлого запуска, попытки кончились) блок снимается на время, а по истечении — тот же
    /// блок, если подключения нет (<see cref="ResumeAsync(int, string, CancellationToken)"/>). Паузы нет (null):
    /// в остальных состояниях, на паузе, без системного прокси в режиме прокси, без известного блока и если
    /// блок не снялся.
    /// </summary>
    public async Task<DateTimeOffset?> PauseAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = Status;
            if (duration <= TimeSpan.Zero || status.IsPaused)
            {
                return null;
            }

            duration = duration > MaxCaptivePause ? MaxCaptivePause : duration;
            string what;
            if (status.State is ConnectionState.Blocked or ConnectionState.Error && _blocking && _held is not null)
            {
                // Страница входа в сеть — мимо блока; блок помнит и перезагрузка (файл блока остается).
                if (!OpenBlock())
                {
                    return null;
                }

                what = status.StrictBlock ? "строгий режим — сеть открыта без VPN" : "блок снят, трафик идет напрямую";
            }
            else if (status.State == ConnectionState.Connected && _request is { Mode: ConnectionMode.Vpn })
            {
                // Блок снимается раньше туннеля: не снялся — туннель цел, «Защищено» остается правдой.
                if (!OpenBlock())
                {
                    return null;
                }

                await StopCoreAsync().ConfigureAwait(false);
                what = "трафик идет напрямую";
            }
            else if (status.State is ConnectionState.Connected or ConnectionState.Error && _systemProxy is { IsActive: true } systemProxy)
            {
                // В «Ошибке» режима прокси системный прокси ведет на остановленное ядро — страница входа тоже не открылась бы.
                systemProxy.Release();
                what = "трафик идет напрямую";
            }
            else
            {
                if (status.State == ConnectionState.Connected)
                {
                    _journal.Add(JournalLevel.Info, "service", "Пауза для входа в Wi-Fi ничего не меняет: системный прокси не включен");
                }

                return null;
            }

            var until = _time.GetUtcNow() + duration;
            var session = _session;
            Update(s => s with { CaptivePauseUntil = until, Protection = ProtectionChecks.Paused });
            lock (_gate)
            {
                _pauseEnd?.Dispose();
                _pauseEnd = _time.CreateTimer(_ => _ = ResumeAsync(session, "время вышло"), null, duration, Timeout.InfiniteTimeSpan);
            }

            _journal.Add(JournalLevel.Warning, "service", $"Пауза для входа в Wi-Fi на {duration.TotalMinutes:0.#} мин: {what}");
            return until;
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Снять блок на паузу, не забывая его: в файле блока — отметка паузы, служба, упавшая на паузе, вернет блок при
    /// запуске. Не снялся — паузы нет, это в журнале.
    /// </summary>
    private bool OpenBlock()
    {
        if (_held is { } held)
        {
            RememberBlock(held with { Paused = true });
        }

        ReleaseBlock(forget: false);
        if (_blocking)
        {
            _journal.Add(JournalLevel.Error, "service", "Пауза для входа в Wi-Fi не началась: блок при обрыве не снялся");
            if (_held is { } kept)
            {
                RememberBlock(kept);
            }
        }

        return !_blocking;
    }

    /// <summary>«Вернуть защиту» раньше конца паузы.</summary>
    public Task ResumeAsync(CancellationToken cancellationToken = default) => ResumeAsync(Volatile.Read(ref _session), "по кнопке", cancellationToken);

    /// <summary>Задержка через каждый сервер — запросом через ядро (<see cref="LatencyProbe"/>).</summary>
    public async Task<V1.MeasureLatencyReply> MeasureLatencyAsync(V1.MeasureLatencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = new V1.MeasureLatencyReply();
        var servers = new List<ServerDefinition>();
        var ids = new List<string>();
        foreach (var target in request.Targets)
        {
            try
            {
                servers.Add((target.Server ?? throw new ArgumentException("Нет сервера")).ToCore());
                ids.Add(target.Id);
            }
            catch (ArgumentException)
            {
                reply.Results.Add(LatencyResult.Failed(LatencyFailure.Unsupported).ToContract(target.Id));
            }
        }

        if (servers.Count == 0)
        {
            return reply;
        }

        IReadOnlyList<LatencyResult> results = _options.XrayPath is { } xray && File.Exists(xray)
            ? await LatencyProbe.MeasureAsync(xray, servers, ConnectionSettings.Default, _options.LatencyTarget, LatencyTimeout,
                _time, cancellationToken).ConfigureAwait(false)
            : [.. servers.Select(_ => LatencyResult.Failed(LatencyFailure.CoreFailed))];
        reply.Results.AddRange(results.Select((r, i) => r.ToContract(ids[i])));
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _pauseEnd?.Dispose();
        }

        // Переподключение и проверка связи останавливаются: подключения больше нет.
        Interlocked.Increment(ref _session);
        if (_options.Network is { } network)
        {
            network.Changed -= OnNetworkChanged;
        }

        await StopCoreAsync().ConfigureAwait(false);
        _systemProxy?.Release();

        // Остановка на паузе для входа в Wi-Fi: защита не выключалась — блок возвращается, как при любой остановке.
        if (Status.IsPaused && !_blocking && _held is { } held)
        {
            Restore(held, "возвращен: служба остановлена на паузе");
        }

        if (_blocking)
        {
            _journal.Add(JournalLevel.Warning, "service", "Служба остановлена, блок при обрыве оставлен: трафик заблокирован до выключения защиты");
        }

        _switch.Dispose();
    }

    /// <summary>
    /// Отказ: запрошенное подключение не состоится, а прежнее не остается работать молча — ядро
    /// останавливается, состояние «Ошибка» с кодом, его видят все подписчики. Из «Выключено» и
    /// «Ошибки» — через «Подключение», как любая попытка. Блок при загрузке и строгий режим сеть не открывают:
    /// «Блокировка» с кодом причины.
    /// </summary>
    private async Task<V1.ConnectReply> RejectAsync(V1.ConnectRequest request, ConnectionMode? mode, string code, string message,
        CancellationToken cancellationToken)
    {
        var serverId = request.Server?.Id;
        var strict = mode == ConnectionMode.Vpn && request.Protection?.StrictMode == true;
        _journal.Add(JournalLevel.Warning, "service", $"Подключение отклонено ({code}): {message}");
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session++;
            await StopCoreAsync().ConfigureAwait(false);
            ConnectionStatus Requested(ConnectionStatus s) => s with
            {
                Mode = mode ?? s.Mode,
                ServerId = serverId is { Length: > 0 } ? serverId : s.ServerId,
            };

            // При загрузке и в строгом режиме блок стоит до подключения или выключения защиты — отказ его не снимает,
            // а на паузе для входа в Wi-Fi возвращает. Строгий режим держит сеть закрытой и после отказа из «Защищено»
            // или «Ошибки»: «Блокировка» строгого режима.
            var status = Status;
            var held = status.State == ConnectionState.Blocked && (_holdBlock || status.StrictBlock)
                && (_blocking || (_held is { } block && Restore(block, "возвращен: подключение не состоялось")));
            if (held || (strict && status.State != ConnectionState.Off && HoldStrictBlock()))
            {
                // Новое время — окно видит новый отказ (и экран «Работает другой VPN»), даже если «Блокировка» та же.
                var now = _time.GetUtcNow();
                EndPauseTimer();
                if (Status.State == ConnectionState.Blocked)
                {
                    Update(s => Requested(s) with
                    {
                        ErrorCode = code, CaptivePauseUntil = null, Protection = ProtectionChecks.For(ConnectionState.Blocked), Since = now,
                        StrictBlock = s.StrictBlock || !held,
                    });
                }
                else
                {
                    Move(Requested, ConnectionState.Blocked, after: s => s with { ErrorCode = code, StrictBlock = true });
                }

                if (Status.StrictBlock)
                {
                    // Как ручное отключение в строгом режиме: после перезагрузки — блок, а не подключение.
                    RememberBootConnection(null, []);
                }

                _journal.Add(JournalLevel.Warning, "service", Status.StrictBlock
                    ? "Строгий режим: подключиться не удалось — сеть закрыта до подключения или выключения строгого режима"
                    : "Защита при загрузке: подключиться не удалось — трафик заблокирован до подключения или выключения защиты");
                return new V1.ConnectReply { Accepted = false, Error = new V1.ErrorInfo { Code = code, Message = message } };
            }

            ReleaseBlock();
            _systemProxy?.Release();
            _journal.Hide([]);

            if (!ConnectionStateMachine.CanMove(Status.State, ConnectionState.Error))
            {
                Move(Requested, ConnectionState.Connecting);
            }

            // Подключения нет и прежнее остановлено: защиты нет, трафик идет напрямую — так и говорить.
            Move(s => Requested(s) with { ErrorCode = code }, ConnectionState.Error,
                s => s with { Protection = ProtectionChecks.For(ConnectionState.Off) });
        }
        finally
        {
            _switch.Release();
        }

        return new V1.ConnectReply { Accepted = false, Error = new V1.ErrorInfo { Code = code, Message = message } };
    }

    /// <summary>Свободен ли порт входа прокси — на том адресе, где его откроет ядро.</summary>
    private static bool PortIsFree(int port, bool allowLan)
    {
        try
        {
            using var listener = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Готово, когда ядро открыло вход прокси; не открыло за время ожидания — ошибка ядра.</summary>
    private async Task WaitReadyAsync(int session, int port, TimeSpan timeout)
    {
        var started = _time.GetTimestamp();
        while (session == Volatile.Read(ref _session) && _time.GetElapsedTime(started) < timeout)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                await ConnectedAsync(session).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time).ConfigureAwait(false);
            }
        }

        Fail(session, ErrorCodes.CoreFailed, "Ядро не открыло вход прокси за " + timeout.TotalSeconds + " с");
    }

    /// <summary>
    /// Ядро вышло: при подключении — «Ошибка», из «Защищено» — переподключение. Ядро, которое служба
    /// уже заменила новым (переподключение), в состояние не вмешивается.
    /// </summary>
    private void OnCoreExited(int session, CoreProcess process)
    {
        if (Volatile.Read(ref _core) is { } current && !ReferenceEquals(current, process))
        {
            return;
        }

        var message = "Ядро остановилось, код " + (process.HasExited ? process.ExitCode : 0);
        if (Status.State == ConnectionState.Connected)
        {
            _ = LostAsync(session, ErrorCodes.CoreFailed, message);
            return;
        }

        Fail(session, ErrorCodes.CoreFailed, message);
    }

    /// <summary>
    /// Ядро открыло вход — системный прокси и «Защищено». Под тем же замком, что смена подключения:
    /// отключение посреди этого не оставит системный прокси включенным.
    /// </summary>
    private async Task ConnectedAsync(int session)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (session != _session || _status.State != ConnectionState.Connecting)
                {
                    return;
                }
            }

            // Сначала системный прокси: «Защищено» в окне — когда браузеры уже идут через прокси.
            ApplySystemProxy();
            Move(s => s, ConnectionState.Connected);
            _journal.Add(JournalLevel.Info, "service", "Подключено: прокси на 127.0.0.1 готов");
            AfterConnected(session);
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Ядро готово: в режиме прокси с системным прокси — включить его на вход ядра, иначе вернуть как был.
    /// Установленная служба работает не от имени пользователя и его настройки не трогает.
    /// </summary>
    private void ApplySystemProxy()
    {
        if (_request is not { Mode: ConnectionMode.Proxy, Connection.SystemProxy: true } request)
        {
            _systemProxy?.Release();
        }
        else if (_systemProxy is null)
        {
            _journal.Add(JournalLevel.Warning, "service",
                $"Системный прокси ставит только служба пользователя — направьте программы на 127.0.0.1:{request.Connection.ProxyPort}");
        }
        else
        {
            _systemProxy.Apply(request.Connection.ProxyPort);
        }
    }

    /// <summary>Ошибка текущего подключения; ядро старого подключения в состояние не вмешивается.</summary>
    private void Fail(int session, string code, string message)
    {
        lock (_gate)
        {
            if (session != _session || _status.State is not (ConnectionState.Connecting or ConnectionState.Connected))
            {
                return;
            }
        }

        _journal.Add(JournalLevel.Error, "service", $"{code}: {message}");
        Move(s => s with { ErrorCode = code }, ConnectionState.Error);
        _ = StopFailedAsync(session);
    }

    /// <summary>Ядро, не открывшее вход вовремя, может еще работать — его останавливает служба.</summary>
    private async Task StopFailedAsync(int session)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session == _session)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Конец паузы: защита и системный прокси — обратно; в режиме VPN — блок и туннель заново, через
    /// «Подключение»; в «Блокировке» без подключения — тот же блок. Подключение сменилось — ничего.
    /// </summary>
    private async Task ResumeAsync(int session, string why, CancellationToken cancellationToken = default)
    {
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = Status;
            if (session != _session || status.CaptivePauseUntil is null)
            {
                return;
            }

            // «Блокировка» без подключения (строгий режим, блок с загрузки или с прошлого запуска) и «Ошибка» под блоком —
            // тот же блок, без «Подключения» и ядер. Блок не вернулся — честная «Ошибка»: трафик идет напрямую.
            if ((status.State == ConnectionState.Blocked && (status.StrictBlock || _request is null))
                || (status.State == ConnectionState.Error && _held is not null))
            {
                EndPauseTimer();
                if (_held is { } held && Restore(held, "возвращен: пауза для входа в Wi-Fi закончилась"))
                {
                    Update(s => s with { CaptivePauseUntil = null, Protection = ProtectionChecks.For(s.State) });
                    _journal.Add(JournalLevel.Info, "service", $"Пауза для входа в Wi-Fi закончилась ({why}): сеть снова закрыта");
                    return;
                }

                _journal.Add(JournalLevel.Error, "service", $"{ErrorCodes.KillSwitchFailed}: после паузы для входа в Wi-Fi блок при обрыве не вернулся — трафик идет напрямую");
                if (status.State == ConnectionState.Error)
                {
                    Update(s => s with { ErrorCode = ErrorCodes.KillSwitchFailed, CaptivePauseUntil = null, Protection = ProtectionChecks.For(ConnectionState.Off) });
                }
                else
                {
                    Move(s => s with { ErrorCode = ErrorCodes.KillSwitchFailed }, ConnectionState.Error,
                        s => s with { Protection = ProtectionChecks.For(ConnectionState.Off) });
                }

                return;
            }

            if (_request is { Mode: ConnectionMode.Vpn } request)
            {
                // Новый номер: туннель поднимается заново, как при подключении; переход в «Подключение» заканчивает паузу.
                var next = ++_session;
                BeginConnecting(request.Mode, Status.ServerId ?? string.Empty);
                _journal.Add(JournalLevel.Info, "service", $"Пауза для входа в Wi-Fi закончилась ({why}): туннель поднимается снова");
                if (_killSwitchOn && !Block(request, _addresses))
                {
                    Fail(next, ErrorCodes.KillSwitchFailed, "Блок при обрыве не включился");
                    return;
                }

                _ = StartTunnelAsync(next, request, _addresses, _killSwitchOn);
                return;
            }

            ApplySystemProxy();
            Update(s => s with { CaptivePauseUntil = null, Protection = ProtectionChecks.For(s.State, s.Mode) });
            EndPauseTimer();

            _journal.Add(JournalLevel.Info, "service", $"Пауза для входа в Wi-Fi закончилась ({why}): защита снова включена");
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>Таймер конца паузы больше не нужен.</summary>
    private void EndPauseTimer()
    {
        lock (_gate)
        {
            _pauseEnd?.Dispose();
            _pauseEnd = null;
        }
    }

    /// <summary>Поменять состояние без перехода (пауза) и разослать подписчикам.</summary>
    private void Update(Func<ConnectionStatus, ConnectionStatus> change)
    {
        lock (_gate)
        {
            _status = change(_status);
            foreach (var watcher in _watchers)
            {
                watcher.TryWrite(_status);
            }
        }
    }

    /// <param name="change">Что поменять до перехода.</param>
    /// <param name="state">Новое состояние.</param>
    /// <param name="after">Что поменять после перехода — подписчики получат уже итог.</param>
    /// <remarks>Любой переход заканчивает паузу для входа в Wi-Fi.</remarks>
    private void Move(Func<ConnectionStatus, ConnectionStatus> change, ConnectionState state, Func<ConnectionStatus, ConnectionStatus>? after = null)
    {
        lock (_gate)
        {
            _pauseEnd?.Dispose();
            _pauseEnd = null;
            _status = Honest(change(_status).MoveTo(state, _time.GetUtcNow()));
            if (after is not null)
            {
                _status = after(_status);
            }

            foreach (var watcher in _watchers)
            {
                watcher.TryWrite(_status);
            }
        }
    }

    /// <summary>«Подключение» к серверу; уже подключается (повторное нажатие) — только новый сервер и режим.</summary>
    private void BeginConnecting(ConnectionMode mode, string serverId)
    {
        // IP прежнего сервера сайтам уже не виден; реальный — измерен перед подключением.
        var direct = _directIp;
        ExitInfo Exit(ConnectionStatus s) => ExitInfo.Unknown with { DirectIp = direct ?? s.Exit.DirectIp };
        if (Status.State == ConnectionState.Connecting)
        {
            Update(s => Honest(s with { Mode = mode, ServerId = serverId, Protection = ProtectionChecks.For(ConnectionState.Connecting, mode), Exit = Exit(s) }));
        }
        else
        {
            Move(s => s with { Mode = mode, ServerId = serverId, Exit = Exit(s) }, ConnectionState.Connecting);
        }
    }

    private async Task StopCoreAsync()
    {
        if (Interlocked.Exchange(ref _core, null) is { } core)
        {
            await core.StopAsync().ConfigureAwait(false);
            core.Dispose();
        }

        if (Interlocked.Exchange(ref _tunnel, null) is { } tunnel)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Туннель поднят — «Защищено»; не поднялся или ядро вышло — «Ошибка», блок остается. Пока туннель
    /// поднимался, подключение сменилось — поднятый туннель останавливается.
    /// </summary>
    private async Task StartTunnelAsync(int session, CoreRequest core, IReadOnlyList<IPAddress> addresses, bool killSwitchOn)
    {
        VpnTunnel tunnel;
        try
        {
            tunnel = await VpnTunnel.StartAsync(_options.XrayPath!, _options.SingBoxPath!, core, addresses,
                TimeSpan.FromSeconds(core.Connection.ConnectTimeoutSeconds), _time, _options.TunnelNetns,
                (source, line) => _journal.Add(JournalLevel.Info, source, line), _probe).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TunnelException or System.ComponentModel.Win32Exception or IOException)
        {
            Fail(session, ErrorCodes.CoreFailed, e.Message);
            return;
        }

        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            bool current;
            lock (_gate)
            {
                current = session == _session && _status.State == ConnectionState.Connecting;
            }

            if (!current)
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _tunnel = tunnel;
            tunnel.Exited += (_, _) => OnTunnelExited(session);
            _journal.Add(JournalLevel.Info, "service", $"Туннель поднят: Xray — процесс {tunnel.Processes.Xray}, sing-box — процесс {tunnel.Processes.SingBox}");
            if (tunnel.HasExited)
            {
                Fail(session, ErrorCodes.CoreFailed, "Ядро туннеля остановилось");
                return;
            }

            // Адаптер туннеля появился только сейчас: на Windows разрешение для него — по его LUID.
            if (killSwitchOn && !Block(core, addresses, "обновлен: адаптер туннеля открыт"))
            {
                Fail(session, ErrorCodes.KillSwitchFailed, "Блок при обрыве не обновился");
                return;
            }

            Move(s => s, ConnectionState.Connected);
            _journal.Add(JournalLevel.Info, "service", core.Routing.HasBypassApps || core.Routing.HasBypassSites
                ? "Подключено: трафик через туннель, обход раздельного режима — напрямую"
                : "Подключено: весь трафик через туннель");
            AfterConnected(session);
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Какие из найденных адресов сервера брать: IPv6 — только если он не закрыт; первым — адрес
    /// предпочтенного семейства (к первому соединяется Xray).
    /// </summary>
    public static IReadOnlyList<IPAddress> PickAddresses(IEnumerable<IPAddress> found, ProtectionSettings protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var ipv6 = protection.Ipv6Allowed;
        var preferV6 = protection.Addresses == AddressPreference.PreferIpv6;
        return [.. found
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (ipv6 && a.AddressFamily == AddressFamily.InterNetworkV6))
            .OrderBy(a => (a.AddressFamily == AddressFamily.InterNetworkV6) != preferV6)];
    }

    /// <summary>Адреса сервера для туннеля и блока: адрес в описании — как есть, имя — через систему. Не нашлось — пусто.</summary>
    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(CoreRequest core, CancellationToken cancellationToken)
    {
        var host = core.Server.Host.Trim('[', ']');
        IPAddress[] found;
        if (IPAddress.TryParse(host, out var literal))
        {
            found = [literal];
        }
        else
        {
            try
            {
                found = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return [];
            }
        }

        return PickAddresses(found, core.Protection);
    }

    /// <summary>
    /// Поставить (или заменить) блок при обрыве под это подключение и запомнить его в файле блока; <paramref name="strict"/> —
    /// блок строгого режима. Не вышло — false.
    /// </summary>
    private bool Block(CoreRequest core, IReadOnlyList<IPAddress> addresses, string done = "включен", bool strict = false)
    {
        var killSwitch = _options.KillSwitch!;

        // Мимо туннеля — еще прямой выход sing-box: программы раздельного режима и прямой выход Xray.
        var direct = VpnTunnel.DirectPrograms(core, _options.XrayPath!).Count > 0;
        var policy = new KillSwitchPolicy(
            SingBoxConfigBuilder.InterfaceName is { Length: > 0 } name ? name : null,
            [.. addresses.Select(a => new KillSwitchEndpoint(a, core.Server.Port))],
            core.Protection.AllowLan,
            core.Protection.LanDiscovery,
            core.Protection.BlockOutsideDns,
            direct ? new DirectExit(VpnTunnel.RealPath(_options.SingBoxPath!), SingBoxConfigBuilder.DirectMark) : null,
            core.Protection.BootProtection);
        if (!Try(() => { killSwitch.Enable(policy); return true; }, "Блок при обрыве не включился"))
        {
            return false;
        }

        _held = new HeldBlock(policy, core.Server.Host, core.Protection.BootProtection, strict);
        RememberBlock(_held);
        _blocking = true;
        _journal.Add(JournalLevel.Info, "service",
            $"Блок при обрыве {done} ({killSwitch.Name}): мимо туннеля — только сервер{(direct ? " и прямой выход sing-box" : string.Empty)}");
        return true;
    }

    /// <summary>Вернуть блок, который служба держит (после паузы, при остановке, в строгом режиме); не встал — false.</summary>
    private bool Restore(HeldBlock held, string done)
    {
        if (_options.KillSwitch is not { } killSwitch || !Try(() => { killSwitch.Enable(held.Policy); return true; }, "Блок при обрыве не включился"))
        {
            return false;
        }

        _held = held;
        RememberBlock(held);
        _blocking = true;
        _journal.Add(JournalLevel.Info, "service", $"Блок при обрыве {done} ({killSwitch.Name})");
        return true;
    }

    /// <summary>
    /// Строгий режим: блок без туннеля — под последнее подключение или тот, что служба держит с прошлого запуска; не
    /// встал или держать нечего — false.
    /// </summary>
    private bool HoldStrictBlock()
    {
        if (_request is { Mode: ConnectionMode.Vpn } request)
        {
            return _killSwitchOn && Block(request, _addresses, "оставлен: строгий режим", strict: true);
        }

        return _request is null && _held is { } held && Restore(held with { Strict = true }, "оставлен: строгий режим");
    }

    /// <summary>
    /// Адреса того же сервера, найденные раньше, — пока стоит блок, под которым имени не найти; другой сервер или блока
    /// нет — пусто.
    /// </summary>
    private IReadOnlyList<IPAddress> HeldAddresses(CoreRequest core) =>
        _blocking && _held is { Host.Length: > 0 } held && string.Equals(held.Host, core.Server.Host, StringComparison.OrdinalIgnoreCase)
            ? PickAddresses(held.Policy.Servers.Where(s => s.Port == core.Server.Port).Select(s => s.Address), core.Protection)
            : [];

    /// <summary>Блок с прошлого запуска или с загрузки: «Блокировка», строгого ли режима — по файлу блока.</summary>
    private void HoldAtStart(HeldBlock? held)
    {
        (_blocking, _held) = (true, held is null ? null : held with { Paused = false });
        _status = _status with
        {
            State = ConnectionState.Blocked,
            Protection = ProtectionChecks.For(ConnectionState.Blocked),
            StrictBlock = held is { Strict: true },
        };
    }

    /// <summary>
    /// Снять блок при обрыве, если он стоит; не снялся — остается и виден в проверках. Защиту выключили —
    /// служба забывает блок (и «Защиту при загрузке»); на паузе для входа в Wi-Fi (<paramref name="forget"/> = false) —
    /// помнит, чтобы вернуть его после паузы и после перезагрузки.
    /// </summary>
    private void ReleaseBlock(bool forget = true)
    {
        if (forget)
        {
            _held = null;
            RememberBlock(null);
        }

        if (!_blocking || _options.KillSwitch is not { } killSwitch)
        {
            return;
        }

        if (Try(() => { killSwitch.Disable(); return true; }, "Блок при обрыве не снялся"))
        {
            _blocking = false;
            _journal.Add(JournalLevel.Info, "service", "Блок при обрыве снят");
        }
    }

    /// <summary>
    /// Блок в файле блока (<see cref="Service.BootBlock"/>); null — забыть его. Без «Защиты при загрузке» служба
    /// забывает и подключение для нее. Не записалось — в журнал, подключение идет.
    /// </summary>
    private void RememberBlock(HeldBlock? block)
    {
        if (block is not { Boot: true })
        {
            RememberBootConnection(null, []);
        }

        if (_options.BootBlock is not { } path)
        {
            return;
        }

        try
        {
            if (block is null)
            {
                Service.BootBlock.Forget(path);
            }
            else
            {
                Service.BootBlock.Save(path, block);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Add(JournalLevel.Error, "service", "Блок при обрыве не записан в файл — после перезагрузки его не будет: " + e.Message);
        }
    }

    /// <summary>
    /// Подключение для «Защиты при загрузке» (<see cref="BootConnection"/>); null — забыть. Не записалось — в журнал:
    /// после перезагрузки будет только блок.
    /// </summary>
    private void RememberBootConnection(V1.ConnectRequest? request, IReadOnlyList<IPAddress> addresses)
    {
        if (_options.BootConnect is not { } boot)
        {
            return;
        }

        try
        {
            if (request is null)
            {
                boot.Forget();
            }
            else
            {
                boot.Save(request, addresses);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Add(JournalLevel.Error, "service", "«Защита при загрузке»: подключение не записано: " + e.Message);
        }
    }

    /// <summary>Вызов правил брандмауэра: ошибка — в журнал и false.</summary>
    private bool Try(Func<bool> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            _journal.Add(JournalLevel.Error, "service", $"{what}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Проверки без обещаний: в режиме VPN без блока при обрыве «Ошибка» — это трафик напрямую, а в
    /// остальных состояниях проверка блока — выключена. Раздельный режим с обходом — «Весь трафик»
    /// частично: выбранные программы и сайты идут мимо туннеля.
    /// </summary>
    private ConnectionStatus Honest(ConnectionStatus status)
    {
        if (status is { Mode: ConnectionMode.Vpn, State: ConnectionState.Connected } && _request?.Routing is { } routing
            && (routing.HasBypassApps || routing.HasBypassSites))
        {
            status = status with { Protection = status.Protection with { AllTraffic = CheckState.Warning } };
        }

        return status.Mode != ConnectionMode.Vpn || status.State == ConnectionState.Off || _blocking
            ? status
            : status.State is ConnectionState.Error or ConnectionState.Blocked
                ? status with { Protection = ProtectionChecks.For(ConnectionState.Off) }
                : status with { Protection = status.Protection with { KillSwitch = CheckState.Off } };
    }
}
