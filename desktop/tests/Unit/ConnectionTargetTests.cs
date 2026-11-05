using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Два сохраненных назначения не подменяют друг друга при выборе режима, запуске и смене настроек.</summary>
public sealed class ConnectionTargetTests
{
    private static readonly ServerProfile Vpn = new("vpn", "VPN", "", ProxyProtocol.Vless, TransportSecurity.Reality,
        "203.0.113.10", 8443, "tcp", null);
    private static readonly ServerProfile OtherVpn = Vpn with { Id = "other", Host = "203.0.113.11" };
    private static readonly ExternalProxySettings Proxy = new(ExternalProxyProtocol.Http, "198.51.100.21", 3128, Revision: 1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shield_uses_the_mode_target_and_switching_preserves_both_endpoints_and_local_port()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with { ExternalProxy = Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off);
        var controller = new ConnectionController(gateway, catalog, target);

        (await controller.ToggleAsync(Ct)).ShouldBeTrue();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        (await controller.ToggleAsync(Ct)).ShouldBeTrue();
        settings.Update(s => s with { Mode = ConnectionMode.Vpn });
        (await controller.ToggleAsync(Ct)).ShouldBeTrue();

        gateway.Calls.ShouldBe(["connect:vpn", "connect:external-proxy:1", "connect:vpn"]);
        settings.Current.ExternalProxy.ShouldBe(Proxy);
        catalog.Selected.ShouldBe(Vpn);
        settings.Current.Connection.ProxyPort.ShouldBe(AppSettings.Default.Connection.ProxyPort);
    }

    [Fact]
    public async Task An_unconfigured_proxy_never_falls_back_to_the_saved_vpn()
    {
        var (_, catalog, target) = Setup(AppSettings.Default with { Mode = ConnectionMode.Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off);

        target.Selected.ShouldBeNull();
        (await new ConnectionController(gateway, catalog, target).ToggleAsync(Ct)).ShouldBeFalse();

        gateway.Calls.ShouldBeEmpty();
        catalog.Selected.ShouldBe(Vpn);
    }

    [Fact]
    public async Task Choosing_a_vpn_while_proxy_is_active_only_saves_the_future_vpn_choice()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with { Mode = ConnectionMode.Proxy, ExternalProxy = Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected, "external-proxy:1");

        await new ConnectionController(gateway, catalog, target).SwitchServerAsync(OtherVpn.Id, Ct);

        catalog.Selected.ShouldBe(OtherVpn);
        settings.Current.Mode.ShouldBe(ConnectionMode.Proxy);
        target.Selected!.Host.ShouldBe(Proxy.Host);
        gateway.Calls.ShouldBeEmpty("выбор VPN не отправляет его как внешний прокси и не меняет действующий выход");
    }

    [Fact]
    public async Task Connect_from_a_vpn_card_explicitly_selects_vpn_and_keeps_the_external_settings()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with { Mode = ConnectionMode.Proxy, ExternalProxy = Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off);

        await new ConnectionController(gateway, catalog, target).ConnectToAsync(OtherVpn.Id, Ct);

        settings.Current.Mode.ShouldBe(ConnectionMode.Vpn);
        settings.Current.ExternalProxy.ShouldBe(Proxy);
        gateway.Calls.ShouldBe(["connect:other"]);
    }

    [Fact]
    public void The_working_proxy_keeps_its_metadata_until_the_new_revision_is_applied()
    {
        var (settings, _, target) = Setup(AppSettings.Default with { Mode = ConnectionMode.Proxy, ExternalProxy = Proxy });
        var old = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Proxy,
            ServerId = "external-proxy:1",
        };
        settings.Update(s => s with { ExternalProxy = Proxy with { Host = "198.51.100.22", Port = 8080, Revision = 2 } });

        target.Selected!.Host.ShouldBe("198.51.100.22");
        target.InUse(old)!.Host.ShouldBe("198.51.100.21");
        target.InUse(old)!.Port.ShouldBe(3128);
        target.InUse(old with { ServerId = "external-proxy:2" })!.Host.ShouldBe("198.51.100.22");
        target.InUse(old with { ServerId = "external-proxy:900" }).ShouldBeNull("неизвестная редакция не заменяется VPN или новым адресом");
        target.InUse(old with { ServerId = null }).ShouldBeNull();
    }

    [Fact]
    public void A_working_vpn_is_not_renamed_to_a_not_yet_applied_proxy()
    {
        var (settings, _, target) = Setup(AppSettings.Default with { ExternalProxy = Proxy });
        var actual = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Vpn,
            ServerId = OtherVpn.Id,
        };

        settings.Update(s => s with { Mode = ConnectionMode.Proxy });

        target.Selected!.Id.ShouldBe("external-proxy:1");
        target.InUse(actual).ShouldBe(OtherVpn);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    public void An_active_mode_switch_to_an_unconfigured_proxy_disconnects_the_old_path(ConnectionState state)
    {
        var (settings, catalog, target) = Setup(AppSettings.Default);
        var gateway = new ConnectionControllerTests.FakeGateway(state, Vpn.Id);
        var time = new FakeTimeProvider();
        new ProtectionSync(settings, gateway, catalog, time, target).Start();

        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        time.Advance(ProtectionSync.Quiet);

        gateway.Calls.ShouldBe(["disconnect"], "нет внешнего адреса — не продолжать старый VPN под названием прокси");
    }

    [Fact]
    public void Editing_the_inactive_proxy_does_not_restart_vpn_but_editing_the_active_proxy_uses_the_new_revision()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with { ExternalProxy = Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected, Vpn.Id);
        var time = new FakeTimeProvider();
        new ProtectionSync(settings, gateway, catalog, time, target).Start();

        settings.Update(s => s with { ExternalProxy = Proxy with { Revision = 2 } });
        time.Advance(ProtectionSync.Quiet);
        gateway.Calls.ShouldBeEmpty();

        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        time.Advance(ProtectionSync.Quiet);
        gateway.Calls.ShouldBe(["connect:external-proxy:2"]);

        gateway.Status = gateway.Status with { Mode = ConnectionMode.Proxy, ServerId = "external-proxy:2" };
        settings.Update(s => s with { ExternalProxy = Proxy with { Revision = 3 } });
        time.Advance(ProtectionSync.Quiet);
        gateway.Calls.ShouldBe(["connect:external-proxy:2", "connect:external-proxy:3"]);
    }

    [Fact]
    public void Explicit_vpn_connect_during_the_mode_switch_is_not_repeated_by_settings_sync()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with { Mode = ConnectionMode.Proxy, ExternalProxy = Proxy });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected, "external-proxy:1");
        gateway.Status = gateway.Status with { Mode = ConnectionMode.Proxy };
        var time = new FakeTimeProvider();
        new ProtectionSync(settings, gateway, catalog, time, target).Start();

        settings.Update(s => s with { Mode = ConnectionMode.Vpn });
        gateway.Status = gateway.Status with { State = ConnectionState.Connecting, Mode = ConnectionMode.Vpn, ServerId = Vpn.Id };
        time.Advance(ProtectionSync.Quiet);

        gateway.Calls.ShouldBeEmpty("та же цель уже запускается по явному нажатию в карточке VPN");
    }

    [Fact]
    public async Task Launch_connect_uses_the_latest_mode_after_waiting_for_the_service()
    {
        var (settings, catalog, target) = Setup(AppSettings.Default with
        {
            ExternalProxy = Proxy,
            ConnectOnLaunch = true,
            OnboardingDone = true,
        });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off);
        var launch = new LaunchConnect(settings, gateway, catalog, new FakeTimeProvider(), target);
        var pending = launch.RunAsync(Ct);

        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        gateway.Announce();
        await pending;

        gateway.Calls.ShouldBe(["connect:external-proxy:1"]);
        catalog.Selected.ShouldBe(Vpn);
    }

    [Fact]
    public async Task Proxy_launch_does_not_need_a_vpn_profile()
    {
        var settings = new SettingsService(new MemorySettings(AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            ExternalProxy = Proxy,
            ConnectOnLaunch = true,
            OnboardingDone = true,
        }));
        var catalog = new ConnectionControllerTests.FakeCatalog();
        var target = new ConnectionTarget(catalog, settings);
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off);
        var pending = new LaunchConnect(settings, gateway, catalog, new FakeTimeProvider(), target).RunAsync(Ct);
        gateway.Announce();
        await pending;

        gateway.Calls.ShouldBe(["connect:external-proxy:1"]);
    }

    private static (SettingsService Settings, ConnectionControllerTests.FakeCatalog Catalog, ConnectionTarget Target) Setup(AppSettings initial)
    {
        var settings = new SettingsService(new MemorySettings(initial));
        var catalog = new ConnectionControllerTests.FakeCatalog(Vpn, OtherVpn) { SelectedId = Vpn.Id };
        return (settings, catalog, new ConnectionTarget(catalog, settings));
    }

    private sealed class MemorySettings(AppSettings initial) : ISettingsStore
    {
        private AppSettings _current = initial;
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }
}
