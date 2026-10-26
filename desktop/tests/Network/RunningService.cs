using Grpc.Net.Client;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using PrivateGateway.Client.Service.Ipc;
using PrivateGateway.Client.Service.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Служба в процессе теста на временном сокете (на Windows — pipe) и клиент по настоящему каналу.</summary>
internal sealed class RunningService : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
    private readonly GrpcChannel _channel;
    private readonly Gateway _gateway;
    private bool _stopped;

    private RunningService(string endpoint, Microsoft.AspNetCore.Builder.WebApplication app, Gateway gateway, GrpcChannel channel, ServiceJournal journal)
    {
        Endpoint = endpoint;
        _app = app;
        _gateway = gateway;
        _channel = channel;
        Journal = journal;
        Client = new V1.GatewayService.GatewayServiceClient(channel);
    }

    /// <summary>Адрес службы — для своих клиентов теста.</summary>
    public string Endpoint { get; }

    public V1.GatewayService.GatewayServiceClient Client { get; }

    public ServiceJournal Journal { get; }

    /// <summary>Сама служба — для того, что делает она сама, без окна (подключение при загрузке).</summary>
    public Gateway Gateway => _gateway;

    /// <param name="latencyTarget">Проверочный адрес задержки; null — настоящий.</param>
    /// <param name="endpoint">Адрес прежней службы — как служба, запущенная заново; null — новый.</param>
    /// <param name="systemProxy">Системный прокси (подделка) и файл его снимка; null — служба его не трогает.</param>
    /// <param name="options">Поправить возможности службы (режим VPN, блок при обрыве); null — служба без прав, только прокси.</param>
    /// <param name="time">Часы службы; null — настоящие.</param>
    /// <param name="updates">Проверка обновлений (journal службы → проверка); null — без ключей подписи.</param>
    /// <param name="installer">Установка обновлений; null — служба их не ставит.</param>
    public static async Task<RunningService> StartAsync(Uri? latencyTarget = null, string? endpoint = null, (ISystemProxy Proxy, string Backup)? systemProxy = null,
        Func<GatewayOptions, GatewayOptions>? options = null, TimeProvider? time = null, Func<ServiceJournal, UpdateChecker>? updates = null,
        Func<ServiceJournal, UpdateInstaller>? installer = null)
    {
        endpoint ??= OperatingSystem.IsWindows()
            ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "pg-ipc-" + Guid.NewGuid().ToString("N")[..8], "ipc.sock");
        var journal = new ServiceJournal(TimeProvider.System);
        var guard = systemProxy is var (proxy, backup) ? new SystemProxyGuard(proxy, backup, journal) : null;
        var defaults = new GatewayOptions(XrayKit.Xray, CanVpn: false, latencyTarget ?? LatencyProbe.DefaultTarget);
        var gateway = new Gateway(options?.Invoke(defaults) ?? defaults, time ?? TimeProvider.System, journal, guard);
        var app = IpcServer.Build(new IpcOptions(endpoint), gateway, journal, ServiceHost.Info(XrayKit.Xray, XrayKit.SingBox), updates?.Invoke(journal),
            installer?.Invoke(journal));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new RunningService(endpoint, app, gateway, IpcChannel.Create(endpoint, system: false), journal);
    }

    /// <summary>Остановить службу, как при ее завершении; повторный вызов ничего не делает.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _channel.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        await _gateway.DisposeAsync();
    }
}
