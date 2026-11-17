using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Серверы, привязка SOCKS5 и токен подписки меняются вместе или не меняются вовсе.</summary>
public sealed class EntryChainCatalogUpdateTests : IDisposable
{
    private static readonly Uri OldUrl = new("https://panel.example/sub/entry/old-test-token");
    private static readonly Uri NewUrl = new("https://panel.example/sub/entry/new-test-token");
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-chain-update-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _secrets = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void One_transaction_rotates_the_subscription_entry_and_vless_profile()
    {
        var (catalog, entry, groupId) = Import();
        var oldId = catalog.Servers.ShouldHaveSingleItem().Id;
        var updated = Profile("22222222-3333-4444-5555-666666666666");
        StageNewSecrets();

        var result = catalog.TryApplyEntryUpdate(groupId, OldUrl, NewUrl, [updated], Mutate);

        result.ShouldNotBeNull().Added.ShouldBe(1);
        catalog.SubscriptionUrl(groupId).ShouldBe(NewUrl);
        catalog.Servers.Single(server => server.Id != oldId).MissingSince.ShouldBeNull();
        var newId = catalog.Servers.Single(server => server.Id != oldId).Id;
        entry.DefinitionFor(newId).ShouldNotBeNull().Credentials.Password.ShouldBe("new-proxy-password-marker");
        entry.SubscriptionFor(NewUrl).ShouldNotBeNull().Url.ShouldBe(NewUrl);
        Should.Throw<SecretStoreException>(() => entry.SubscriptionFor(OldUrl));
        new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, TimeProvider.System).SubscriptionUrl(groupId).ShouldBe(NewUrl);
    }

    [Fact]
    public void Failed_entry_mutation_rolls_back_profiles_and_the_active_url()
    {
        var (catalog, entry, groupId) = Import();
        var oldId = catalog.Servers.ShouldHaveSingleItem().Id;
        StageNewSecrets();

        Should.Throw<InvalidOperationException>(() => catalog.TryApplyEntryUpdate(groupId, OldUrl, NewUrl,
            [Profile("22222222-3333-4444-5555-666666666666")], (connection, transaction) =>
            {
                Mutate(connection, transaction);
                throw new InvalidOperationException("test transaction abort");
            }));

        catalog.SubscriptionUrl(groupId).ShouldBe(OldUrl);
        catalog.Servers.ShouldHaveSingleItem().Id.ShouldBe(oldId);
        entry.DefinitionFor(oldId).ShouldNotBeNull().Credentials.Password.ShouldBe("old-proxy-password-marker");
        new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, TimeProvider.System).SubscriptionUrl(groupId).ShouldBe(OldUrl);
    }

    [Fact]
    public void Observer_failure_after_commit_does_not_report_the_update_as_failed()
    {
        var (catalog, entry, groupId) = Import();
        StageNewSecrets();
        catalog.Changed += (_, _) => throw new InvalidOperationException("test observer failure");

        var result = catalog.TryApplyEntryUpdate(groupId, OldUrl, NewUrl,
            [Profile("22222222-3333-4444-5555-666666666666")], Mutate);

        result.ShouldNotBeNull().Added.ShouldBe(1);
        catalog.SubscriptionUrl(groupId).ShouldBe(NewUrl);
        entry.SubscriptionFor(NewUrl).ShouldNotBeNull();
    }

    private (SqliteServerCatalog Catalog, EntryProxyStore Entry, string GroupId) Import()
    {
        var database = new SqliteDatabase(_paths);
        var catalog = new SqliteServerCatalog(database, _secrets, TimeProvider.System);
        var entry = new EntryProxyStore(database, catalog, _secrets);
        var bundle = new EntryChainBundle("test-device", "test-node", OldUrl,
            new EntryChainProxy("entry.example", 1080, "old-user", "old-proxy-password-marker", false),
            [new EntryChainProfile("test-device:tcp", Profile("11111111-2222-3333-4444-555555555555"))]);
        entry.Import(bundle, "Test chain");
        return (catalog, entry, catalog.Subscriptions.ShouldHaveSingleItem().Id);
    }

    private void StageNewSecrets()
    {
        _secrets.Save("entry-subscription:new-test-key", NewUrl.OriginalString);
        _secrets.Save("entry-proxy:new-test-key", JsonSerializer.Serialize(new ServerCredentials(
            Username: "new-user", Password: "new-proxy-password-marker")));
    }

    private static void Mutate(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE entry_proxies SET credentials_key = 'entry-proxy:new-test-key', "
                + "subscription_key = 'entry-subscription:new-test-key' WHERE device_id = 'test-device'";
            update.ExecuteNonQuery().ShouldBe(1);
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM entry_proxy_bindings WHERE device_id = 'test-device'";
            clear.ExecuteNonQuery();
        }

        using var bind = connection.CreateCommand();
        bind.Transaction = transaction;
        bind.CommandText = "INSERT INTO entry_proxy_bindings (server_id, device_id) "
            + "SELECT id, 'test-device' FROM servers WHERE subscription_id = "
            + "(SELECT group_id FROM entry_proxies WHERE device_id = 'test-device')";
        bind.ExecuteNonQuery().ShouldBeGreaterThan(0);
    }

    private static ServerDefinition Profile(string uuid) => new("TCP", string.Empty, ProxyProtocol.Vless,
        "192.0.2.7", 8443, TransportOptions.Tcp,
        new SecurityOptions(TransportSecurity.Reality, Sni: "front.example", Fingerprint: "chrome",
            RealityPublicKey: new string('A', 43), RealityShortId: "aabb", RealitySpiderX: "/"),
        new ProtocolOptions(Flow: "xtls-rprx-vision"), new ServerCredentials(Uuid: uuid));
}
