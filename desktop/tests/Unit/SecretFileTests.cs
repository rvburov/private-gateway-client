using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Секреты Windows — в своем файле (<see cref="SecretFile"/>), а не в базе каталога. Каталог пишет секрет посреди своей
/// транзакции: в той же базе запись ждала бы ее конца, а она — записи, и «Добавить» падало с «database is locked»
/// (у пользователя на Windows 2026-10-29). Здесь хранилище устроено как DPAPI (файл SQLite рядом с базой), только без
/// шифрования — чтобы те же пути каталога шли на любой ОС.
/// </summary>
public sealed class SecretFileTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 17, 3, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Catalog_adds_updates_and_restores_while_writing_secrets_beside_its_database()
    {
        var database = new SqliteDatabase(_paths);
        var store = new FileStore(new SecretFile(database));
        var catalog = new SqliteServerCatalog(database, store, _time);
        var url = new Uri("https://example.com/sub/token-a");

        var started = DateTime.UtcNow;
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B"), "Основная", url).Added.ShouldBe(2);
        var subscription = catalog.Subscriptions.ShouldHaveSingleItem().Id;
        catalog.ApplyUpdate(subscription, Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-c@198.51.100.3:443#C"));
        catalog.Restore(catalog.Snapshot());
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "запись секрета не ждет транзакцию каталога");

        catalog.Servers.Count.ShouldBe(2);
        catalog.Servers.Select(s => catalog.Definition(s.Id)!.Credentials).ShouldAllBe(c => c != null);
        catalog.SubscriptionUrl(catalog.Subscriptions[0].Id).ShouldBe(url);
        File.Exists(_paths.Secrets).ShouldBeTrue();
        SqliteConnection.ClearAllPools();
        Encoding.UTF8.GetString(File.ReadAllBytes(_paths.Database)).ShouldNotContain("pw-c", Case.Sensitive, "секреты — не в базе каталога");
    }

    [Fact]
    public void Secrets_of_earlier_versions_move_from_the_database_to_the_file()
    {
        var database = new SqliteDatabase(_paths);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO secrets (key, value) VALUES ('server:srv-1', $value)";
            command.Parameters.AddWithValue("$value", Encoding.UTF8.GetBytes("blob-1"));
            command.ExecuteNonQuery();
        }

        File.Exists(_paths.Secrets).ShouldBeFalse();
        var file = new SecretFile(database);

        Encoding.UTF8.GetString(file.Read("server:srv-1").ShouldNotBeNull()).ShouldBe("blob-1");
        file.Write("server:srv-1", Encoding.UTF8.GetBytes("blob-2"));
        Encoding.UTF8.GetString(new SecretFile(database).Read("server:srv-1")!).ShouldBe("blob-2", "переносится только при создании файла");
    }

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];

    /// <summary>Как <see cref="DpapiSecretStore"/>, но без DPAPI: тот же файл, текст как есть.</summary>
    private sealed class FileStore(SecretFile file) : ISecretStore
    {
        public string Name => "Файл секретов без шифрования";

        public bool IsAvailable => true;

        public void Save(string key, string secret) => file.Write(key, Encoding.UTF8.GetBytes(secret));

        public string? Read(string key) => file.Read(key) is { } blob ? Encoding.UTF8.GetString(blob) : null;

        public void Delete(string key) => file.Delete(key);
    }
}
