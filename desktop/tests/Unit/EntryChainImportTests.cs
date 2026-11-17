using System.Text;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class EntryChainImportTests : IDisposable
{
    private const string AccessToken = "device-scoped-token-marker";
    private const string ProxyUser = "entry-user-marker";
    private const string ProxyPassword = "entry-password-marker";
    private static readonly string PublicKey = new('A', 43);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-entry-chain-" + Guid.NewGuid().ToString("N")));
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
    public void Tcp_and_xhttp_reality_profiles_are_imported_as_one_chain_without_exposing_secrets_in_display_strings()
    {
        var parsed = EntryChainImport.TryParse(Bundle(Tcp, Xhttp)).ShouldNotBeNull();

        parsed.Batch.Format.ShouldBe(ImportParser.FormatEntryChain);
        parsed.Batch.Failed.ShouldBe(0);
        var chain = parsed.Bundle.ShouldNotBeNull();
        chain.Profiles.Count.ShouldBe(2);
        chain.Profiles[0].Server.Protocol.ShouldBe(ProxyProtocol.Vless);
        chain.Profiles[0].Server.Transport.Kind.ShouldBe(TransportKind.Tcp);
        chain.Profiles[0].Server.Options.Flow.ShouldBe("xtls-rprx-vision");
        chain.Profiles[1].Server.Transport.Kind.ShouldBe(TransportKind.Xhttp);
        chain.EntryProxy.SupportsUdp.ShouldBeFalse();
        chain.SubscriptionUrl.AbsoluteUri.ShouldContain(AccessToken);
        chain.EntryProxy.ToString().ShouldNotContain(ProxyUser);
        chain.EntryProxy.ToString().ShouldNotContain(ProxyPassword);
        chain.ToString().ShouldNotContain(AccessToken);
        chain.ToString().ShouldNotContain(ProxyPassword);
    }

    [Fact]
    public void Mixed_h3_bundle_keeps_tcp_siblings_and_shows_h3_error_even_with_udp_capable_proxy()
    {
        var result = EntryChainImport.TryParse(Bundle(Tcp, H3).Replace("\"supports_udp\":false", "\"supports_udp\":true", StringComparison.Ordinal))
            .ShouldNotBeNull();

        result.Bundle.ShouldNotBeNull().Profiles.ShouldHaveSingleItem();
        result.Batch.Failed.ShouldBe(1);
        result.Batch.Lines[1].Result.Error.ShouldBe(ImportErrors.EntryH3Unsupported);
        EntryChainImport.TryParse(Bundle(H3)).ShouldNotBeNull().Bundle.ShouldBeNull();
    }

    [Fact]
    public void No_auth_socks_and_empty_reality_short_id_may_omit_their_optional_fields()
    {
        var noAuth = Bundle(Tcp.Replace("\"short_id\":\"aabb\",", string.Empty, StringComparison.Ordinal))
            .Replace($"\"username\":\"{ProxyUser}\",\"password\":\"{ProxyPassword}\",", string.Empty, StringComparison.Ordinal);

        var chain = EntryChainImport.TryParse(noAuth).ShouldNotBeNull().Bundle.ShouldNotBeNull();

        chain.EntryProxy.Username.ShouldBeEmpty();
        chain.EntryProxy.Password.ShouldBeEmpty();
        chain.Profiles[0].Server.Security.RealityShortId.ShouldBeEmpty();
        var broken = noAuth.Replace("\"supports_udp\":false", "\"username\":\"alone\",\"supports_udp\":false", StringComparison.Ordinal);
        EntryChainImport.TryParse(broken).ShouldNotBeNull().Batch.Lines.ShouldHaveSingleItem().Result.Error
            .ShouldBe(ImportErrors.NoCredentials);
    }

    [Theory]
    [InlineData("https://config.example.com/sub/entry/")]
    [InlineData("http://config.example.com/sub/entry/device-scoped-token-marker")]
    [InlineData("https://user:pass@config.example.com/sub/entry/device-scoped-token-marker")]
    [InlineData("https://config.example.com/sub/entry/device-scoped-token-marker#secret")]
    public void Subscription_url_must_be_a_private_https_entry_endpoint(string url)
    {
        var result = EntryChainImport.TryParse(Bundle(Tcp).Replace(SubscriptionUrl, url, StringComparison.Ordinal)).ShouldNotBeNull();

        result.Bundle.ShouldBeNull();
        result.Batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.EntryBundleInvalid);
    }

    [Fact]
    public void A_dns_name_as_vless_target_is_rejected_before_it_can_be_resolved_outside_the_proxy()
    {
        var result = EntryChainImport.TryParse(Bundle(Tcp.Replace("192.0.2.7", "vpn.example.com", StringComparison.Ordinal)))
            .ShouldNotBeNull();

        result.Bundle.ShouldBeNull();
        result.Batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.EntryBundleInvalid);
    }

    [Fact]
    public void The_os_secret_store_holds_proxy_credentials_and_subscription_token_and_updates_inherit_the_entry()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();

        store.Import(chain, "Entry group").Added.ShouldBe(1);
        var first = catalog.Servers.ShouldHaveSingleItem();
        var groupId = first.SubscriptionId.ShouldNotBeNull();
        store.DefinitionFor(first.Id).ShouldNotBeNull().Credentials.Password.ShouldBe(ProxyPassword);
        var subscription = store.SubscriptionFor(chain.SubscriptionUrl).ShouldNotBeNull();
        subscription.Url.AbsoluteUri.ShouldContain(AccessToken);
        subscription.Proxy.Credentials.Username.ShouldBe(ProxyUser);
        subscription.ToString().ShouldNotContain(AccessToken);
        _secrets.Values.Values.Any(value => value.Contains(AccessToken, StringComparison.Ordinal)).ShouldBeTrue();

        var replacement = chain.Profiles[0].Server with { Host = "192.0.2.8" };
        catalog.ApplyUpdate(groupId, [replacement]);
        var updated = catalog.Servers.Single(server => server.Host == "192.0.2.8");
        store.DefinitionFor(updated.Id).ShouldNotBeNull().Credentials.Password.ShouldBe(ProxyPassword);

        SqliteConnection.ClearAllPools();
        var disk = string.Join('\n', Directory.GetFiles(_paths.DataDirectory).Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        disk.ShouldNotContain(ProxyUser);
        disk.ShouldNotContain(ProxyPassword);
        disk.ShouldNotContain(AccessToken);
    }

    [Fact]
    public void Missing_proxy_or_subscription_secret_fails_closed()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, null);
        var id = catalog.Servers.ShouldHaveSingleItem().Id;

        var credentialKey = _secrets.Values.Keys.Single(key => key.StartsWith("entry-proxy:", StringComparison.Ordinal));
        var credentials = _secrets.Values[credentialKey];
        _secrets.Values.Remove(credentialKey);
        Should.Throw<SecretStoreException>(() => store.DefinitionFor(id));
        _secrets.Values[credentialKey] = credentials;
        var subscriptionKey = _secrets.Values.Keys.Single(key => key.StartsWith("entry-subscription:", StringComparison.Ordinal));
        _secrets.Values.Remove(subscriptionKey);
        Should.Throw<SecretStoreException>(() => store.SubscriptionFor(chain.SubscriptionUrl));
        Should.Throw<SecretStoreException>(() => store.SubscriptionFor(new Uri("https://config.example.com/sub/entry/unknown")));
        Should.Throw<SecretStoreException>(() => store.SubscriptionFor(new Uri("http://config.example.com/sub/entry/unknown")));
    }

    [Fact]
    public void A_missing_entry_row_cannot_turn_a_subscription_profile_into_a_direct_connection()
    {
        var catalog = Catalog();
        var database = new SqliteDatabase(_paths);
        var store = new EntryProxyStore(database, catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, "Entry group");
        var id = catalog.Servers.ShouldHaveSingleItem().Id;
        using (var connection = database.Open())
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM entry_proxies WHERE device_id = 'device-7'";
            delete.ExecuteNonQuery().ShouldBe(1);
        }

        Should.Throw<SecretStoreException>(() => store.DefinitionFor(id));
    }

    [Fact]
    public void Restored_chain_profiles_fail_closed_until_the_bundle_is_imported_again()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, "Entry group");
        var saved = catalog.Snapshot();

        catalog.Restore(saved);
        var restored = catalog.Servers.ShouldHaveSingleItem();
        Should.Throw<SecretStoreException>(() => store.DefinitionFor(restored.Id));

        store.Import(chain, "Entry group").Duplicates.ShouldBe(1);
        catalog.Subscriptions.ShouldHaveSingleItem();
        store.DefinitionFor(restored.Id).ShouldNotBeNull().Credentials.Password.ShouldBe(ProxyPassword);
    }

    [Fact]
    public void Reimport_after_restore_replaces_stale_profiles_and_keeps_a_missing_selection()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp, Xhttp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, "Entry group");
        catalog.Restore(catalog.Snapshot());
        var restored = catalog.Servers.Single(server => server.Name == "TCP");
        var staleXhttp = catalog.Servers.Single(server => server.Id != restored.Id);
        catalog.Choose(restored.Id);
        Should.Throw<SecretStoreException>(() => store.DefinitionFor(restored.Id));

        var changed = EntryChainImport.TryParse(Bundle(Tcp.Replace(
            "11111111-2222-3333-4444-555555555555",
            "33333333-4444-5555-6666-777777777777", StringComparison.Ordinal)))
            .ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(changed, "Entry group").Added.ShouldBe(1);

        catalog.Subscriptions.ShouldHaveSingleItem();
        catalog.Servers.Count.ShouldBe(2);
        catalog.Servers.Any(server => server.Id == staleXhttp.Id).ShouldBeFalse();
        catalog.Selected.ShouldNotBeNull().Id.ShouldBe(restored.Id);
        catalog.Selected.MissingSince.ShouldNotBeNull();
        var replacement = catalog.Servers.Single(server => server.Id != restored.Id);
        replacement.MissingSince.ShouldBeNull();
        store.DefinitionFor(replacement.Id).ShouldNotBeNull().Credentials.Password.ShouldBe(ProxyPassword);
    }

    [Fact]
    public void Reimport_rotates_proxy_secret_without_creating_a_second_subscription()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, "Entry group");
        var oldKey = _secrets.Values.Keys.Single(key => key.StartsWith("entry-proxy:", StringComparison.Ordinal));

        store.Import(chain with { EntryProxy = chain.EntryProxy with { Password = "new-secret-marker" } }, "Entry group")
            .Duplicates.ShouldBe(1);

        catalog.Subscriptions.Count.ShouldBe(1);
        _secrets.Values.ContainsKey(oldKey).ShouldBeFalse();
        store.DefinitionFor(catalog.Servers.ShouldHaveSingleItem().Id).ShouldNotBeNull().Credentials.Password.ShouldBe("new-secret-marker");
    }

    [Fact]
    public void Reimport_with_a_rotated_subscription_token_keeps_the_group_and_replaces_the_old_token()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        store.Import(chain, "Entry group");
        var groupId = catalog.Subscriptions.ShouldHaveSingleItem().Id;
        var rotated = new Uri("https://config.example.com/sub/entry/new-device-token-marker");

        store.Import(chain with { SubscriptionUrl = rotated }, "Entry group").Duplicates.ShouldBe(1);

        catalog.Subscriptions.ShouldHaveSingleItem().Id.ShouldBe(groupId);
        catalog.SubscriptionUrl(groupId).ShouldBe(rotated);
        store.SubscriptionFor(rotated).ShouldNotBeNull().Url.ShouldBe(rotated);
        Should.Throw<SecretStoreException>(() => store.SubscriptionFor(chain.SubscriptionUrl));
        _secrets.Values.Values.Any(value => value.Contains(AccessToken, StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void Failed_secret_save_leaves_no_servers_group_or_entry_secret()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var chain = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        _secrets.SavesLeft = 1;

        Should.Throw<InvalidOperationException>(() => store.Import(chain, "Entry group"));

        catalog.Servers.ShouldBeEmpty();
        catalog.Subscriptions.ShouldBeEmpty();
        _secrets.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Two_devices_on_the_same_vps_keep_separate_groups_tokens_and_entry_credentials()
    {
        var catalog = Catalog();
        var store = new EntryProxyStore(new SqliteDatabase(_paths), catalog, _secrets);
        var first = EntryChainImport.TryParse(Bundle(Tcp)).ShouldNotBeNull().Bundle.ShouldNotBeNull();
        var secondText = Bundle(Tcp)
            .Replace("device-7", "device-8", StringComparison.Ordinal)
            .Replace(AccessToken, "other-device-token-marker", StringComparison.Ordinal)
            .Replace("192.0.2.7", "192.0.2.8", StringComparison.Ordinal)
            .Replace(ProxyPassword, "other-device-password", StringComparison.Ordinal);
        var second = EntryChainImport.TryParse(secondText).ShouldNotBeNull().Bundle.ShouldNotBeNull();

        store.Import(first, "Device 7");
        store.Import(second, "Device 8");

        catalog.Subscriptions.Count.ShouldBe(2);
        var firstServer = catalog.Servers.Single(server => server.Host == "192.0.2.7");
        var secondServer = catalog.Servers.Single(server => server.Host == "192.0.2.8");
        store.DefinitionFor(firstServer.Id).ShouldNotBeNull().Credentials.Password.ShouldBe(ProxyPassword);
        store.DefinitionFor(secondServer.Id).ShouldNotBeNull().Credentials.Password.ShouldBe("other-device-password");
        store.SubscriptionFor(first.SubscriptionUrl).ShouldNotBeNull().NodeId.ShouldBe("node-7");
        store.SubscriptionFor(second.SubscriptionUrl).ShouldNotBeNull().NodeId.ShouldBe("node-7");
    }

    private SqliteServerCatalog Catalog() => new(new SqliteDatabase(_paths), _secrets, TimeProvider.System);

    private const string SubscriptionUrl = "https://config.example.com/sub/entry/" + AccessToken;

    private static string Bundle(params string[] profiles) => $$"""
        {"version":1,"device_id":"device-7","node_id":"node-7","subscription_url":"{{SubscriptionUrl}}",
         "entry_proxy":{"type":"socks5","host":"entry.example.com","port":1080,
                        "username":"{{ProxyUser}}","password":"{{ProxyPassword}}","supports_udp":false},
         "profiles":[{{string.Join(",", profiles)}}]}
        """;

    private static string Tcp => $$"""
        {"id":"device-7:tcp","name":"TCP","transport":"tcp","host":"192.0.2.7","port":8444,
         "uuid":"11111111-2222-3333-4444-555555555555","security":"reality","sni":"front.example.com",
         "public_key":"{{PublicKey}}","short_id":"aabb","fingerprint":"chrome",
         "flow":"xtls-rprx-vision","spider_x":"/"}
        """;

    private static string Xhttp => $$"""
        {"id":"device-7:xhttp","name":"XHTTP","transport":"xhttp","host":"192.0.2.7","port":8444,
         "uuid":"22222222-3333-4444-5555-666666666666","security":"reality","sni":"front.example.com",
         "public_key":"{{PublicKey}}","short_id":"aabb","fingerprint":"chrome",
         "path":"/xhttp","mode":"auto"}
        """;

    private const string H3 = """
        {"id":"device-7:h3","security":"tls","alpn":["h3"]}
        """;
}
