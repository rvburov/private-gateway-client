using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Tests.Unit;

public class ConnectionControllerTests
{
    private static readonly ServerProfile Nl =
        new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    private static readonly ServerProfile Fi =
        new("fi", "Финляндия 01", "FI", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.31", 443, "tcp", 38);

    [Theory]
    [InlineData(ConnectionState.Off)]
    [InlineData(ConnectionState.Error)]
    public async Task Toggle_when_off_or_broken_connects_selected_server(ConnectionState state)
    {
        var gateway = new FakeGateway(state);
        var controller = new ConnectionController(gateway, new FakeCatalog(Nl, Fi) { SelectedId = "nl" });

        (await controller.ToggleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        gateway.Calls.ShouldBe(["connect:nl"]);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Blocked)]
    public async Task Toggle_when_protection_is_on_disconnects(ConnectionState state)
    {
        var gateway = new FakeGateway(state);
        var controller = new ConnectionController(gateway, new FakeCatalog(Nl) { SelectedId = "nl" });

        (await controller.ToggleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        gateway.Calls.ShouldBe(["disconnect"]);
    }

    [Fact]
    public async Task Toggle_without_selected_server_does_nothing_and_says_so()
    {
        var gateway = new FakeGateway(ConnectionState.Off);
        var controller = new ConnectionController(gateway, new FakeCatalog());

        (await controller.ToggleAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_while_protected_reconnects_to_the_new_server()
    {
        var gateway = new FakeGateway(ConnectionState.Connected);
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).SwitchServerAsync("fi", TestContext.Current.CancellationToken);

        catalog.SelectedId.ShouldBe("fi");
        gateway.Calls.ShouldBe(["connect:fi"]);
    }

    [Fact]
    public async Task Switch_while_off_only_selects_without_connecting()
    {
        var gateway = new FakeGateway(ConnectionState.Off);
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).SwitchServerAsync("fi", TestContext.Current.CancellationToken);

        catalog.SelectedId.ShouldBe("fi");
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_to_the_same_server_changes_nothing()
    {
        var gateway = new FakeGateway(ConnectionState.Connected);
        var catalog = new FakeCatalog(Nl) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).SwitchServerAsync("nl", TestContext.Current.CancellationToken);

        gateway.Calls.ShouldBeEmpty();
    }

    private sealed class FakeGateway(ConnectionState state) : IGatewayClient
    {
        public List<string> Calls { get; } = [];

        public ConnectionStatus Status { get; } =
            ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with { State = state };

        public bool IsSimulated => true;

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
        {
            Calls.Add("connect:" + server.Id);
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("disconnect");
            return Task.CompletedTask;
        }

        public Task RunChecksAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCatalog(params ServerProfile[] servers) : IServerCatalog
    {
        public string? SelectedId { get; set; }

        public IReadOnlyList<ServerProfile> Servers => servers;

        public ServerProfile? Selected => servers.FirstOrDefault(s => s.Id == SelectedId);

        public event EventHandler? Changed;

        public void Choose(string serverId)
        {
            SelectedId = serverId;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
