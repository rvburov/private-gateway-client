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
        };
        Store().Save(changed);

        Store().Load().ShouldBe(changed);
    }

    [Fact]
    public void Migrations_run_once_and_set_version()
    {
        var database = new SqliteDatabase(_paths);
        database.Version().ShouldBe(1);
        new SqliteDatabase(_paths).Version().ShouldBe(1);
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
