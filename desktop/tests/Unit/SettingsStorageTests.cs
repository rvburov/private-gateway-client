using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class SettingsStorageTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void New_database_gives_defaults_from_decisions()
    {
        var settings = Store().Load();

        settings.ShouldBe(AppSettings.Default);
        settings.KillSwitch.ShouldBeTrue();
        settings.Notifications.ShouldBe(NotificationLevel.ImportantOnly);
    }

    [Fact]
    public void Saved_settings_survive_a_restart()
    {
        var changed = AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            KillSwitch = false,
            ConnectOnLaunch = true,
            Notifications = NotificationLevel.Off,
            Language = "en",
            SubscriptionRefresh = SubscriptionRefresh.Every24Hours,
            UpdateOnlyViaVpn = false,
            NotifySelectedServerChange = false,
            Protection = new ProtectionSettings(
                BootProtection: false, StrictMode: true, Dns: DnsChoice.Custom, CustomDns: "9.9.9.9", BlockOutsideDns: false,
                FakeDns: true, CheckLeaksAfterConnect: false, BlockIpv6: false, Addresses: AddressPreference.Ipv4Only,
                AllowLan: true, LanDiscovery: true),
            Connection = new ConnectionSettings(
                CoreLogLevel.Debug, TunEngine.Gvisor, Mtu: 1420, StrictRoutes: false, Sniffing: false, TlsFingerprint.Firefox,
                Mux: true, TlsFragment: true, ConnectTimeoutSeconds: 20, ReconnectPolicy.Never, ProxyPort: 2080, ProxyAllowLan: true),
            Routing = new RoutingSettings(
                RoutingMode.Split,
                [new BypassApp("/opt/game/game", "game"), new BypassApp("/opt/bank/bank", "bank", IsEnabled: false)],
                [new DomainRule("example.com", RuleAction.Direct), new DomainRule("*.local", RuleAction.Direct), new DomainRule("ads.example.net", RuleAction.Block)]),
        };
        Store().Save(changed);

        Store().Load().ShouldBe(changed);
    }

    [Fact]
    public void Migrations_run_once_and_set_version()
    {
        // Версия — номер последней миграции; миграции встроены в сборку как migrations.NNNN_*.sql.
        var latest = typeof(SqliteDatabase).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations.", StringComparison.Ordinal))
            .Max(n => int.Parse(n["migrations.".Length..("migrations.".Length + 4)], CultureInfo.InvariantCulture));
        latest.ShouldBeGreaterThanOrEqualTo(2);

        var database = new SqliteDatabase(_paths);
        database.Version().ShouldBe(latest);
        new SqliteDatabase(_paths).Version().ShouldBe(latest);
    }

    [Fact]
    public void Unreadable_value_falls_back_to_default_instead_of_failing()
    {
        var database = new SqliteDatabase(_paths);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('kill_switch', 'может быть'), ('mode', 'Teleport'), ('language', 'xx');";
            command.ExecuteNonQuery();
        }

        var settings = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance).Load();

        settings.KillSwitch.ShouldBeTrue();
        settings.Mode.ShouldBe(ConnectionMode.Vpn);
        settings.Language.ShouldBe("ru");
    }

    [Fact]
    public void Routing_lists_keep_only_valid_entries()
    {
        var database = new SqliteDatabase(_paths);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES
                ('routing.mode', 'Split'),
                ('routing.apps', '[{"Path":"/opt/game/game","Name":"game","IsEnabled":true},{"Path":" ","Name":"пусто","IsEnabled":true}]'),
                ('routing.rules', '[{"Pattern":"Example.COM","Action":"Block"},{"Pattern":"localhost","Action":"Direct"}]');
                """;
            command.ExecuteNonQuery();
        }

        var routing = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance).Load().Routing;

        routing.Mode.ShouldBe(RoutingMode.Split);
        routing.Apps.ShouldBe([new BypassApp("/opt/game/game", "game")], "программа без пути — не программа");
        routing.Rules.ShouldBe([new DomainRule("example.com", RuleAction.Block)], "шаблон приводится к виду для службы, негодный — отбрасывается");
    }

    [Fact]
    public void Broken_routing_json_gives_empty_lists()
    {
        var database = new SqliteDatabase(_paths);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('routing.apps', '[{oops'), ('routing.rules', 'не JSON');";
            command.ExecuteNonQuery();
        }

        var routing = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance).Load().Routing;

        routing.ShouldBe(RoutingSettings.Default);
    }

    [Fact]
    public void Service_saves_only_real_changes_and_reports_them()
    {
        var store = Store();
        var service = new SettingsService(store);
        var reported = 0;
        service.Changed += (_, _) => reported++;

        service.Update(s => s);
        service.Update(s => s with { MinimizeToTray = false });

        reported.ShouldBe(1);
        Store().Load().MinimizeToTray.ShouldBeFalse();
    }

    private SqliteSettingsStore Store() => new(new SqliteDatabase(_paths), NullLogger<SqliteSettingsStore>.Instance);
}
