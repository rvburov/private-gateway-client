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

    [Fact]
    public async Task Connect_from_card_selects_the_server_and_turns_protection_on()
    {
        var gateway = new FakeGateway(ConnectionState.Off);
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).ConnectToAsync("fi", TestContext.Current.CancellationToken);

        catalog.SelectedId.ShouldBe("fi");
        gateway.Calls.ShouldBe(["connect:fi"]);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    public async Task Connect_from_card_to_the_server_in_use_does_nothing(ConnectionState state)
    {
        var gateway = new FakeGateway(state, "nl");
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).ConnectToAsync("nl", TestContext.Current.CancellationToken);

        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_from_card_after_an_error_retries_the_same_server()
    {
        var gateway = new FakeGateway(ConnectionState.Error, "nl");
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).ConnectToAsync("nl", TestContext.Current.CancellationToken);

        gateway.Calls.ShouldBe(["connect:nl"]);
    }

    [Fact]
    public async Task Removing_the_server_in_use_disconnects_and_leaves_nothing_selected()
    {
        var gateway = new FakeGateway(ConnectionState.Connected, "nl");
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).RemoveServerAsync("nl", TestContext.Current.CancellationToken);

        gateway.Calls.ShouldBe(["disconnect"], "на другой сервер сами не переходим");
        catalog.Removed.ShouldBe(["nl"]);
        catalog.SelectedId.ShouldBeNull();
    }

    [Fact]
    public async Task Removing_another_server_keeps_protection()
    {
        var gateway = new FakeGateway(ConnectionState.Connected, "nl");
        var catalog = new FakeCatalog(Nl, Fi) { SelectedId = "nl" };

        await new ConnectionController(gateway, catalog).RemoveServerAsync("fi", TestContext.Current.CancellationToken);

        gateway.Calls.ShouldBeEmpty();
        catalog.SelectedId.ShouldBe("nl");
    }

    [Theory]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Blocked, true)]
    [InlineData(ConnectionState.Off, false)]
    public async Task Removing_a_group_disconnects_only_if_protection_goes_through_it(ConnectionState state, bool disconnects)
    {
        var gateway = new FakeGateway(state, "fi");
        var controller = new ConnectionController(gateway, new FakeCatalog());

        await controller.ReleaseAsync(["nl", "de"], TestContext.Current.CancellationToken);
        gateway.Calls.ShouldBeEmpty("защита идет через сервер не из этой группы");

        await controller.ReleaseAsync(["nl", "fi"], TestContext.Current.CancellationToken);
        gateway.Calls.ShouldBe(disconnects ? ["disconnect"] : []);
    }

    [Fact]
    public async Task Shield_during_captive_pause_returns_protection_instead_of_disconnecting()
    {
        var gateway = new FakeGateway(ConnectionState.Connected, "nl", pausedUntil: DateTimeOffset.UnixEpoch.AddMinutes(5));
        var controller = new ConnectionController(gateway, new FakeCatalog(Nl) { SelectedId = "nl" });

        (await controller.ToggleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        gateway.Calls.ShouldBe(["resume"]);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Blocked, true)]
    [InlineData(ConnectionState.Off, false)]
    public async Task Captive_pause_is_asked_only_while_protection_is_on(ConnectionState state, bool paused)
    {
        var gateway = new FakeGateway(state, "nl");

        (await new ConnectionController(gateway, new FakeCatalog()).PauseForCaptivePortalAsync(TestContext.Current.CancellationToken))
            .ShouldBe(paused);

        gateway.Calls.ShouldBe(paused ? ["pause:5"] : []);
    }

    internal sealed class FakeGateway(ConnectionState state, string? serverId = null, DateTimeOffset? pausedUntil = null) : IGatewayClient
    {
        public List<string> Calls { get; } = [];

        public ConnectionStatus Status { get; } =
            ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with { State = state, ServerId = serverId, CaptivePauseUntil = pausedUntil };

        public ServiceKind Service => ServiceKind.Simulated;

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

        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Calls.Add("pause:" + duration.TotalMinutes);
            return Task.FromResult(true);
        }

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("resume");
            return Task.CompletedTask;
        }


        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));

        public IReadOnlyList<JournalEntry> Journal => [];

        // Тесты контроллера журнал не читают.
        public event EventHandler<JournalEntry>? JournalAdded
        {
            add { }
            remove { }
        }
    }

    internal sealed class FakeCatalog(params ServerProfile[] servers) : IServerCatalog
    {
        public string? SelectedId { get; set; }

        public IReadOnlyList<ServerProfile> Servers => servers;

        public IReadOnlyList<ServerSubscription> Subscriptions => [];

        public ServerProfile? Selected => servers.FirstOrDefault(s => s.Id == SelectedId);

        public event EventHandler? Changed;

        public void Choose(string serverId)
        {
            SelectedId = serverId;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public List<string> Removed { get; } = [];

        public void SetFavorite(string serverId, bool isFavorite)
        {
        }

        public void Rename(string serverId, string name)
        {
        }

        public void Remove(string serverId)
        {
            Removed.Add(serverId);
            if (SelectedId == serverId)
            {
                SelectedId = null;
            }
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
