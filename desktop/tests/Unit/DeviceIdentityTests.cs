using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Infrastructure.Account;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// uid устройства и сведения входа в базе программы (<see cref="SqliteAccountStore"/>): uid — один на установку,
/// переживает перезапуск и выход, в резервную копию не попадает.
/// </summary>
public sealed class DeviceIdentityTests : IDisposable
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
    public void Uid_is_made_once_and_survives_a_restart_and_a_sign_out()
    {
        var store = new SqliteAccountStore(new SqliteDatabase(_paths));
        var uid = store.Uid;

        Guid.TryParse(uid, out _).ShouldBeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(uid, "^[A-Za-z0-9_-]{16,64}$").ShouldBeTrue("формат uid сайта");
        store.Uid.ShouldBe(uid);
        store.Save(new SavedAccount(DateTimeOffset.UnixEpoch.AddDays(30), "user@example.com", null, "WORKSTATION", DateTimeOffset.UnixEpoch));
        store.Clear();

        var restarted = new SqliteAccountStore(new SqliteDatabase(_paths));
        restarted.Uid.ShouldBe(uid, "тот же компьютер — то же устройство на сайте, место не тратится");
        new SqliteAccountStore(new SqliteDatabase(new AppPaths(_paths.DataDirectory + "-other"))).Uid.ShouldNotBe(uid);
        Directory.Delete(_paths.DataDirectory + "-other", recursive: true);
    }

    [Fact]
    public void Saved_account_survives_a_restart_and_clear_forgets_it()
    {
        var saved = new SavedAccount(new DateTimeOffset(2026, 12, 12, 10, 0, 0, 123, TimeSpan.Zero), "user@example.com", "Роман", "WORKSTATION",
            new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var store = new SqliteAccountStore(new SqliteDatabase(_paths));
        store.Load().ShouldBeNull();

        store.Save(saved);
        store.Save(saved with { DisplayName = null });

        new SqliteAccountStore(new SqliteDatabase(_paths)).Load().ShouldBe(saved with { DisplayName = null });
        store.Clear();
        new SqliteAccountStore(new SqliteDatabase(_paths)).Load().ShouldBeNull();
    }

    [Fact]
    public void Uid_and_sign_in_do_not_go_into_a_backup()
    {
        var time = new FakeTimeProvider();
        var secrets = new FakeSecrets();
        var database = new SqliteDatabase(_paths);
        var account = new SqliteAccountStore(database);
        var uid = account.Uid;
        account.Save(new SavedAccount(DateTimeOffset.UnixEpoch.AddDays(30), "user@example.com", "Роман", "WORKSTATION", DateTimeOffset.UnixEpoch));
        secrets.Save(AccountService.TokenKey, "tok-secret-marker");
        var settings = new SettingsService(new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance));
        settings.Update(s => s with { Language = "en" });
        var backup = new BackupService(settings, new SqliteServerCatalog(database, secrets, time), secrets, _paths, time);

        var file = backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), "correct horse battery");
        var content = BackupService.Read(file, "correct horse battery");
        content.HasSettings.ShouldBeTrue();

        var away = new AppPaths(_paths.DataDirectory + "-away");
        try
        {
            var awayDatabase = new SqliteDatabase(away);
            var awaySecrets = new FakeSecrets();
            var awaySettings = new SettingsService(new SqliteSettingsStore(awayDatabase, NullLogger<SqliteSettingsStore>.Instance));
            new BackupService(awaySettings, new SqliteServerCatalog(awayDatabase, awaySecrets, time), awaySecrets, away, time).Restore(content);

            awaySettings.Current.Language.ShouldBe("en", "настройки перенеслись");
            var awayAccount = new SqliteAccountStore(awayDatabase);
            awayAccount.Uid.ShouldNotBe(uid, "uid устройства на другой компьютер не переносится");
            awayAccount.Load().ShouldBeNull("и вход тоже");
            awaySecrets.Values.ShouldNotContainKey(AccountService.TokenKey);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(away.DataDirectory))
            {
                Directory.Delete(away.DataDirectory, recursive: true);
            }
        }
    }
}
