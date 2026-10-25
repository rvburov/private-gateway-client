using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using PrivateGateway.Client.Service.Ipc;

if (args.Contains("--version"))
{
    Console.WriteLine($"Private Gateway Service {ServiceVersion.Current} (IPC v{ContractMapping.ApiVersion})");
    return 0;
}

// --install --owner <SID или uid> — поставить службу (от администратора или root): копия в защищенную папку и запуск с ОС;
// --uninstall — удалить ее и снять блок при обрыве. --service-name, --install-dir — для тестов.
if (args.Contains("--install") || args.Contains("--uninstall"))
{
    var name = Option("--service-name") ?? ServiceSetup.DefaultName;
    var folder = Option("--install-dir") ?? ServiceSetup.DefaultFolder;
    try
    {
        if (args.Contains("--uninstall"))
        {
            ServiceSetup.Uninstall(name, folder, ServiceHost.KillSwitch());
            Console.WriteLine($"Служба {name} удалена");
            return 0;
        }

        ServiceSetup.Install(new SetupOptions(name, folder, Option("--owner") ?? string.Empty, Option("--ipc")), AppContext.BaseDirectory,
            Option("--xray") ?? ServiceHost.DefaultXray(), Option("--sing-box") ?? ServiceHost.DefaultSingBox(AppContext.BaseDirectory));
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
// --ipc <адрес>, --xray <путь>, --sing-box <путь>, --state <папка> — для разработки и тестов;
// --owner <uid или SID> — кого пускать, --service-name — имя службы Windows (их ставит установка).
var user = args.Contains("--user");
var endpoint = Option("--ipc") ?? (user ? IpcEndpoints.User : IpcEndpoints.System);
var owner = uint.TryParse(Option("--owner"), out var uid) ? uid : (uint?)null;
var ownerSid = Option("--owner") is { } sid && sid.StartsWith("S-", StringComparison.Ordinal) && ServiceSetup.IsValidOwner(sid) ? sid : null;
var xray = Option("--xray") ?? ServiceHost.DefaultXray();
var singBox = Option("--sing-box") ?? ServiceHost.DefaultSingBox(AppContext.BaseDirectory);
var time = TimeProvider.System;
var journal = new ServiceJournal(time);

// Ядра, которые пережили прошлый запуск службы (служба пользователя на Linux и macOS после сбоя), — снять:
// они держат порт прокси и ходят через сервер. Папка — та же, что у снимка прокси или «Защиты при загрузке».
var ledger = new CoreLedger(CoreLedger.In(Option("--state") ?? (user ? UserServiceFiles.Folder : AppContext.BaseDirectory)));
foreach (var pid in ledger.KillLeftovers())
{
    journal.Add(JournalLevel.Warning, "service", $"Ядро прошлого запуска пережило службу (процесс {pid}) — остановлено");
}

CoreProcess.Ledger = ledger;

// Системный прокси — настройки пользователя: их меняет только служба пользователя. Снимок остался после сбоя — вернуть сразу.
var systemProxy = user && ServiceHost.SystemProxy() is { } proxy
    ? new SystemProxyGuard(proxy, UserServiceFiles.ProxyBackup(Option("--state") ?? UserServiceFiles.Folder), journal)
    : null;
systemProxy?.RestoreStale();
var canVpn = !user && Environment.IsPrivilegedProcess;
using var network = new SystemNetworkEvents(time);

// «Защита при загрузке» — в папке установленной службы: туда пишут только администраторы и root. Последнее
// подключение — там же, зашифрованное хранилищем ОС; нет хранилища — при загрузке только блок.
var bootFolder = Option("--state") ?? AppContext.BaseDirectory;
var bootBlock = canVpn ? Path.Combine(bootFolder, "boot-block.json") : null;
var bootConnect = canVpn && ServiceHost.Sealer() is { } sealer ? new BootConnection(Path.Combine(bootFolder, "boot-connect.sealed"), sealer) : null;
await using var gateway = new Gateway(
    new GatewayOptions(xray, canVpn, LatencyProbe.DefaultTarget, singBox, canVpn ? ServiceHost.KillSwitch() : null, Routes: ServiceHost.Routes(),
        Reconnect: ReconnectTiming.Default, Network: network, BootBlock: bootBlock, Trace: LatencyProbe.DefaultTrace, BootConnect: bootConnect),
    time, journal, systemProxy);
await using var app = IpcServer.Build(new IpcOptions(endpoint, owner, ownerSid, Option("--service-name")), gateway, journal, ServiceHost.Info(xray, singBox));
journal.Add(JournalLevel.Info, "service", $"Служба {ServiceVersion.Current} слушает IPC" + (user ? " (пользователь, только прокси)" : string.Empty));
if (int.TryParse(Option("--parent"), out var parent))
{
    _ = ServiceHost.StopWithParentAsync(parent, () =>
    {
        journal.Add(JournalLevel.Info, "service", "Окно закрылось — служба пользователя останавливается");
        return app.StopAsync();
    });
}

// Защита была включена, когда машина выключилась: подключиться самой, не дожидаясь входа пользователя.
_ = gateway.ConnectAtBootAsync();
await app.RunAsync().ConfigureAwait(false);
return 0;

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
