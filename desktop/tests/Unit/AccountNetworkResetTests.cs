using Microsoft.Extensions.Logging.Abstractions;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class AccountNetworkResetTests
{
    [Fact]
    public async Task Sign_out_disables_network_automation_but_keeps_servers_related_preferences()
    {
        var original = AppSettings.Default with
        {
            KillSwitch = true,
            ConnectOnLaunch = true,
            Language = "en",
            Protection = ProtectionSettings.Default with { StrictMode = true, BootProtection = true, Dns = DnsChoice.Quad9Doh },
            Connection = ConnectionSettings.Default with { SystemProxy = true, VpnSystemProxy = true, ProxyPort = 2080 },
        };
        var settings = new SettingsService(new MemorySettings(original));
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected);
        var proxy = new UserProxy();
        var reset = new AccountNetworkReset(settings, gateway, proxy);

        (await reset.RestoreAsync()).ShouldBeTrue();

        (settings.Current.KillSwitch, settings.Current.ConnectOnLaunch, settings.Current.Protection.StrictMode,
            settings.Current.Protection.BootProtection, settings.Current.Connection.SystemProxy, settings.Current.Connection.VpnSystemProxy)
            .ShouldBe((false, false, false, false, false, false));
        (settings.Current.Language, settings.Current.Protection.Dns, settings.Current.Protection.BlockIpv6,
            settings.Current.Protection.BlockOutsideDns, settings.Current.Connection.ProxyPort)
            .ShouldBe(("en", DnsChoice.Quad9Doh, true, true, 2080));
        settings.Current.AccountSignedOut.ShouldBeTrue();
        gateway.Calls.ShouldBe(["drop"]);
        proxy.Restores.ShouldBe(1);
        (reset.NeedsRecovery, reset.IsRunning, reset.HasRestored).ShouldBe((false, false, true));
        reset.AcceptSignIn();
        settings.Current.AccountSignedOut.ShouldBeFalse();
        settings.Current.KillSwitch.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_failure_keeps_recovery_available_and_does_not_skip_the_other_cleanup(bool serviceFails, bool settingsFail)
    {
        var store = new MemorySettings(AppSettings.Default) { FailSave = settingsFail };
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected)
        {
            PendingDrop = serviceFails ? Task.FromException(new IOException("IPC недоступен")) : null,
        };
        var proxy = new UserProxy();
        var reset = new AccountNetworkReset(new SettingsService(store), gateway, proxy);

        (await reset.RestoreAsync()).ShouldBeFalse();
        (gateway.Calls.Count, proxy.Restores, reset.NeedsRecovery).ShouldBe((1, 1, true));

        gateway.PendingDrop = null;
        store.FailSave = false;
        (await reset.RestoreAsync()).ShouldBeTrue();
        (gateway.Calls.Count, proxy.Restores, reset.NeedsRecovery).ShouldBe((2, 2, false));
    }

    [Fact]
    public async Task Concurrent_recovery_requests_share_the_same_stop_and_a_proxy_error_is_not_success()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected) { PendingDrop = stopped.Task };
        var proxy = new UserProxy { Succeeds = false };
        var reset = new AccountNetworkReset(new SettingsService(new MemorySettings(AppSettings.Default)), gateway, proxy);
        var first = reset.RestoreAsync();
        var second = reset.RestoreAsync();
        second.ShouldBeSameAs(first);
        (reset.IsRunning, reset.NeedsRecovery).ShouldBe((true, true));
        stopped.SetResult();
        (await first).ShouldBeFalse();
        (gateway.Calls.Count, proxy.Restores).ShouldBe((1, 1));
        reset.NeedsRecovery.ShouldBeTrue();
    }

    [Fact]
    public void Signed_out_marker_survives_real_storage_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "pg-account-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = new SqliteDatabase(new AppPaths(path));
            var store = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance);
            store.Save(AppSettings.Default with { AccountSignedOut = true, AccountNetworkResetPending = true });
            var loaded = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance).Load();
            (loaded.AccountSignedOut, loaded.AccountNetworkResetPending).ShouldBe((true, true));
            store.Save(store.Load() with { AccountSignedOut = false });
            store.Load().AccountSignedOut.ShouldBeFalse();
            store.Load().AccountNetworkResetPending.ShouldBeTrue("новый вход не подменяет подтверждение восстановления");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    internal sealed class UserProxy : IUserProxyController
    {
        public int Restores { get; private set; }
        public bool Succeeds { get; set; } = true;
        public UserProxyStatus Status => new(UserProxyState.Off);
        public event EventHandler<UserProxyStatus>? Changed { add { } remove { } }
        public void Start() { }
        public void Shutdown() { }
        public bool RestoreForSignOut() { Restores++; return Succeeds; }
    }

    internal sealed class MemorySettings(AppSettings initial) : ISettingsStore
    {
        public bool FailSave { get; set; }
        private AppSettings _settings = initial;
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = FailSave ? throw new IOException("База недоступна") : settings;
    }
}
