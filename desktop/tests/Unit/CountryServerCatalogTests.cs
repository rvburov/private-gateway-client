using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class CountryServerCatalogTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly CountryGateway _gateway = new();

    [Fact]
    public async Task Literal_ip_overrides_subscription_emoji_without_dns_or_changing_the_saved_profile()
    {
        var server = Server("1", "192.0.2.7");
        var inner = new ConnectionControllerTests.FakeCatalog(server) { SelectedId = "1" };
        using var catalog = new CountryServerCatalog(inner, _gateway, _time, _ => "NL");
        catalog.Servers.Single().CountryCode.ShouldBe("NL");
        catalog.Selected!.CountryCode.ShouldBe("NL");
        inner.Selected!.CountryCode.ShouldBe("JP");
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        _gateway.Asked.ShouldBeEmpty();
        using var unknown = new CountryServerCatalog(inner, _gateway, _time, _ => null);
        unknown.Selected!.CountryCode.ShouldBeEmpty("не подменяем отсутствие геоданных флагом из названия");
    }

    [Fact]
    public async Task Duplicate_host_is_resolved_once_and_refreshes_after_expiry()
    {
        _gateway.Addresses = [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1")];
        var inner = new ConnectionControllerTests.FakeCatalog(Server("1", "example.com"), Server("2", "EXAMPLE.COM"));
        using var catalog = new CountryServerCatalog(inner, _gateway, _time, _ => "US");
        catalog.Servers.ShouldAllBe(s => s.CountryCode == "");
        var changes = 0;
        catalog.Changed += (_, _) => changes++;
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        _gateway.Asked.Count.ShouldBe(1);
        changes.ShouldBe(1);
        catalog.Servers.ShouldAllBe(s => s.CountryCode == "US");
        _time.Advance(TimeSpan.FromMinutes(11));
        catalog.Servers.ShouldAllBe(s => s.CountryCode == "");
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        _gateway.Asked.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_or_unknown_dns_country_has_no_flag(bool unknown)
    {
        _gateway.Addresses = [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1")];
        using var catalog = new CountryServerCatalog(new ConnectionControllerTests.FakeCatalog(Server("1", "example.org")), _gateway, _time,
            ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "US" : unknown ? null : "JP");
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        catalog.Servers.Single().CountryCode.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ConnectionState.Blocked)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Error)]
    public async Task Does_not_probe_dns_while_protection_is_blocked_or_changing(ConnectionState state)
    {
        _gateway.Status = _gateway.Status with { State = state };
        using var catalog = new CountryServerCatalog(new ConnectionControllerTests.FakeCatalog(Server("1", "example.com")), _gateway, _time);
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        _gateway.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Does_not_probe_when_a_strict_block_is_reported_with_off_state()
    {
        _gateway.Status = _gateway.Status with { State = ConnectionState.Off, StrictBlock = true };
        using var catalog = new CountryServerCatalog(new ConnectionControllerTests.FakeCatalog(Server("1", "example.com")), _gateway, _time);
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        _gateway.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_lookup_can_recover_without_reimporting_the_subscription()
    {
        using var catalog = new CountryServerCatalog(new ConnectionControllerTests.FakeCatalog(Server("1", "example.com")), _gateway, _time, _ => "DE");
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        catalog.Servers.Single().CountryCode.ShouldBeEmpty();
        _gateway.Addresses = [IPAddress.Parse("192.0.2.1")];
        _time.Advance(TimeSpan.FromSeconds(61));
        await catalog.RefreshCountriesAsync(TestContext.Current.CancellationToken);
        catalog.Servers.Single().CountryCode.ShouldBe("DE");
    }

    private static ServerProfile Server(string id, string host) => new(id, "🇯🇵 Arbitrary subscription", "JP", ProxyProtocol.Vless, TransportSecurity.Reality, host, 443, "tcp", null);

    private sealed class CountryGateway : IGatewayClient
    {
        public List<string> Asked { get; } = [];
        public IPAddress[] Addresses { get; set; } = [];
        public ConnectionStatus Status { get; set; } = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);
        public ServiceKind Service => ServiceKind.Installed;
        public event EventHandler<ConnectionStatus>? StatusChanged { add { } remove { } }
        public IReadOnlyList<JournalEntry> Journal => [];
        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }
        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));
        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
        public async IAsyncEnumerable<ProbeStep> ProbeAsync(ProbeKind kind, string target, int port, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            kind.ShouldBe(ProbeKind.Dns);
            lock (Asked)
            {
                Asked.Add(target);
            }

            await Task.Yield();
            foreach (var address in Addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ProbeStep(1, address, TimeSpan.Zero, ProbeOutcome.Ok);
            }
        }
    }
}
