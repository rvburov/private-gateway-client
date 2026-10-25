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

// --user — служба пользователя без прав (только режим прокси), адрес IPC — в папке пользователя;
// --parent <pid> — окно, которое ее запустило: закроется окно — остановится и служба;
// --ipc <адрес>, --xray <путь>, --sing-box <путь>, --state <папка> — для разработки и тестов; --owner <uid> — кого пускать (ставит установщик).
var user = args.Contains("--user");
var endpoint = Option("--ipc") ?? (user ? IpcEndpoints.User : IpcEndpoints.System);
var owner = uint.TryParse(Option("--owner"), out var uid) ? uid : (uint?)null;
var xray = Option("--xray") ?? ServiceHost.DefaultXray();
var singBox = Option("--sing-box") ?? ServiceHost.DefaultSingBox(AppContext.BaseDirectory);
var time = TimeProvider.System;
var journal = new ServiceJournal(time);

// Системный прокси — настройки пользователя: их меняет только служба пользователя. Снимок остался после сбоя — вернуть сразу.
var systemProxy = user && ServiceHost.SystemProxy() is { } proxy
    ? new SystemProxyGuard(proxy, UserServiceFiles.ProxyBackup(Option("--state") ?? UserServiceFiles.Folder), journal)
    : null;
systemProxy?.RestoreStale();
await using var gateway = new Gateway(
    new GatewayOptions(xray, CanVpn: !user && Environment.IsPrivilegedProcess, LatencyProbe.DefaultTarget), time, journal, systemProxy);
await using var app = IpcServer.Build(new IpcOptions(endpoint, owner), gateway, journal, ServiceHost.Info(xray, singBox));
journal.Add(JournalLevel.Info, "service", $"Служба {ServiceVersion.Current} слушает IPC" + (user ? " (пользователь, только прокси)" : string.Empty));
if (int.TryParse(Option("--parent"), out var parent))
{
    _ = ServiceHost.StopWithParentAsync(parent, () =>
    {
        journal.Add(JournalLevel.Info, "service", "Окно закрылось — служба пользователя останавливается");
        return app.StopAsync();
    });
}

await app.RunAsync().ConfigureAwait(false);
return 0;

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
