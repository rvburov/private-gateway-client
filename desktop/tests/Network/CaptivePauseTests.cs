using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Пауза для входа в Wi-Fi в режиме прокси: служба на время паузы возвращает прямые настройки
/// системного прокси (подделка) и включает его обратно сама или по «Вернуть защиту».
/// </summary>
public sealed class CaptivePauseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-pause-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Pause_gives_direct_settings_back_and_ends_by_itself()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var proxy = new FakeSystemProxy();
        await using var connected = await ConnectedAsync(proxy, systemProxy: true);
        var (service, watch, port) = connected;

        var reply = await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)) },
            cancellationToken: Ct);

        reply.Until.ShouldNotBeNull();
        var paused = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null))[^1].ToCore();
        (paused.State, paused.IsPaused, paused.Protection).ShouldBe((ConnectionState.Connected, true, ProtectionChecks.Paused));
        proxy.Port.ShouldBeNull("страница входа в сеть открывается напрямую");

        var resumed = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null))[^1].ToCore();

        resumed.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Connected, ConnectionMode.Proxy));
        proxy.Port.ShouldBe(port, "пауза кончилась — системный прокси снова на ядре");
    }

    [Fact]
    public async Task Return_now_ends_the_pause_and_disconnect_cancels_it()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var proxy = new FakeSystemProxy();
        await using var connected = await ConnectedAsync(proxy, systemProxy: true);
        var (service, watch, port) = connected;
        var pause = new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromHours(1)) };

        var reply = await service.Client.PauseForCaptivePortalAsync(pause, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);

        (reply.Until.ToDateTimeOffset() - DateTimeOffset.UtcNow).ShouldBeLessThanOrEqualTo(Gateway.MaxCaptivePause, "дольше 15 минут паузы не бывает");
        await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Cancel = true }, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is null);
        proxy.Port.ShouldBe(port);
        (await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest(), cancellationToken: Ct)).Until
            .ShouldBeNull("пауза без срока — не пауза");

        await service.Client.PauseForCaptivePortalAsync(pause, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.CaptivePauseUntil is not null);
        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        var off = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off))[^1].ToCore();
        (off.IsPaused, off.CaptivePauseUntil, proxy.Port).ShouldBe((false, null, null), "отключение заканчивает и паузу");
    }

    [Fact]
    public async Task Pause_without_a_system_proxy_changes_nothing()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var proxy = new FakeSystemProxy();
        await using var connected = await ConnectedAsync(proxy, systemProxy: false);
        var service = connected.Service;

        var reply = await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) },
            cancellationToken: Ct);

        reply.Until.ShouldBeNull("программы с прокси ходят через него и дальше — делать вид паузы нельзя");
        (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).CaptivePauseUntil.ShouldBeNull();
        service.Journal.Tail(1).ShouldHaveSingleItem().Message.ShouldContain("ничего не меняет");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Window_learns_whether_the_pause_began(bool systemProxy, bool began)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var connected = await ConnectedAsync(new FakeSystemProxy(), systemProxy);
        using var window = new ServiceGatewayClient(IpcChannel.Create(connected.Service.Endpoint, system: false), ServiceKind.User,
            _ => null, () => AppSettings.Default, TimeProvider.System);

        (await window.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), Ct)).ShouldBe(began);
    }

    [Fact]
    public async Task Pause_while_off_does_nothing()
    {
        await using var service = await RunningService.StartAsync(systemProxy: (new FakeSystemProxy(), Path.Combine(_root, "system-proxy.json")));

        var reply = await service.Client.PauseForCaptivePortalAsync(new V1.CaptivePauseRequest { Duration = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)) },
            cancellationToken: Ct);

        reply.Until.ShouldBeNull();
    }

    /// <summary>Служба с подделкой системного прокси, подключенная в режиме прокси к своему серверу Xray.</summary>
    private async Task<Connected> ConnectedAsync(FakeSystemProxy proxy, bool systemProxy)
    {
        var service = await RunningService.StartAsync(systemProxy: (proxy, Path.Combine(_root, "system-proxy.json")));
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        var server = await XrayKit.Start(serverConfig, serverPort);
        var port = XrayKit.FreePort();
        var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, port, systemProxy), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        return new Connected(service, watch, port, server);
    }

    private sealed record Connected(RunningService Service, Grpc.Core.AsyncServerStreamingCall<V1.Status> Watch, int Port, IDisposable Server)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Watch.Dispose();
            await Service.DisposeAsync();
            Server.Dispose();
        }

        public void Deconstruct(out RunningService service, out Grpc.Core.AsyncServerStreamingCall<V1.Status> watch, out int port) =>
            (service, watch, port) = (Service, Watch, Port);
    }
}
