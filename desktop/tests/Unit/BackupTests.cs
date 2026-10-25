using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Резервная копия: файл под паролем, восстановление на другом компьютере и автокопия.</summary>
public sealed class BackupTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string Uuid = "11111111-2222-3333-4444-555555555555";
    private static readonly Uri Subscription = new("https://example.com/sub/token-marker-123");

    private readonly List<AppPaths> _dirs = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 12, 0, TimeSpan.Zero));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs.Where(d => Directory.Exists(d.DataDirectory)))
        {
            Directory.Delete(dir.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Copy_with_keys_brings_everything_to_another_computer()
    {
        var home = Machine();
        home.Catalog.Add(Parse("trojan://pw-mine@198.51.100.1:443#Mine"), null);
        home.Catalog.Add(Parse($"vless://{Uuid}@203.0.113.24:443?security=tls&sni=example.com#NL", "trojan://pw-de@198.51.100.2:443#DE"), "Основная", Subscription);
        var nl = home.Catalog.Servers.Single(s => s.Name == "NL").Id;
        home.Catalog.Choose(nl);
        home.Catalog.SetFavorite(nl, true);
        home.Catalog.Rename(nl, "Мой NL");
        home.Settings.Update(s => s with { Language = "en", KillSwitch = false, Routing = s.Routing with { Mode = RoutingMode.Split } });

        var file = home.Backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password);
        file.ShouldNotContain("pw-mine");
        file.ShouldNotContain(Uuid);
        file.ShouldNotContain("token-marker");
        file.ShouldNotContain("198.51.100.1");

        var away = Machine();
        var content = BackupService.Read(file, Password);
        (content.HasServers, content.HasSettings, content.HasKeys, content.ServerCount, content.CreatedAt).ShouldBe((true, true, true, 3, _time.GetUtcNow()));
        var summary = away.Backup.Restore(content);

        summary.ShouldBe(new RestoreSummary(new RestoreOutcome(3, 0), Settings: true));
        away.Catalog.Servers.Select(s => (s.Name, s.IsFavorite)).ShouldBe([("Mine", false), ("Мой NL", true), ("DE", false)]);
        away.Catalog.Selected!.Name.ShouldBe("Мой NL");
        away.Catalog.Definition(away.Catalog.Selected.Id)!.Credentials.Uuid.ShouldBe(Uuid);
        var group = away.Catalog.Subscriptions.ShouldHaveSingleItem();
        (group.Name, group.UpdatedAt).ShouldBe(("Основная", null), "восстановленная подписка обновится при ближайшей проверке");
        away.Catalog.SubscriptionUrl(group.Id).ShouldBe(Subscription);
        away.Settings.Current.ShouldBe(home.Settings.Current);
        away.Store.Load().ShouldBe(home.Settings.Current, "восстановленные настройки сохранены в базе");

        var reopened = Machine(away.Paths, away.Secrets).Catalog;
        reopened.Servers.Select(s => (s.Name, s.IsFavorite)).ShouldBe([("Mine", false), ("Мой NL", true), ("DE", false)], "и серверы — в базе");
        reopened.Selected!.Name.ShouldBe("Мой NL");
        reopened.Subscriptions.ShouldHaveSingleItem().UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public void Copy_without_keys_takes_keys_from_the_same_servers_here_or_skips_them()
    {
        var home = Machine();
        home.Catalog.Add(Parse("trojan://pw-mine@198.51.100.1:443#Mine"), null);
        home.Catalog.Add(Parse("trojan://pw-de@198.51.100.2:443#DE"), "Основная", Subscription);
        var file = home.Backup.Create(new BackupParts(Servers: true, Settings: false, Keys: false), Password);
        BackupService.Read(file, Password).HasKeys.ShouldBeFalse();

        home.Catalog.Rename(home.Catalog.Servers[0].Id, "Переименован");
        home.Backup.Restore(BackupService.Read(file, Password)).Servers.ShouldBe(new RestoreOutcome(2, 0));
        home.Catalog.Servers.Select(s => s.Name).ShouldBe(["Mine", "DE"]);
        home.Catalog.Definition(home.Catalog.Servers[0].Id)!.Credentials.Password.ShouldBe("pw-mine", "ключ взят у такого же сервера здесь");

        var away = Machine();
        away.Backup.Restore(BackupService.Read(file, Password)).Servers.ShouldBe(new RestoreOutcome(0, 2));
        away.Catalog.Servers.ShouldBeEmpty("без ключей к серверу не подключиться");
        away.Catalog.SubscriptionUrl(away.Catalog.Subscriptions.ShouldHaveSingleItem().Id).ShouldBe(Subscription, "серверы подписки вернутся с ее обновлением");
        away.Secrets.Values.Keys.ShouldNotContain(k => k.StartsWith("server:", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_chosen_parts_are_restored()
    {
        var home = Machine();
        home.Catalog.Add(Parse("trojan://pw-mine@198.51.100.1:443#Mine"), null);
        home.Settings.Update(s => s with { KillSwitch = false });
        var settingsOnly = home.Backup.Create(new BackupParts(Servers: false, Settings: true, Keys: false), Password);

        var away = Machine();
        away.Catalog.Add(Parse("trojan://pw-away@198.51.100.9:443#Away"), null);
        var summary = away.Backup.Restore(BackupService.Read(settingsOnly, Password));

        summary.ShouldBe(new RestoreSummary(null, Settings: true));
        away.Settings.Current.KillSwitch.ShouldBeFalse();
        away.Catalog.Servers.Select(s => s.Name).ShouldBe(["Away"], "серверов в копии нет — свои остаются");
    }

    [Fact]
    public void Wrong_password_tampering_and_other_files_are_told_apart()
    {
        var home = Machine();
        var file = home.Backup.Create(new BackupParts(Servers: false, Settings: true, Keys: false), Password);

        Error(() => BackupService.Read(file, "wrong password")).ShouldBe(BackupError.WrongPassword);
        Error(() => BackupService.Read(Changed(file, "created", "2020-01-01T00:00:00.0000000+00:00"), Password))
            .ShouldBe(BackupError.WrongPassword, "время создания под защитой тега");
        Error(() => BackupService.Read(Changed(file, "version", 2), Password)).ShouldBe(BackupError.NewerVersion);
        Error(() => BackupService.Read(Changed(file, "iterations", 1000), Password)).ShouldBe(BackupError.NotBackup, "слабый ключ не принимаем");
        Error(() => BackupService.Read(Changed(file, "kdf", "os-key"), Password)).ShouldBe(BackupError.NotBackup);
        Error(() => BackupService.Read(Changed(file, "salt", "%%%"), Password)).ShouldBe(BackupError.NotBackup);
        Error(() => BackupService.Read("vless://not-a-backup", Password)).ShouldBe(BackupError.NotBackup);
        Error(() => BackupService.Read("""{"format":"something-else","version":1}""", Password)).ShouldBe(BackupError.NotBackup);
        Should.Throw<ArgumentException>(() => home.Backup.Create(new BackupParts(Servers: false, Settings: true, Keys: false), "1234567"));
    }

    [Fact]
    public void Restore_keeps_an_autocopy_that_undoes_it()
    {
        var home = Machine();
        home.Catalog.Add(Parse("trojan://pw-old@198.51.100.1:443#Old"), null);
        var file = Machine("donor", m =>
        {
            m.Catalog.Add(Parse("trojan://pw-new@198.51.100.7:443#New"), null);
            m.Settings.Update(s => s with { KillSwitch = false });
        }).Backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password);
        home.Backup.LastAutoCopy.ShouldBeNull();

        _time.Advance(TimeSpan.FromMinutes(5));
        home.Backup.Restore(BackupService.Read(file, Password));

        home.Catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("New");
        home.Backup.LastAutoCopy.ShouldBe(_time.GetUtcNow());
        var autocopy = File.ReadAllText(home.Backup.AutoCopyPath);
        autocopy.ShouldNotContain("pw-old");
        home.Secrets.Values.ShouldContainKey("backup:autocopy");

        home.Backup.RestoreAutoCopy();

        home.Catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("Old");
        home.Catalog.Definition(home.Catalog.Servers[0].Id)!.Credentials.Password.ShouldBe("pw-old");
        home.Settings.Current.KillSwitch.ShouldBeTrue();
        home.Secrets.Values.Values.ShouldNotContain("""{"Password":"pw-new"}""", "ключи замененных серверов удалены");
    }

    [Fact]
    public void Nothing_changes_when_the_os_store_fails()
    {
        var home = Machine();
        home.Catalog.Add(Parse("trojan://pw-old@198.51.100.1:443#Old"), null);
        var file = Machine("donor", m => m.Catalog.Add(Parse("trojan://pw-a@198.51.100.7:443#A", "trojan://pw-b@198.51.100.8:443#B"), null))
            .Backup.Create(new BackupParts(Servers: true, Settings: false, Keys: true), Password);
        var content = BackupService.Read(file, Password);
        var before = home.Secrets.Values.ToDictionary();

        home.Secrets.SavesLeft = 2; // автокопия и ее ключ проходят, второй сервер — нет
        Should.Throw<InvalidOperationException>(() => home.Backup.Restore(content));

        home.Catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("Old");
        Machine(home.Paths).Catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("Old", "база откатилась");
        home.Secrets.Values.Keys.Where(k => k.StartsWith("server:", StringComparison.Ordinal)).ShouldBe(
            before.Keys.Where(k => k.StartsWith("server:", StringComparison.Ordinal)), "записанные ключи новых серверов удалены");

        home.Secrets.SavesLeft = null;
        home.Secrets.Available = false;
        Should.Throw<InvalidOperationException>(() => home.Backup.Restore(content));
    }

    [Fact]
    public void Restore_does_not_bring_the_same_server_twice()
    {
        var home = Machine();
        var server = new SavedServer(Parse("trojan://pw-a@198.51.100.1:443#A")[0]);

        var outcome = home.Catalog.Restore([new SavedGroup(null, null, [server, server]), new SavedGroup("Группа", null, [server])]);

        outcome.ShouldBe(new RestoreOutcome(1, 0));
        home.Catalog.Servers.ShouldHaveSingleItem();
        home.Catalog.Subscriptions.ShouldHaveSingleItem().UpdatedAt.ShouldBe(_time.GetUtcNow(), "группа без адреса не ждет обновления");
    }

    private static BackupError Error(Action read) => Should.Throw<BackupException>(read).Error;

    private static string Changed(string file, string key, JsonNode value)
    {
        var envelope = JsonNode.Parse(file)!.AsObject();
        envelope[key] = value;
        return envelope.ToJsonString();
    }

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];

    private Machine Machine(string name = "", Action<Machine>? prepare = null)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "pg-backup-" + name + Guid.NewGuid().ToString("N")));
        _dirs.Add(paths);
        var machine = Machine(paths);
        prepare?.Invoke(machine);
        return machine;
    }

    private Machine Machine(AppPaths paths, FakeSecrets? secrets = null)
    {
        secrets ??= new FakeSecrets();
        var database = new SqliteDatabase(paths);
        var store = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance);
        var settings = new SettingsService(store);
        var catalog = new SqliteServerCatalog(database, secrets, _time);
        return new Machine(paths, secrets, store, settings, catalog, new BackupService(settings, catalog, secrets, paths, _time));
    }
}

internal sealed record Machine(
    AppPaths Paths, FakeSecrets Secrets, SqliteSettingsStore Store, SettingsService Settings, SqliteServerCatalog Catalog, BackupService Backup);
