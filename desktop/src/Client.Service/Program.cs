using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using PrivateGateway.Client.Service.Ipc;
using PrivateGateway.Client.Service.Updates;

if (args.Contains("--version"))
{
    Console.WriteLine($"Private Gateway Service {ServiceVersion.Current} (IPC v{ContractMapping.ApiVersion})");
    return 0;
}

// --install --owner <SID или uid> — поставить службу (от администратора или root): копия в защищенную папку и запуск с ОС,
// поверх работающей — атомарно, с возвратом прежней, если новая не запустилась; --uninstall — удалить ее, ее состояние и
// снять блок при обрыве. --service-name, --install-dir, --state-dir — для тестов.
if (args.Contains("--install") || args.Contains("--uninstall"))
{
    var name = Option("--service-name") ?? ServiceSetup.DefaultName;
    var folder = Option("--install-dir") ?? ServiceSetup.DefaultFolder;
    var stateDir = Option("--state-dir") ?? ServiceSetup.DefaultStateFolder;
    try
    {
        if (args.Contains("--uninstall"))
        {
            // DNS ОС, направленный на туннель, и системный прокси машины (macOS), оставленные упавшей службой, — вернуть до
            // удаления их копий вместе с состоянием.
            ServiceHost.SystemDns(stateDir)?.Restore();
            if (ServiceHost.SystemProxy(user: false) is { } machineProxy)
            {
                new SystemProxyGuard(machineProxy, UserServiceFiles.ProxyBackup(stateDir), new ServiceJournal(TimeProvider.System)).RestoreStale();
            }

            ServiceSetup.Uninstall(name, folder, stateDir, ServiceHost.KillSwitch(), ServiceSetup.ForThisMachine());

            // Ключ подключения при загрузке в системной Связке ключей (macOS) — вместе с зашифрованным им файлом, когда
            // служба уже остановлена и нового не зашифрует.
            if (ServiceHost.Sealer() is PrivateGateway.Client.Platform.MacOS.KeychainSealer keychain)
            {
                keychain.Forget();
            }

            Console.WriteLine($"Служба {name} удалена");
            return 0;
        }

        ServiceSetup.Install(new SetupOptions(name, folder, Option("--owner") ?? string.Empty, Option("--ipc"), stateDir), AppContext.BaseDirectory,
            Option("--xray") ?? ServiceHost.DefaultXray(), Option("--sing-box") ?? ServiceHost.DefaultSingBox(AppContext.BaseDirectory),
            ServiceSetup.ForThisMachine());
        Console.WriteLine($"Служба {name} установлена в {folder} и запущена");
        return 0;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException
        or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine((args.Contains("--uninstall") ? "Служба не удалена: " : "Служба не установлена: ") + e.Message);
        return 1;
    }
}

// --user — служба пользователя без прав (только режим прокси), адрес IPC — в папке пользователя;
// --parent <pid> — окно, которое ее запустило: закроется окно — остановится и служба;
// --ipc <адрес>, --xray <путь>, --sing-box <путь>, --state <папка>, --latency-target <адрес> (проверочный адрес задержки и
// проб сервера) — для разработки и тестов;
// --owner <uid или SID> — кого пускать, --service-name — имя службы Windows (их ставит установка).
var user = args.Contains("--user");
var endpoint = Option("--ipc") ?? (user ? IpcEndpoints.User : IpcEndpoints.System);
var owner = uint.TryParse(Option("--owner"), out var uid) ? uid : (uint?)null;
var ownerSid = Option("--owner") is { } sid && sid.StartsWith("S-", StringComparison.Ordinal) && ServiceSetup.IsValidOwner(sid) ? sid : null;
var xray = Option("--xray") ?? ServiceHost.DefaultXray();
var singBox = Option("--sing-box") ?? ServiceHost.DefaultSingBox(AppContext.BaseDirectory);
var time = TimeProvider.System;

// Одна копия на папку состояния — раньше всего остального: вторая сняла бы ядра первой и правила ее туннеля.
var state = Option("--state") is { Length: > 0 } given ? given : user ? UserServiceFiles.Folder : AppContext.BaseDirectory;
using var instance = ServiceLock.TryAcquire(state, out var busy);
if (busy)
{
    await Console.Error.WriteLineAsync($"{ErrorCodes.ServiceRunning}: служба уже работает (папка состояния {state})").ConfigureAwait(false);
    return 2;
}

// Журнал — и на диске, в папке состояния: окно видит, что было до аварии или перезагрузки. Признак работы остался — прошлый
// запуск завершился аварийно.
using var journalFile = new JournalFile(state);
var journal = new ServiceJournal(time, journalFile);
journal.Preload(journalFile.Tail(ServiceJournal.Capacity));
using var running = new RunMarker(state);
var crashed = running.Start();
if (crashed)
{
    journal.Add(JournalLevel.Warning, "service", "Прошлый запуск службы завершился аварийно — восстановление");
}

// Ядра, которые пережили прошлый запуск службы (служба пользователя на Linux и macOS после сбоя), — снять:
// они держат порт прокси и ходят через сервер. Папка — та же, что у снимка прокси или «Защиты при загрузке».
var ledger = new CoreLedger(CoreLedger.In(state));
var leftovers = ledger.KillLeftovers();
foreach (var pid in leftovers)
{
    journal.Add(JournalLevel.Warning, "service", $"Ядро прошлого запуска пережило службу (процесс {pid}) — остановлено");
}

CoreProcess.Ledger = ledger;

// Системный прокси меняет та служба, что вправе (Windows и Linux — служба пользователя, macOS — установленная). Снимок
// остался после сбоя — вернуть сразу.
var systemProxy = ServiceHost.SystemProxy(user) is { } proxy
    ? new SystemProxyGuard(proxy, UserServiceFiles.ProxyBackup(state), journal)
    : null;
var proxyRestored = systemProxy?.RestoreStale() == true;
var canVpn = !user && Environment.IsPrivilegedProcess;
using var network = new SystemNetworkEvents(time, sleep: ServiceHost.SuspendClock());

// «Защита при загрузке» — в папке установленной службы: туда пишут только администраторы и root. Последнее
// подключение — там же, зашифрованное хранилищем ОС; нет хранилища — при загрузке только блок.
var bootFolder = state;
var bootBlock = canVpn ? Path.Combine(bootFolder, "boot-block.json") : null;
var bootConnect = canVpn && ServiceHost.Sealer() is { } sealer ? new BootConnection(Path.Combine(bootFolder, "boot-connect.sealed"), sealer) : null;

// DNS ОС, направленный на прошлый туннель (macOS), — после аварии службы вернуть, как было: туннеля сейчас нет.
var systemDns = canVpn ? ServiceHost.SystemDns(state) : null;
try
{
    systemDns?.Restore();
}
catch (IOException e)
{
    journal.Add(JournalLevel.Warning, "service", "DNS ОС после прошлого туннеля не возвращен: " + e.Message);
}

await using var gateway = new Gateway(
    new GatewayOptions(xray, canVpn, Uri.TryCreate(Option("--latency-target"), UriKind.Absolute, out var target) ? target : LatencyProbe.DefaultTarget, singBox, canVpn ? ServiceHost.KillSwitch() : null, Routes: ServiceHost.Routes(),
        Reconnect: ReconnectTiming.Default, Network: network, BootBlock: bootBlock, Trace: LatencyProbe.DefaultTrace, BootConnect: bootConnect,
        Trace6: LatencyProbe.DefaultTrace6, Doh: LatencyProbe.DefaultDoh, OutsideDns: DnsProbe.DefaultResolver,
        TunDevice: OperatingSystem.IsLinux() ? "/dev/net/tun" : null, SystemDns: systemDns),
    time, journal, systemProxy);
// Обновления: выпуски на GitHub, подпись манифеста — ключами, встроенными в службу; номер раскатки и прежняя версия (для
// отката) — в папке состояния. Ставит обновление только установленная служба (у нее есть --service-name) программы из
// установщика.
var updateKind = UpdateInstaller.ForThisMachine(installedService: !user && Option("--service-name") is not null, ownerSid);
using var updateHttp = GitHubReleaseFeed.CreateHttp();
using var updates = new UpdateChecker(new GitHubReleaseFeed(updateHttp, GitHubReleaseFeed.DefaultReleases, GitHubReleaseFeed.DefaultDownloads),
    UpdateChecker.EmbeddedKeys(), ServiceVersion.Current, UpdateChecker.LoadBucket(state), time, journal, VersionHistory.Record(state, ServiceVersion.Current),
    updateKind?.Asset);
var installer = new UpdateInstaller(updateHttp, GitHubReleaseFeed.DefaultDownloads, Path.Combine(state, "updates"), updateKind, journal);
await using var app = IpcServer.Build(new IpcOptions(endpoint, owner, ownerSid, Option("--service-name")), gateway, journal, ServiceHost.Info(xray, singBox), updates,
    installer);
journal.Add(JournalLevel.Info, "service", $"Служба {ServiceVersion.Current} слушает IPC" + (user ? " (пользователь, только прокси)" : string.Empty));
if (crashed)
{
    // Сводка: что после аварии найдено и сделано — подробности строками выше.
    journal.Add(JournalLevel.Info, "recovery", $"Восстановление: ядер прошлого запуска снято — {leftovers.Count}; системный прокси " +
        (proxyRestored ? "возвращен, как был" : "не оставался") + "; " +
        (gateway.Status.State == ConnectionState.Blocked ? "блок при обрыве держит трафик до подключения или отключения" : "блока при обрыве нет"));
}
if (int.TryParse(Option("--parent"), out var parent))
{
    _ = ServiceHost.StopWithParentAsync(parent, () =>
    {
        journal.Add(JournalLevel.Info, "service", "Окно закрылось — служба пользователя останавливается");
        return app.StopAsync();
    });
}

// Готовность — для установки, которая ждет новую копию (ServiceReady): IPC слушает.
app.Lifetime.ApplicationStarted.Register(() => ServiceReady.Mark(state, ServiceVersion.Current));
app.Lifetime.ApplicationStopping.Register(() => ServiceReady.Clear(state));

// Защита была включена, когда машина выключилась: подключиться самой, не дожидаясь входа пользователя.
_ = gateway.ConnectAtBootAsync();
await app.RunAsync().ConfigureAwait(false);
return 0;

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
