using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class RetiredEntryChainTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-retired-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _secrets = new();

    public void Dispose()
    {
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Upgrade_removes_only_obsolete_profiles_and_their_secrets_without_direct_fallback()
    {
        var (ordinary, chain, snapshot) = PrepareVersionNine();
        _secrets.ReadFailure = new InvalidOperationException("читать секреты при миграции нельзя");
        var catalog = Catalog();

        catalog.Servers.ShouldHaveSingleItem().Id.ShouldBe(ordinary);
        catalog.Subscriptions.ShouldHaveSingleItem().Name.ShouldBe("Обычная");
        catalog.Selected.ShouldBeNull("снятая цепочка не выбирается напрямую и не заменяется другим сервером");
        _secrets.Values.Keys.ShouldBe(["server:" + ordinary, "subscription:" + catalog.Subscriptions[0].Id], ignoreOrder: true);
        Scalar("PRAGMA user_version").ShouldBe(10L);
        Scalar("SELECT COUNT(*) FROM retired_secret_keys").ShouldBe(0L);
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('entry_proxies', 'entry_proxy_bindings')").ShouldBe(0L);
        catalog.Definition(chain).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => catalog.Restore(snapshot));
        catalog.Servers.ShouldHaveSingleItem().Id.ShouldBe(ordinary);
        Catalog().Servers.ShouldHaveSingleItem().Id.ShouldBe(ordinary);
    }

    [Fact]
    public void Refused_secret_cleanup_keeps_a_retry_queue_but_never_restores_a_direct_connection()
    {
        var (ordinary, chain, _) = PrepareVersionNine();
        _secrets.DeleteFailure = new InvalidOperationException("Связка ключей закрыта");

        Catalog().Servers.ShouldHaveSingleItem().Id.ShouldBe(ordinary);
        _secrets.Values.ShouldContainKey("server:" + chain);
        Scalar("SELECT COUNT(*) FROM retired_secret_keys").ShouldBe(4L);

        _secrets.DeleteFailure = null;
        Catalog().Servers.ShouldHaveSingleItem().Id.ShouldBe(ordinary);
        _secrets.Values.ShouldNotContainKey("server:" + chain);
        Scalar("SELECT COUNT(*) FROM retired_secret_keys").ShouldBe(0L);
    }

    [Fact]
    public void Failed_sql_migration_rolls_back_the_catalog_queue_and_version_before_secret_cleanup()
    {
        var (_, chain, _) = PrepareVersionNine();
        using var raw = OpenRaw();
        Execute(raw, "CREATE TRIGGER deny_retirement BEFORE DELETE ON servers BEGIN SELECT RAISE(ABORT, 'test refusal'); END");
        var keys = _secrets.Values.Keys.ToArray();

        Should.Throw<SqliteException>(() => Catalog());

        _secrets.Values.Keys.ShouldBe(keys);
        Scalar("PRAGMA user_version").ShouldBe(9L);
        Scalar("SELECT COUNT(*) FROM entry_proxy_bindings").ShouldBe(1L);
        Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'retired_secret_keys'").ShouldBe(0L);
        _secrets.Values.ShouldContainKey("server:" + chain);
        Execute(raw, "DROP TRIGGER deny_retirement");
        Catalog().Servers.Count.ShouldBe(1);
    }

    [Fact]
    public void A_retired_bundle_cannot_be_partially_imported_as_direct_singbox_outbounds()
    {
        const string text = """
            {"entry_proxy":{},"outbounds":[{"type":"vless","server":"192.0.2.1","server_port":443,
            "uuid":"11111111-2222-3333-4444-555555555555"}]}
            """;
        var batch = ImportParser.Parse(text);

        batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.UnsupportedParameter);
        batch.Lines.ShouldAllBe(line => line.Result.Server == null);
    }

    private (string Ordinary, string Chain, IReadOnlyList<SavedGroup> Snapshot) PrepareVersionNine()
    {
        // Описание обычного профиля создает настоящий каталог; старую схему восстанавливает исходная миграция 0009.
        var catalog = Catalog();
        catalog.Add([LinkParser.Parse("trojan://fake-one@192.0.2.1:443#Direct").Server!], "Обычная", new Uri("https://example.com/sub/fake-direct"));
        var ordinary = catalog.Servers.Single().Id;
        catalog.Add([LinkParser.Parse("trojan://fake-two@192.0.2.2:443#OldChain").Server!], "Цепочка", new Uri("https://example.com/sub/entry/fake-old"));
        var chain = catalog.Servers.Single(server => server.Id != ordinary);
        catalog.Choose(chain.Id);
        var snapshot = catalog.Snapshot();
        _secrets.Save("entry-proxy:fake-credentials", "fake-user-secret");
        _secrets.Save("entry-subscription:fake-url", "https://example.com/sub/entry/fake-old");
        using var raw = OpenRaw();
        using var resource = typeof(SqliteDatabase).Assembly.GetManifestResourceStream("migrations.0009_entry_proxies.sql")!;
        using var reader = new StreamReader(resource);
        Execute(raw, reader.ReadToEnd());
        using var command = raw.CreateCommand();
        command.CommandText = """
            INSERT INTO entry_proxies VALUES ('fake-device', 'fake-node', '192.0.2.3', 1080, 0,
                'entry-proxy:fake-credentials', 'entry-subscription:fake-url', $group);
            INSERT INTO entry_proxy_bindings VALUES ($server, 'fake-device');
            DROP TABLE retired_secret_keys;
            PRAGMA user_version = 9;
            """;
        command.Parameters.AddWithValue("$group", chain.SubscriptionId!);
        command.Parameters.AddWithValue("$server", chain.Id);
        command.ExecuteNonQuery();
        return (ordinary, chain.Id, snapshot);
    }

    private SqliteServerCatalog Catalog() => new(new SqliteDatabase(_paths), _secrets, TimeProvider.System);

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.Database, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private object? Scalar(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
