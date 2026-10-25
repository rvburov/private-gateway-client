using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Обновление подписок (доска 12): что остается, что уходит, что с выбранным сервером, когда
/// обновлять и когда нельзя. База — настоящая SQLite во временной папке, сеть и служба — подделки.
/// </summary>
public sealed class SubscriptionUpdateTests : IDisposable
{
    private static readonly Uri Url = new("https://sub.example.com/sub/fake-token-1");

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _secrets = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 15, 42, 0, TimeSpan.Zero));
    private readonly MemorySettings _settingsStore = new();
    private readonly FakeGateway _gateway = new();
    private readonly FakeDownloader _downloads = new();
    private SettingsService? _settings;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    // ── Хранилище: как список заменяется ─────────────────────────────────────

    [Fact]
    public void Update_keeps_what_the_user_changed_adds_new_and_removes_gone()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B", "trojan://pw-c@198.51.100.3:443#C");
        var b = ServerId(catalog, "B");
        var c = ServerId(catalog, "C");
        catalog.SetFavorite(b, true);
        catalog.Rename(b, "Мой B");
        _time.Advance(TimeSpan.FromHours(1));

        var update = catalog.ApplyUpdate(id, Parse(
            "trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443?sni=example.org#B2", "trojan://pw-d@198.51.100.4:443#D"));

        update.ShouldBe(new SubscriptionUpdate(Added: 1, Removed: 1, Kept: 2, SelectedServerChange.None));
        var kept = catalog.Servers.Single(s => s.Id == b);
        kept.Name.ShouldBe("Мой B", "свое название переживает обновление");
        kept.IsFavorite.ShouldBeTrue();
        catalog.Servers.Select(s => s.Name).ShouldBe(["A", "Мой B", "D"], ignoreOrder: true);
        _secrets.Values.ShouldNotContainKey("server:" + c);
        catalog.Subscriptions.Single().UpdatedAt.ShouldBe(_time.GetUtcNow());

        var reopened = Catalog();
        reopened.Servers.Select(s => s.Name).ShouldBe(["A", "Мой B", "D"], ignoreOrder: true);
        reopened.Definition(b)!.Security.Sni.ShouldBe("example.org", "описание взято из новой версии подписки");
    }

    [Fact]
    public void Selected_server_that_disappeared_stays_until_another_is_chosen()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B");
        var a = catalog.Selected!.Id;

        var update = catalog.ApplyUpdate(id, Parse("trojan://pw-b@198.51.100.2:443#B"));

        update.Selected.ShouldBe(SelectedServerChange.Missing);
        update.Removed.ShouldBe(0, "выбранный не удаляется");
        catalog.Selected!.Id.ShouldBe(a, "выбор не меняется сам");
        catalog.Selected.MissingSince.ShouldBe(_time.GetUtcNow());
        Catalog().Selected!.MissingSince.ShouldBe(_time.GetUtcNow());

        catalog.Choose(ServerId(catalog, "B"));
        catalog.ApplyUpdate(id, Parse("trojan://pw-b@198.51.100.2:443#B")).Removed.ShouldBe(1);
        catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("B");
        _secrets.Values.ShouldNotContainKey("server:" + a);
    }

    [Fact]
    public void Selected_server_with_new_keys_is_reported_as_changed_and_not_switched()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        var a = catalog.Selected!.Id;

        var update = catalog.ApplyUpdate(id, Parse("trojan://pw-new@198.51.100.1:443#A"));

        update.Selected.ShouldBe(SelectedServerChange.Changed);
        catalog.Selected!.Id.ShouldBe(a);
        catalog.Servers.Count.ShouldBe(2, "новая версия — рядом, выбирает пользователь");
    }

    [Fact]
    public void Server_that_came_back_is_no_longer_missing()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B");
        catalog.ApplyUpdate(id, Parse("trojan://pw-b@198.51.100.2:443#B"));

        catalog.ApplyUpdate(id, Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B"))
            .Selected.ShouldBe(SelectedServerChange.None);

        catalog.Selected!.MissingSince.ShouldBeNull();
        Catalog().Selected!.MissingSince.ShouldBeNull();
    }

    [Fact]
    public void Servers_already_in_another_group_are_not_duplicated()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-x@198.51.100.9:443#X"), null);
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");

        catalog.ApplyUpdate(id, Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-x@198.51.100.9:443#X")).Added.ShouldBe(0);

        catalog.Servers.Count(s => s.Name == "X").ShouldBe(1);
    }

    [Fact]
    public void Empty_list_never_replaces_a_subscription()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");

        Should.Throw<ArgumentException>(() => catalog.ApplyUpdate(id, []));

        catalog.Servers.ShouldHaveSingleItem();
    }

    [Fact]
    public void Editing_renames_and_keeps_the_new_address_only_in_the_os_store()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        catalog.MarkUpdateFailed(id, DownloadFailure.Status, 403);
        var newUrl = new Uri("https://other.example.org/clash/fake-token-2?flag=1");

        catalog.EditSubscription(id, "  Резервная ", newUrl);

        var subscription = Catalog().Subscriptions.ShouldHaveSingleItem();
        subscription.Name.ShouldBe("Резервная");
        subscription.Address.ShouldBe("https://other.example.org/clash/••••");
        subscription.LastFailure.ShouldBeNull("новый адрес — прошлая ошибка к нему не относится");
        catalog.SubscriptionUrl(id).ShouldBe(newUrl);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.ReadAllText(_paths.Database).ShouldNotContain("fake-token-2");
    }

    [Fact]
    public void Removing_a_subscription_takes_its_servers_secrets_and_selection()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-x@198.51.100.9:443#X"), null);
        catalog.Choose(ServerId(catalog, "X"));
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        catalog.Choose(ServerId(catalog, "A"));

        catalog.RemoveSubscription(id);

        catalog.Subscriptions.ShouldBeEmpty();
        catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("X");
        catalog.Selected.ShouldBeNull("удаленный сервер был выбран — выбора больше нет");
        _secrets.Values.Keys.ShouldBe(["server:" + ServerId(catalog, "X")]);
        Catalog().Servers.ShouldHaveSingleItem();
        Catalog().Selected.ShouldBeNull();
    }

    [Fact]
    public void Removing_my_servers_leaves_subscriptions_alone()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-x@198.51.100.9:443#X"), null);
        Import(catalog, "trojan://pw-a@198.51.100.1:443#A");

        catalog.RemoveSubscription(null);

        catalog.Servers.ShouldHaveSingleItem().Name.ShouldBe("A");
        catalog.Subscriptions.ShouldHaveSingleItem();
    }

    [Fact]
    public void Address_of_a_subscription_saved_before_migration_4_comes_from_the_os_store()
    {
        var catalog = Catalog();
        Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        using (var connection = new SqliteDatabase(_paths).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE subscriptions SET address = NULL";
            command.ExecuteNonQuery();
        }

        Catalog().Subscriptions.Single().Address.ShouldBe("https://sub.example.com/sub/••••");
    }

    // ── Обновитель: когда и как ──────────────────────────────────────────────

    [Fact]
    public async Task Failure_keeps_the_previous_list_and_remembers_why()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        _downloads.Failure = new SubscriptionDownloadException(DownloadFailure.Status, 403);

        var result = await Updater(catalog).UpdateAsync(id, Ct);

        result.ShouldBe(new UpdateResult(null, DownloadFailure.Status, 403));
        catalog.Servers.ShouldHaveSingleItem();
        var subscription = Catalog().Subscriptions.Single();
        (subscription.LastFailure, subscription.LastStatusCode, subscription.LastAttemptAt).ShouldBe((DownloadFailure.Status, 403, _time.GetUtcNow()));
    }

    [Fact]
    public async Task Answer_without_servers_keeps_the_previous_list()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        _downloads.Text = "<html>не подписка</html>";

        (await Updater(catalog).UpdateAsync(id, Ct)).Failure.ShouldBe(DownloadFailure.NoServers);

        catalog.Servers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Successful_update_replaces_the_list_and_clears_the_last_failure()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        catalog.MarkUpdateFailed(id, DownloadFailure.Timeout);
        _downloads.Text = "trojan://pw-a@198.51.100.1:443#A\ntrojan://pw-b@198.51.100.2:443#B\nbroken";

        var result = await Updater(catalog).UpdateAsync(id, Ct);

        result.Update.ShouldBe(new SubscriptionUpdate(1, 0, 1, SelectedServerChange.None));
        _downloads.Requested.ShouldBe([Url]);
        catalog.Subscriptions.Single().LastFailure.ShouldBeNull();
    }

    [Fact]
    public async Task Only_via_vpn_waits_for_protection_through_the_real_service()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        var updater = Updater(catalog);

        _gateway.Simulated = true;
        (await updater.UpdateAsync(id, Ct)).Block.ShouldBe(UpdateBlock.ServiceMissing);
        _gateway.Simulated = false;
        _gateway.State = ConnectionState.Blocked;
        (await updater.UpdateAsync(id, Ct)).Block.ShouldBe(UpdateBlock.ProtectionOff);
        _downloads.Requested.ShouldBeEmpty("адрес подписки не ушел в сеть мимо VPN");

        _gateway.State = ConnectionState.Connected;
        (await updater.UpdateAsync(id, Ct)).IsUpdated.ShouldBeTrue();

        _gateway.State = ConnectionState.Off;
        Settings().Update(s => s with { UpdateOnlyViaVpn = false });
        (await updater.UpdateAsync(id, Ct)).IsUpdated.ShouldBeTrue("настройка выключена — напрямую можно");
    }

    [Fact]
    public async Task Missing_selected_server_is_announced_only_when_asked()
    {
        var catalog = Catalog();
        var id = Import(catalog, "trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B");
        var updater = Updater(catalog);
        var notices = new List<SelectedServerNotice>();
        updater.SelectedServerChanged += (_, n) => notices.Add(n);
        _downloads.Text = "trojan://pw-b@198.51.100.2:443#B";

        await updater.UpdateAsync(id, Ct);
        notices.ShouldBe([new SelectedServerNotice("Основная", "A", SelectedServerChange.Missing)]);

        Settings().Update(s => s with { NotifySelectedServerChange = false });
        await updater.UpdateAsync(id, Ct);
        notices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Due_subscriptions_follow_the_interval_and_retry_failures_hourly()
    {
        var catalog = Catalog();
        Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
        catalog.Add(Parse("trojan://pw-b@198.51.100.2:443#B"), "Без адреса");
        var updater = Updater(catalog);

        _time.Advance(TimeSpan.FromHours(11));
        updater.Due().ShouldBeEmpty("12 часов еще не прошли");
        _time.Advance(TimeSpan.FromHours(1));
        updater.Due().ShouldHaveSingleItem().Name.ShouldBe("Основная", "группа без адреса не обновляется");

        _downloads.Failure = new SubscriptionDownloadException(DownloadFailure.Timeout);
        await updater.UpdateDueAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(59));
        updater.Due().ShouldBeEmpty("после неудачи — не чаще раза в час");
        _time.Advance(TimeSpan.FromMinutes(1));
        updater.Due().ShouldHaveSingleItem();

        Settings().Update(s => s with { SubscriptionRefresh = SubscriptionRefresh.Off });
        updater.Due().ShouldBeEmpty("автообновление выключено");
    }

    [Fact]
    public void Schedule_runs_by_the_program_clock_and_after_protection_comes_up()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var catalog = Catalog();
            Import(catalog, "trojan://pw-a@198.51.100.1:443#A");
            using var updater = Updater(catalog);
            _time.Advance(TimeSpan.FromHours(12));
            _gateway.State = ConnectionState.Off;
            updater.Start();

            _time.Advance(SubscriptionUpdater.FirstCheck);
            _downloads.Requested.ShouldBeEmpty("защита выключена, а обновлять — только через VPN");

            _gateway.State = ConnectionState.Connected;
            _downloads.Requested.ShouldHaveSingleItem("сразу после включения защиты");
            _time.Advance(SubscriptionUpdater.CheckPeriod);
            _downloads.Requested.Count.ShouldBe(1, "следующий раз — через 12 часов");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    [Fact]
    public async Task Group_without_an_address_cannot_be_updated()
    {
        var catalog = Catalog();
        catalog.Add(Parse("trojan://pw-b@198.51.100.2:443#B"), "Без адреса");

        (await Updater(catalog).UpdateAsync(catalog.Subscriptions.Single().Id, Ct)).Failure.ShouldBe(DownloadFailure.NoAddress);
    }

    // ── Подпись адреса ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com/sub/0123456789abcdef", "https://example.com/sub/••••")]
    [InlineData("https://example.org/clash/abc?token=1", "https://example.org/clash/••••")]
    [InlineData("https://sub.example.com/api/v1/client/subscribe?token=fake", "https://sub.example.com/api/••••")]
    [InlineData("https://example.com/AbCdEf0123456789xyz/clash", "https://example.com/••••")]
    [InlineData("https://example.com/0123456789abcdef", "https://example.com/••••")]
    [InlineData("https://example.com/AbCdEfGhIjKlMnOpQr/clash", "https://example.com/••••")]
    [InlineData("https://example.com/?token=fake", "https://example.com/••••")]
    [InlineData("https://user:pass@example.com:8443/sub", "https://example.com:8443/sub")]
    [InlineData("http://203.0.113.5:2096/sub/fake-id", "http://203.0.113.5:2096/sub/••••")]
    [InlineData("https://example.com", "https://example.com")]
    public void Address_shows_where_but_never_the_token(string url, string expected)
    {
        SubscriptionAddress.Mask(new Uri(url)).ShouldBe(expected);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SqliteServerCatalog Catalog() => new(new SqliteDatabase(_paths), _secrets, _time);

    private SettingsService Settings() => _settings ??= new SettingsService(_settingsStore);

    private SubscriptionUpdater Updater(SqliteServerCatalog catalog) =>
        new(catalog, _downloads, _gateway, Settings(), _time, NullLogger<SubscriptionUpdater>.Instance);

    private string Import(SqliteServerCatalog catalog, params string[] links)
    {
        catalog.Add(Parse(links), "Основная", Url);
        _downloads.Text ??= string.Join('\n', links);
        return catalog.Subscriptions.Single(s => s.Name == "Основная").Id;
    }

    private static string ServerId(SqliteServerCatalog catalog, string name) => catalog.Servers.Single(s => s.Name == name).Id;

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _value = AppSettings.Default;

        public AppSettings Load() => _value;

        public void Save(AppSettings settings) => _value = settings;
    }

    /// <summary>Загрузка без сети: один ответ на любой адрес или ошибка.</summary>
    private sealed class FakeDownloader : ISubscriptionDownloader
    {
        public string? Text { get; set; }

        public SubscriptionDownloadException? Failure { get; set; }

        public List<Uri> Requested { get; } = [];

        public Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Requested.Add(url);
            return Failure is { } failure
                ? Task.FromException<SubscriptionContent>(failure)
                : Task.FromResult(new SubscriptionContent(Text ?? string.Empty, null));
        }
    }

    /// <summary>Служба: настоящая или имитация, состояние задает тест.</summary>
    private sealed class FakeGateway : IGatewayClient
    {
        private ConnectionState _state = ConnectionState.Connected;

        public bool Simulated { get; set; }

        public ConnectionState State
        {
            get => _state;
            set
            {
                _state = value;
                StatusChanged?.Invoke(this, Status);
            }
        }

        public ConnectionStatus Status => ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with { State = _state };

        public bool IsSimulated => Simulated;

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;


        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));

        public IReadOnlyList<JournalEntry> Journal => [];

        // Обновитель журнал службы не читает.
        public event EventHandler<JournalEntry>? JournalAdded
        {
            add { }
            remove { }
        }
    }
}
