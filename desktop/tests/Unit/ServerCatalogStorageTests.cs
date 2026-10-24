using System.Text;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ServerCatalogStorageTests : IDisposable
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _secrets = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 15, 42, 0, TimeSpan.Zero));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Import_keeps_secrets_in_the_os_store_and_out_of_the_database()
    {
        var catalog = Catalog();

        var outcome = catalog.Add(Parse(
            $"vless://{Uuid}@203.0.113.24:443?security=reality&sni=www.example.com&pbk=bbbbbbbbbbbb#%F0%9F%87%B3%F0%9F%87%B1%20NL",
            "trojan://pw-db-marker@198.51.100.201:443#Trojan"), "Основная");

        outcome.ShouldBe(new ImportOutcome(2, 0));
        catalog.Servers.Select(s => (s.Name, s.CountryCode, s.Protocol)).ShouldBe(
            [("NL", "NL", ProxyProtocol.Vless), ("Trojan", "", ProxyProtocol.Trojan)]);
        catalog.Subscriptions.ShouldHaveSingleItem().Name.ShouldBe("Основная");
        catalog.Servers.ShouldAllBe(s => s.SubscriptionId == catalog.Subscriptions[0].Id);
        catalog.Selected!.Name.ShouldBe("NL", "своего выбора не было — выбран первый добавленный");
        _secrets.Values.Keys.ShouldAllBe(k => k.StartsWith("server:srv-", StringComparison.Ordinal));
        _secrets.Values.Values.ShouldContain(v => v.Contains("pw-db-marker", StringComparison.Ordinal));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var database = Encoding.UTF8.GetString(File.ReadAllBytes(_paths.Database));
        database.ShouldNotContain("pw-db-marker");
        database.ShouldNotContain(Uuid);
    }

    [Fact]
    public void Full_definition_with_secrets_comes_back_for_connecting()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-connect@198.51.100.201:443?sni=example.com&type=ws&path=%2Ftr#T"), null);

        var definition = catalog.Definition(catalog.Servers[0].Id).ShouldNotBeNull();

        definition.Credentials.Password.ShouldBe("pw-connect");
        definition.Transport.ShouldBe(new TransportOptions(TransportKind.WebSocket, "/tr"));
        definition.Security.Sni.ShouldBe("example.com");
        catalog.Definition("nobody").ShouldBeNull();
    }

    [Fact]
    public void Changes_survive_a_restart()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B"), null);
        var b = catalog.Servers[1].Id;
        _time.Advance(TimeSpan.FromMinutes(3));
        catalog.Choose(b);
        catalog.SetFavorite(b, true);
        catalog.Rename(b, "  Мой сервер ");

        var reopened = Catalog();

        reopened.Selected!.Id.ShouldBe(b);
        var server = reopened.Servers.Single(s => s.Id == b);
        server.Name.ShouldBe("Мой сервер");
        server.IsFavorite.ShouldBeTrue();
        server.LastUsedAt.ShouldBe(_time.GetUtcNow());
        reopened.Subscriptions.ShouldBeEmpty("ссылки без названия — в «Мои серверы»");
    }

    [Fact]
    public void Same_server_twice_is_a_duplicate_but_other_credentials_are_not()
    {
        var catalog = Catalog();
        var links = Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-a@198.51.100.1:443#A again");

        catalog.Add(links, null).ShouldBe(new ImportOutcome(1, 1), "второй — дубль внутри одного списка");
        catalog.CountDuplicates(links).ShouldBe(2);
        catalog.Add(links, null).ShouldBe(new ImportOutcome(0, 2));
        catalog.Add(Parse("trojan://pw-other@198.51.100.1:443#A2"), null).ShouldBe(new ImportOutcome(1, 0));
        catalog.Servers.Count.ShouldBe(2);
    }

    [Fact]
    public void Removing_a_server_deletes_its_secret_and_selection()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443#A"), null);
        var id = catalog.Selected!.Id;

        catalog.Remove(id);

        catalog.Servers.ShouldBeEmpty();
        catalog.Selected.ShouldBeNull();
        _secrets.Values.ShouldBeEmpty();
        Catalog().Selected.ShouldBeNull();
    }

    [Fact]
    public void Without_a_secret_store_nothing_is_saved()
    {
        _secrets.Available = false;
        var catalog = Catalog();

        Should.Throw<InvalidOperationException>(() => catalog.Add(Parse("trojan://pw-a@198.51.100.1:443#A"), "Группа"));

        catalog.Servers.ShouldBeEmpty();
        catalog.Subscriptions.ShouldBeEmpty();
        Catalog().Servers.ShouldBeEmpty();
    }

    [Fact]
    public void Store_failing_midway_leaves_no_secrets_and_no_rows()
    {
        _secrets.SavesLeft = 2;
        var catalog = Catalog();
        var links = Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B");

        Should.Throw<InvalidOperationException>(() =>
            catalog.Add(links, "Группа", new Uri("https://sub.example.com/sub/fake-token-3")));

        _secrets.Values.ShouldBeEmpty("адрес подписки и первый сервер записаны до сбоя — их убрали");
        catalog.Servers.ShouldBeEmpty();
        catalog.Selected.ShouldBeNull();
        catalog.Subscriptions.ShouldBeEmpty();
        var reopened = Catalog();
        reopened.Servers.ShouldBeEmpty();
        reopened.Subscriptions.ShouldBeEmpty();

        _secrets.SavesLeft = null;
        catalog.Add(links, "Группа").ShouldBe(new ImportOutcome(2, 0), "после сбоя импорт проходит заново");
    }

    [Fact]
    public void Demo_servers_show_until_the_first_import()
    {
        var user = Catalog();
        var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time));
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        catalog.IsDemo.ShouldBeTrue();
        catalog.Servers.Count.ShouldBe(7);
        catalog.Selected!.Name.ShouldBe("Нидерланды 01");

        user.Add(Parse("trojan://pw-a@198.51.100.1:443#Мой"), null);

        changes.ShouldBe(1);
        catalog.IsDemo.ShouldBeFalse();
        catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("Мой");
        catalog.Selected!.Name.ShouldBe("Мой");
    }

    private SqliteServerCatalog Catalog() => new(new SqliteDatabase(_paths), _secrets, _time);

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];

    private sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Available { get; set; } = true;

        public string Name => "Тестовое хранилище";

        public bool IsAvailable => Available;

        /// <summary>Сколько записей пройдет до отказа хранилища; null — без отказа.</summary>
        public int? SavesLeft { get; set; }

        public void Save(string key, string secret)
        {
            if (!Available || SavesLeft-- <= 0)
            {
                throw new InvalidOperationException();
            }

            Values[key] = secret;
        }

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public void Delete(string key) => Values.Remove(key);
    }
}
