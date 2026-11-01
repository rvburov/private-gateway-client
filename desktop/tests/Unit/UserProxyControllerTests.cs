using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>HKCU в тесте заменен памятью: фактическая готовность, владение, авария окна и отсутствие прямого обхода при обрыве.</summary>
public sealed class UserProxyControllerTests
{
    [Theory]
    [InlineData(ConnectionMode.Proxy, ServiceKind.Installed)]
    [InlineData(ConnectionMode.Vpn, ServiceKind.Installed)]
    [InlineData(ConnectionMode.Proxy, ServiceKind.User)]
    public void Applies_only_the_confirmed_port_and_restores_the_original_settings(ConnectionMode mode, ServiceKind service)
    {
        using var rig = new Rig(mode, service);
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, port: null);
        rig.Proxy.Enables.ShouldBeEmpty("Connected старой службы не доказывает наличие HTTP/SOCKS-входа");

        rig.Gateway.Raise(ConnectionState.Connected, port: 2080);
        rig.Proxy.Enables.ShouldBe([2080], "порт службы важнее желаемого порта 10808 в настройках");
        rig.Controller.Status.ShouldBe(new UserProxyStatus(UserProxyState.Applied, 2080));
        rig.Backup.Snapshot!.Original.ShouldBe(original);
        rig.Backup.Snapshot.OwnerService.ShouldBe(service);
        rig.Proxy.Current.Values["ConnectionFlags"].ShouldBe("3", "WPAD и PAC выключены на время подключения");

        rig.Settings.Update(s => s with { Connection = s.Connection with { ProxyPort = 3080 } });
        rig.Proxy.Enables.ShouldBe([2080], "новый желаемый порт еще не готов");
        rig.Gateway.Raise(ConnectionState.Connected, port: 3080);
        rig.Proxy.Enables.ShouldBe([2080, 3080]);
        rig.Backup.Snapshot!.Original.ShouldBe(original, "смена порта не заменяет первоначальный снимок");

        rig.Gateway.Raise(ConnectionState.Off, port: null);
        rig.Proxy.Current.ShouldBe(original);
        rig.Backup.Snapshot.ShouldBeNull();
        rig.Controller.Status.State.ShouldBe(UserProxyState.Restored);
    }

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Blocked)]
    [InlineData(ConnectionState.Error)]
    public void Keeps_the_proxy_on_failure_instead_of_allowing_the_browser_to_go_direct(ConnectionState state)
    {
        using var rig = new Rig();
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        var applied = rig.Proxy.Current;
        rig.Gateway.Raise(state, port: null);
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        rig.Proxy.Current.ShouldBe(applied);
        rig.Proxy.Restores.ShouldBe(0);
        rig.Controller.Status.ShouldBe(new UserProxyStatus(UserProxyState.Held, 10808));
    }

    [Fact]
    public void Foreign_changes_are_not_overwritten_by_disconnect_or_repeated_connected_status()
    {
        using var rig = new Rig();
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        var foreign = new SystemProxySnapshot(new Dictionary<string, string?> { ["ProxyServer"] = "192.0.2.10:3128" });
        rig.Proxy.Current = foreign;
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Time.Advance(TimeSpan.FromSeconds(4));

        rig.Proxy.Current.ShouldBe(foreign);
        rig.Proxy.Enables.ShouldBe([10808], "повтор статуса не забирает настройки у другой программы");
        rig.Controller.Status.State.ShouldBe(UserProxyState.ChangedByOther);
        rig.Gateway.Raise(ConnectionState.Off, port: null);
        rig.Proxy.Current.ShouldBe(foreign);
        rig.Proxy.Restores.ShouldBe(0);
    }

    [Fact]
    public void Restarted_window_waits_for_real_status_and_recovers_the_original_snapshot()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Controller.Dispose();

        var gateway = new FakeGateway(ServiceKind.Installed);
        using var recovered = new UserProxyController(rig.Settings, gateway, rig.Proxy, rig.Backup, rig.Time);
        recovered.Start();
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        rig.Proxy.Restores.ShouldBe(0, "первоначальный Off окна не доказывает отключение живой службы");
        gateway.Raise(ConnectionState.Connected, 10808);
        rig.Backup.Snapshot!.Original.ShouldBe(original);
        rig.Proxy.Enables.ShouldBe([10808], "живой вход после аварии окна подхватывается без нового снимка");
        gateway.Raise(ConnectionState.Off, port: null);
        rig.Proxy.Current.ShouldBe(original);
        rig.Backup.Snapshot.ShouldBeNull();
    }

    [Fact]
    public void Legacy_user_service_remains_the_single_owner_and_vpn_proxy_is_opt_in()
    {
        using var rig = new Rig(service: ServiceKind.User);
        rig.Gateway.Raise(ConnectionState.Connected, 10808, windowManaged: false);
        rig.Proxy.Enables.ShouldBeEmpty();
        rig.Controller.Status.State.ShouldBe(UserProxyState.ServiceManaged);

        rig.Settings.Update(s => s with { Mode = ConnectionMode.Vpn, Connection = s.Connection with { VpnSystemProxy = false } });
        rig.Gateway.Mode = ConnectionMode.Vpn;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Proxy.Enables.ShouldBeEmpty("старый SystemProxy=true не включает новый VPN-вход автоматически");
    }

    [Fact]
    public void Partial_apply_failure_is_rolled_back_and_does_not_become_a_foreign_change()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Proxy.FailAfterChange = true;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Time.Advance(TimeSpan.FromSeconds(4));

        rig.Controller.Status.State.ShouldBe(UserProxyState.Error);
        rig.Proxy.Current.ShouldBe(original);
        rig.Backup.Snapshot!.Original.ShouldBe(original);
        rig.Backup.Snapshot.AppliedConfirmed.ShouldBeFalse();
    }

    [Fact]
    public void Changing_from_proxy_to_vpn_keeps_the_old_proxy_until_the_new_mode_is_confirmed()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Settings.Update(s => s with { Mode = ConnectionMode.Vpn, Connection = s.Connection with { VpnSystemProxy = false } });
        rig.Proxy.Restores.ShouldBe(0, "TUN еще не поднят, а окно лишь сменило желаемый режим");
        rig.Gateway.Mode = ConnectionMode.Vpn;
        rig.Gateway.Raise(ConnectionState.Connected, port: null, windowManaged: false);
        rig.Proxy.Current.ShouldBe(original);
    }

    [Fact]
    public void Explicitly_disabling_proxy_restores_our_settings_even_when_ipc_is_unavailable()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Gateway.HasReceivedStatus = false;
        rig.Settings.Update(s => s with { Connection = s.Connection with { SystemProxy = false } });

        rig.Proxy.Current.ShouldBe(original, "явный выбор пользователя отличается от сохраненного false при старте");
        rig.Backup.Snapshot.ShouldBeNull();
        rig.Controller.Status.State.ShouldBe(UserProxyState.Restored);
    }

    [Fact]
    public void A_new_clients_initial_off_does_not_restore_proxy_before_its_real_status()
    {
        using var rig = new Rig();
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Gateway.HasReceivedStatus = false;
        rig.Gateway.Raise(ConnectionState.Off, port: null, real: false);

        rig.Proxy.Restores.ShouldBe(0, "новая IPC-сессия еще не получила состояние службы");
        rig.Gateway.Raise(ConnectionState.Off, port: null);
        rig.Proxy.Restores.ShouldBe(1);
    }

    [Theory]
    [InlineData(ServiceKind.User, true)]
    [InlineData(ServiceKind.Installed, false)]
    public void Normal_exit_restores_user_service_proxy_but_keeps_a_running_installed_service(ServiceKind service, bool restore)
    {
        using var rig = new Rig(service: service);
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Controller.Shutdown();
        (rig.Proxy.Current == original).ShouldBe(restore);
    }

    [Fact]
    public void Restart_with_vpn_preference_does_not_drop_an_old_proxy_before_the_first_real_status()
    {
        using var rig = new Rig();
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Controller.Dispose();
        var settings = new SettingsService(new MemorySettings(AppSettings.Default));
        var gateway = new FakeGateway(ServiceKind.Installed) { Mode = ConnectionMode.Vpn };
        using var recovered = new UserProxyController(settings, gateway, rig.Proxy, rig.Backup, rig.Time);
        recovered.Start();
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        rig.Proxy.Restores.ShouldBe(0, "сохраненное предпочтение VPN без прокси еще не является ответом службы");
        gateway.Raise(ConnectionState.Connected, port: null, windowManaged: false);
        rig.Proxy.Restores.ShouldBe(1, "новый реальный VPN уже не использует системный прокси");
    }

    [Fact]
    public void Partial_restore_failure_survives_a_window_restart_and_retries_the_original_snapshot()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);
        rig.Proxy.FailRestoreAfterChange = true;
        rig.Gateway.Raise(ConnectionState.Off, port: null);
        rig.Controller.Status.State.ShouldBe(UserProxyState.Error);
        rig.Backup.Snapshot!.Original.ShouldBe(original);
        rig.Backup.Snapshot.AppliedConfirmed.ShouldBeFalse("фаза возврата сохраняется до изменений ОС");
        rig.Controller.Dispose();

        rig.Proxy.FailRestoreAfterChange = false;
        var gateway = new FakeGateway(ServiceKind.Installed);
        using var recovered = new UserProxyController(rig.Settings, gateway, rig.Proxy, rig.Backup, rig.Time);
        recovered.Start();
        gateway.Raise(ConnectionState.Off, port: null);
        rig.Proxy.Current.ShouldBe(original);
        rig.Backup.Snapshot.ShouldBeNull();
    }

    [Fact]
    public void A_failed_port_change_then_a_failed_restore_keep_the_first_snapshot()
    {
        using var rig = new Rig();
        var original = rig.Proxy.Current;
        rig.Gateway.Raise(ConnectionState.Connected, 2080);
        rig.Proxy.FailAfterChange = true;
        rig.Gateway.Raise(ConnectionState.Connected, 3080);
        rig.Proxy.Current.Values["ProxyServer"].ShouldBe("127.0.0.1:2080", "неудачная смена порта вернула прежний вход");

        rig.Proxy.FailRestoreAfterChange = true;
        rig.Gateway.Raise(ConnectionState.Off, port: null);
        rig.Backup.Snapshot!.Original.ShouldBe(original);
        rig.Backup.Snapshot.Restoring.ShouldBeTrue();
        rig.Proxy.FailRestoreAfterChange = false;
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Proxy.Current.ShouldBe(original, "остатки старого порта не считаются чужим изменением");
        rig.Backup.Snapshot.ShouldBeNull();
    }

    [Fact]
    public void Recovery_owner_changes_with_the_confirmed_service_without_replacing_the_original_snapshot()
    {
        var proxy = new FakeProxy();
        var original = proxy.Current;
        var store = new MemoryBackup();
        var lease = new UserProxyLease(proxy, store);
        lease.Apply(10808, ServiceKind.Installed).ShouldBeTrue();
        lease.Apply(10808, ServiceKind.User).ShouldBeTrue();
        store.Snapshot!.OwnerService.ShouldBe(ServiceKind.User);
        store.Snapshot.Original.ShouldBe(original);
        proxy.Enables.ShouldBe([10808], "смена владельца готового входа не перезаписывает HKCU");
    }

    [Fact]
    public void Apply_error_is_visible_and_the_snapshot_survives_for_recovery()
    {
        using var rig = new Rig();
        rig.Proxy.DenyEnable = true;
        rig.Gateway.Raise(ConnectionState.Connected, 10808);

        rig.Controller.Status.State.ShouldBe(UserProxyState.Error);
        rig.Backup.Snapshot.ShouldNotBeNull("снимок сохраняется до изменения настроек ОС");
        rig.Proxy.Restores.ShouldBe(0);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(ConnectionMode mode = ConnectionMode.Proxy, ServiceKind service = ServiceKind.Installed)
        {
            Settings = new SettingsService(new MemorySettings(AppSettings.Default with
            {
                Mode = mode,
                Connection = ConnectionSettings.Default with { VpnSystemProxy = true },
            }));
            Gateway = new FakeGateway(service) { Mode = mode };
            Controller = new UserProxyController(Settings, Gateway, Proxy, Backup, Time);
            Controller.Start();
        }

        public FakeTimeProvider Time { get; } = new();
        public FakeProxy Proxy { get; } = new();
        public MemoryBackup Backup { get; } = new();
        public SettingsService Settings { get; }
        public FakeGateway Gateway { get; }
        public UserProxyController Controller { get; }
        public void Dispose() => Controller.Dispose();
    }

    private sealed class MemorySettings(AppSettings initial) : ISettingsStore
    {
        private AppSettings _current = initial;
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class MemoryBackup : IUserProxyBackupStore
    {
        public UserProxyBackup? Snapshot { get; private set; }
        public UserProxyBackup? Read() => Snapshot;
        public void Write(UserProxyBackup snapshot) => Snapshot = snapshot;
        public void Delete() => Snapshot = null;
    }

    private sealed class FakeProxy : ISystemProxy
    {
        public string Name => "стенд";
        public SystemProxySnapshot Current { get; set; } = new(new Dictionary<string, string?>
        {
            ["ProxyEnable"] = "0", ["ProxyServer"] = null, ["ProxyOverride"] = null,
            ["AutoConfigURL"] = "https://example.com/proxy.pac", ["ConnectionFlags"] = "9",
        });
        public List<int> Enables { get; } = [];
        public int Restores { get; private set; }
        public bool DenyEnable { get; set; }
        public bool FailAfterChange { get; set; }
        public bool FailRestoreAfterChange { get; set; }
        public SystemProxySnapshot Read() => Current;
        public void Enable(int port)
        {
            if (DenyEnable)
            {
                throw new UnauthorizedAccessException("стенд");
            }

            var before = Current;
            Enables.Add(port);
            Current = CurrentUserWindowsProxy.ForPort(port);
            if (FailAfterChange)
            {
                Current = new SystemProxySnapshot(Current.Values.ToDictionary(v => v.Key,
                    v => v.Key == "ConnectionFlags" ? before.Values.GetValueOrDefault(v.Key) : v.Value));
                throw new IOException("ошибка после изменения полей, до подтверждения WinINet");
            }
        }

        public void Restore(SystemProxySnapshot previous)
        {
            Restores++;
            if (FailRestoreAfterChange)
            {
                Current = new SystemProxySnapshot(previous.Values.ToDictionary(v => v.Key,
                    v => v.Key is "ProxyServer" or "ConnectionFlags" ? Current.Values.GetValueOrDefault(v.Key) : v.Value));
                throw new IOException("ошибка в середине возврата настроек");
            }

            Current = previous;
        }
    }

    private sealed class FakeGateway(ServiceKind service) : IGatewayClient
    {
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);
        public ServiceKind Service => service;
        public bool HasReceivedStatus { get; set; }
        public ConnectionMode Mode { get; set; } = ConnectionMode.Proxy;
        public IReadOnlyList<JournalEntry> Journal => [];
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }
        public void Raise(ConnectionState state, int? port, bool windowManaged = true, bool real = true)
        {
            Status = Status with { State = state, Mode = Mode, LocalProxyPort = port, UserProxyManagedByWindow = windowManaged };
            HasReceivedStatus = real;
            StatusChanged?.Invoke(this, Status);
        }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));
        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
    }
}
