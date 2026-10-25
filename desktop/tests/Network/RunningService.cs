using Grpc.Net.Client;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using PrivateGateway.Client.Service.Ipc;
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

    /// <param name="latencyTarget">Проверочный адрес задержки; null — настоящий.</param>
    /// <param name="endpoint">Адрес прежней службы — как служба, запущенная заново; null — новый.</param>
    public static async Task<RunningService> StartAsync(Uri? latencyTarget = null, string? endpoint = null)
    {
        endpoint ??= OperatingSystem.IsWindows()
            ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "pg-ipc-" + Guid.NewGuid().ToString("N")[..8], "ipc.sock");
        var journal = new ServiceJournal(TimeProvider.System);
        var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, CanVpn: false, latencyTarget ?? LatencyProbe.DefaultTarget), TimeProvider.System, journal);
        var app = IpcServer.Build(new IpcOptions(endpoint), gateway, journal, ServiceHost.Info(XrayKit.Xray));
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
