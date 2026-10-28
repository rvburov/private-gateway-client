using System.Text;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ServerCatalogStorageTests : IDisposable
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";

    private const string Pin = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private const string SelfSigned = "trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#Self-signed";

    private const string Pinned = "trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=" + Pin + "#Pinned";

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
    public void Same_server_imported_again_takes_the_pinned_certificate_and_keeps_its_name()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#Self-signed"), null).ShouldBe(new ImportOutcome(1, 0));
        var id = catalog.Servers.ShouldHaveSingleItem().Id;
        catalog.Rename(id, "Мой сервер");

        // Тот же сервер с закрепленным сертификатом — дубль, но защита берется из новой ссылки (решение 484).
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90#Pinned"), null).ShouldBe(new ImportOutcome(0, 1, 1));

        var server = catalog.Definition(id).ShouldNotBeNull();
        server.Security.PinnedPeerCertSha256.ShouldBe(["a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"]);
        server.Security.AllowInsecure.ShouldBeFalse();
        catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("Мой сервер", "свое название, группа и выбор остаются");

        // Сертификат сменился — новая ссылка заменяет закрепление.
        catalog.Add(Parse("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0#Pinned"), null).ShouldBe(new ImportOutcome(0, 1, 1));
        catalog.Definition(id)!.Security.PinnedPeerCertSha256.ShouldBe(["0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0"]);
        Catalog().Definition(id)!.Security.PinnedPeerCertSha256.ShouldBe(["0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0"], "записано в базу");
    }

    [Fact]
    public void Renewal_is_counted_before_import_and_notifies_once_while_the_same_link_again_changes_nothing()
    {
        var catalog = Catalog();
        catalog.Add(Parse(SelfSigned), null);
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        catalog.CountRenewals(Parse(Pinned)).ShouldBe(1, "предпросмотр видит, что у сохраненного обновится защита");
        catalog.Add(Parse(Pinned), null).ShouldBe(new ImportOutcome(0, 1, 1));
        changes.ShouldBe(1);

        catalog.CountRenewals(Parse(Pinned)).ShouldBe(0);
        catalog.Add(Parse(Pinned), null).ShouldBe(new ImportOutcome(0, 1, 0), "та же ссылка еще раз ничего не меняет");
        changes.ShouldBe(1);
    }

    [Fact]
    public void Same_server_twice_in_one_import_takes_the_first_one_that_differs_like_the_phones()
    {
        var catalog = Catalog();
        catalog.Add(Parse(SelfSigned), null);

        catalog.Add(Parse(SelfSigned, Pinned), null).ShouldBe(new ImportOutcome(0, 2, 1));

        catalog.Definition(catalog.Servers[0].Id)!.Security.PinnedPeerCertSha256.ShouldBe([Pin]);
    }

    [Fact]
    public void Server_of_a_subscription_with_an_address_takes_its_protection_only_from_the_refresh()
    {
        var catalog = Catalog();
        catalog.Add(Parse(SelfSigned), "Провайдер", new Uri("https://example.com/sub/not-a-real-token"));
        catalog.Add(Parse("trojan://pw-b@198.51.100.2:443?sni=example.com&allowInsecure=1#B"), "Из файла");
        var subscribed = catalog.Servers.Single(s => s.Name == "Self-signed").Id;
        var fromFile = catalog.Servers.Single(s => s.Name == "B").Id;

        // Сервер подписки с адресом ведет ее обновление, иначе импорт и обновление переписывали бы защиту по очереди;
        // группа без адреса (профиль из файла) не обновляется — ее сервер берет защиту из импорта, как «Мои серверы».
        var outcome = catalog.Add(Parse(Pinned, $"trojan://pw-b@198.51.100.2:443?sni=example.com&pcs={Pin}#B"), null);

        outcome.ShouldBe(new ImportOutcome(0, 2, 1));
        catalog.Definition(subscribed)!.Security.PinnedPeerCertSha256.ShouldBeNull();
        catalog.Definition(subscribed)!.Security.AllowInsecure.ShouldBeTrue();
        catalog.Definition(fromFile)!.Security.PinnedPeerCertSha256.ShouldBe([Pin]);
    }

    [Fact]
    public void Failed_import_leaves_the_protection_of_saved_servers_as_it_was()
    {
        var catalog = Catalog();
        catalog.Add(Parse(SelfSigned), null);
        var id = catalog.Servers[0].Id;

        // Защита обновляется в той же транзакции, что и новые серверы: хранилище секретов отказало — не меняется ничего.
        _secrets.SavesLeft = 0;
        Should.Throw<InvalidOperationException>(() => catalog.Add(Parse(Pinned, "trojan://pw-new@198.51.100.3:443#New"), null));

        catalog.Servers.ShouldHaveSingleItem();
        catalog.Definition(id)!.Security.PinnedPeerCertSha256.ShouldBeNull();
        Catalog().Definition(id)!.Security.AllowInsecure.ShouldBeTrue("и в базе");
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
        var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), new SimulatedGatewayClient(_time));
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

    [Fact]
    public void With_the_real_service_there_are_no_demo_servers()
    {
        var catalog = new UserOrDemoCatalog(Catalog(), new DemoServerCatalog(_time), new LatencyGateway(new Dictionary<string, LatencyResult>()));

        catalog.IsDemo.ShouldBeFalse("к демо-серверам служба не подключится");
        catalog.Servers.ShouldBeEmpty();
        catalog.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task Ping_of_own_servers_comes_from_the_service_and_is_kept()
    {
        var user = Catalog();
        user.Add(Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B", "trojan://pw-c@198.51.100.3:443#C"), null);
        var ids = user.Servers.ToDictionary(s => s.Name, s => s.Id);
        var gateway = new LatencyGateway(new Dictionary<string, LatencyResult>
        {
            [ids["A"]] = new(42, null),
            [ids["B"]] = LatencyResult.Failed(LatencyFailure.NoResponse),
        });
        var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway);

        (await catalog.RefreshAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        gateway.Asked.ShouldBe([ids["A"], ids["B"], ids["C"]], ignoreOrder: true);
        Catalog().Servers.ToDictionary(s => s.Name, s => s.LatencyMs).ShouldBe(
            new Dictionary<string, int?> { ["A"] = 42, ["B"] = null, ["C"] = null }, "после перезапуска — тоже");

        var silent = new UserOrDemoCatalog(Catalog(), new DemoServerCatalog(_time), new SimulatedGatewayClient(_time));
        (await silent.RefreshAsync(TestContext.Current.CancellationToken)).ShouldBeFalse("службы нет — пинг не мерился");
        silent.Servers.Single(s => s.Name == "A").LatencyMs.ShouldBe(42, "прежнее значение не стирается");
    }

    private SqliteServerCatalog Catalog() => new(new SqliteDatabase(_paths), _secrets, _time);

    /// <summary>Служба, которая меряет пинг заданными значениями и запоминает, кого спросили.</summary>
    private sealed class LatencyGateway(IReadOnlyDictionary<string, LatencyResult> results) : IGatewayClient
    {
        public List<string> Asked { get; } = [];

        public ConnectionStatus Status { get; } = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);

        public ServiceKind Service => ServiceKind.Installed;

        public IReadOnlyList<JournalEntry> Journal => [];

        public event EventHandler<ConnectionStatus>? StatusChanged { add { } remove { } }

        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }

        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default)
        {
            Asked.AddRange(serverIds);
            return Task.FromResult(results);
        }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));
    }

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];
}
