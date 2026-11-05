using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ExternalProxyStorageTests : IDisposable
{
    private const string Username = "proxy-user-marker";
    private const string Password = "proxy-password-marker";
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-external-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _secrets = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Credentials_live_only_in_the_os_store_and_both_mode_settings_survive_reopening()
    {
        var settings = Settings();
        var catalog = new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, TimeProvider.System);
        catalog.Add([new ServerDefinition("VPN", string.Empty, ProxyProtocol.Vless, "vpn.example.com", 443,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty,
            new ServerCredentials(Uuid: "11111111-2222-3333-4444-555555555555"))], null);
        var selectedVpn = catalog.Selected!.Id;
        var proxy = new ExternalProxyStore(settings, _secrets);
        proxy.Save(Authenticated(), Username, Password);
        var saved = settings.Current.ExternalProxy;
        settings.Update(s => s with { Mode = ConnectionMode.Proxy, Connection = s.Connection with { ProxyPort = 2080 } });
        settings.Update(s => s with { Mode = ConnectionMode.Vpn });

        var reopened = Settings();
        reopened.Current.ExternalProxy.ShouldBe(saved);
        reopened.Current.Connection.ProxyPort.ShouldBe(2080);
        reopened.Current.Mode.ShouldBe(ConnectionMode.Vpn);
        new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, TimeProvider.System).Selected!.Id.ShouldBe(selectedVpn);
        var definition = new ExternalProxyStore(reopened, _secrets).Definition().ShouldNotBeNull();
        definition.Port.ShouldBe(8443, "внешний порт не меняется вместе с локальным входом");
        definition.Credentials.Username.ShouldBe(Username);
        definition.Credentials.Password.ShouldBe(Password);
        _secrets.Values.Where(pair => ExternalProxySettings.IsCredentialsKey(pair.Key)).ShouldHaveSingleItem().Value.ShouldContain(Username);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var disk = string.Join('\n', Directory.GetFiles(_paths.DataDirectory).Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        disk.ShouldNotContain(Username);
        disk.ShouldNotContain(Password);
        reopened.Current.ToString().ShouldNotContain(Username);
        reopened.Current.ToString().ShouldNotContain(Password);
    }

    [Theory]
    [InlineData(ExternalProxyProtocol.Socks5, ProxyProtocol.Socks5, TransportSecurity.None)]
    [InlineData(ExternalProxyProtocol.Http, ProxyProtocol.Http, TransportSecurity.None)]
    [InlineData(ExternalProxyProtocol.Https, ProxyProtocol.Http, TransportSecurity.Tls)]
    public void Anonymous_forwarding_does_not_require_a_secret_store(ExternalProxyProtocol protocol, ProxyProtocol expected, TransportSecurity security)
    {
        _secrets.Available = false;
        var settings = Settings();
        var proxy = new ExternalProxyStore(settings, _secrets);

        proxy.Save(new ExternalProxySettings(protocol, "proxy.example.com", 8443), string.Empty, string.Empty);

        var definition = proxy.Definition().ShouldNotBeNull();
        definition.Protocol.ShouldBe(expected);
        definition.Security.Kind.ShouldBe(security);
        definition.Security.AllowInsecure.ShouldBeFalse();
        definition.Credentials.ShouldBe(new ServerCredentials());
        settings.Current.ExternalProxy.Revision.ShouldBe(1);
        _secrets.Values.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_or_unavailable_required_secret_never_becomes_anonymous_access()
    {
        var settings = Settings();
        var proxy = new ExternalProxyStore(settings, _secrets);
        proxy.Save(Authenticated(), Username, Password);
        var key = settings.Current.ExternalProxy.CredentialsKey!;
        var envelope = _secrets.Values[key];
        new ExternalProxyStore(settings, new RefusingSecrets()).ReadCredentials().ShouldBeNull();
        new ExternalProxyStore(settings, new RefusingSecrets()).Definition().ShouldBeNull();
        _secrets.Values.Remove(key);
        proxy.ReadCredentials().ShouldBeNull();
        proxy.Definition().ShouldBeNull();

        _secrets.Values[key] = envelope;
        _secrets.Available = false;
        proxy.CanStoreSecrets.ShouldBeFalse();
        proxy.ReadCredentials().ShouldBeNull();
        proxy.Definition().ShouldBeNull();
        settings.Current.ExternalProxy.Authentication.ShouldBe(ProxyAuthentication.UserPassword);

        _secrets.Available = true;
        _secrets.Values[key] = "not-json";
        proxy.Definition().ShouldBeNull();
    }

    [Fact]
    public void A_reference_to_another_kind_of_secret_is_not_used()
    {
        var settings = Settings();
        _secrets.Values["server:srv-example"] = "{\"Username\":\"proxy-user-marker\",\"Password\":\"proxy-password-marker\"}";
        settings.Update(s => s with { ExternalProxy = Authenticated() with { CredentialsKey = "server:srv-example" } });

        new ExternalProxyStore(settings, _secrets).Definition().ShouldBeNull();
        _secrets.Values.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_failed_settings_write_keeps_the_old_profile_and_removes_only_the_new_secret()
    {
        var storage = new FailingSettingsStore(new SqliteSettingsStore(new SqliteDatabase(_paths), NullLogger<SqliteSettingsStore>.Instance));
        var settings = new SettingsService(storage);
        var proxy = new ExternalProxyStore(settings, _secrets);
        proxy.Save(Authenticated(), Username, Password);
        var previous = settings.Current;
        var key = previous.ExternalProxy.CredentialsKey!;
        storage.Fail = true;

        var error = Should.Throw<InvalidOperationException>(() => proxy.Save(Authenticated() with { Host = "second.example.com" }, "next-user", "next-password"));

        error.ToString().ShouldNotContain("next-user");
        error.ToString().ShouldNotContain("next-password");
        settings.Current.ShouldBe(previous);
        Settings().Current.ShouldBe(previous);
        _secrets.Values.Keys.ShouldBe([key]);
        proxy.Definition()!.Credentials.Username.ShouldBe(Username);
    }

    [Fact]
    public void A_secret_store_failure_does_not_publish_or_expose_the_new_credentials()
    {
        var settings = Settings();
        var proxy = new ExternalProxyStore(settings, new RefusingSecrets());

        var error = Should.Throw<InvalidOperationException>(() => proxy.Save(Authenticated(), Username, Password));

        settings.Current.ExternalProxy.ShouldBe(ExternalProxySettings.Default);
        Settings().Current.ExternalProxy.ShouldBe(ExternalProxySettings.Default);
        error.ToString().ShouldNotContain(Username);
        error.ToString().ShouldNotContain(Password);
        proxy.ReadCredentials().ShouldBe(new ServerCredentials(), "прежняя пустая конфигурация не изменилась");
    }

    [Fact]
    public void Save_replaces_the_generation_and_secret()
    {
        var settings = Settings();
        var proxy = new ExternalProxyStore(settings, _secrets);
        proxy.Save(Authenticated(), Username, Password);
        var old = settings.Current.ExternalProxy;

        proxy.Save(Authenticated(), "replacement-user", "replacement-password");

        settings.Current.ExternalProxy.Revision.ShouldBe(old.Revision + 1);
        settings.Current.ExternalProxy.ToProfile()!.Id.ShouldNotBe(old.ToProfile()!.Id);
        _secrets.Values.ShouldHaveSingleItem();
        _secrets.Values.Keys.ShouldNotContain(old.CredentialsKey!);
        proxy.Definition()!.Credentials.Username.ShouldBe("replacement-user");

        proxy.Save(Authenticated() with { Authentication = ProxyAuthentication.None }, string.Empty, string.Empty);
        _secrets.Values.ShouldBeEmpty();
        proxy.Definition()!.Credentials.ShouldBe(new ServerCredentials());
    }

    [Theory]
    [InlineData("user", "")]
    [InlineData("", "password")]
    [InlineData("user\n", "password")]
    public void Incomplete_or_control_containing_credentials_are_not_saved(string username, string password)
    {
        var settings = Settings();
        var proxy = new ExternalProxyStore(settings, _secrets);

        Should.Throw<InvalidOperationException>(() => proxy.Save(Authenticated(), username, password));

        settings.Current.ExternalProxy.ShouldBe(ExternalProxySettings.Default);
        _secrets.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Corrupt_persisted_protocol_authentication_or_port_does_not_connect_with_a_default_instead()
    {
        var settings = Settings();
        new ExternalProxyStore(settings, _secrets).Save(Authenticated(), Username, Password);
        var database = new SqliteDatabase(_paths);
        foreach (var (key, invalid) in new[] { ("external_proxy.protocol", "FutureTls"), ("external_proxy.authentication", "UnknownAuth"), ("external_proxy.port", "65536") })
        {
            var original = Read(database, key);
            Write(database, key, invalid);
            new ExternalProxyStore(Settings(), _secrets).Definition().ShouldBeNull(key);
            Write(database, key, original);
        }
    }

    [Fact]
    public async Task A_worker_save_keeps_preferences_changed_while_the_os_store_is_busy()
    {
        var settings = Settings();
        _ = settings.Current;
        var secrets = new BlockingSecrets();
        var proxy = new ExternalProxyStore(settings, secrets);
        var ct = TestContext.Current.CancellationToken;
        var worker = Task.Run(() => proxy.Save(Authenticated(), Username, Password), ct);
        try
        {
            await secrets.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            settings.Update(s => s with { Language = "en", Notifications = NotificationLevel.Off, ConnectOnLaunch = true });
        }
        finally
        {
            secrets.Release.TrySetResult();
        }

        await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
        settings.Current.Language.ShouldBe("en");
        settings.Current.Notifications.ShouldBe(NotificationLevel.Off);
        settings.Current.ConnectOnLaunch.ShouldBeTrue();
        proxy.Definition()!.Credentials.Username.ShouldBe(Username);
    }

    [Fact]
    public async Task Separate_store_instances_serialize_saves_for_the_same_settings()
    {
        var settings = Settings();
        _ = settings.Current;
        var secrets = new BlockingSecrets();
        var firstStore = new ExternalProxyStore(settings, secrets);
        var secondStore = new ExternalProxyStore(settings, secrets);
        var ct = TestContext.Current.CancellationToken;
        var first = Task.Factory.StartNew(() => firstStore.Save(Authenticated(), Username, Password), ct,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? second = null;
        try
        {
            await secrets.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second = Task.Factory.StartNew(() =>
            {
                secondStarted.TrySetResult();
                secondStore.Save(Authenticated() with { Host = "second.example.com" }, "second-user", "second-password");
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            // Без общего замка вторая запись уже закончится до снятия первой искусственной задержки ОС.
            await Task.WhenAny(secrets.SecondEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(300), ct));
            if (secrets.SecondEntered.Task.IsCompletedSuccessfully)
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally
        {
            secrets.Release.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await second.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(5), ct);
        settings.Current.ExternalProxy.Revision.ShouldBe(2);
        var active = firstStore.Definition().ShouldNotBeNull();
        active.Host.ShouldBe("second.example.com");
        active.Credentials.Username.ShouldBe("second-user");
        secrets.Count.ShouldBe(1, "предыдущая OS-запись удалена после второй фиксации");
    }

    private SettingsService Settings() => new(new SqliteSettingsStore(new SqliteDatabase(_paths), NullLogger<SqliteSettingsStore>.Instance));

    private static ExternalProxySettings Authenticated() => new(ExternalProxyProtocol.Https, "proxy.example.com", 8443, ProxyAuthentication.UserPassword);

    private static string Read(SqliteDatabase database, string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (string)command.ExecuteScalar()!;
    }

    private static void Write(SqliteDatabase database, string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET value = $value WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private sealed class FailingSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public bool Fail { get; set; }

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings next)
        {
            if (Fail)
            {
                throw new IOException("Тестовый отказ записи");
            }

            inner.Save(next);
        }
    }

    private sealed class RefusingSecrets : ISecretStore
    {
        public string Name => "Тестовое хранилище";

        public bool IsAvailable => true;

        public void Save(string key, string secret) => throw new InvalidOperationException(Username + ":" + Password);

        public string? Read(string key) => throw new InvalidOperationException(Username + ":" + Password);

        public void Delete(string key) => throw new InvalidOperationException(Username + ":" + Password);
    }

    private sealed class BlockingSecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private int _calls;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => _values.Count;

        public string Name => "Тестовое хранилище с задержкой";

        public bool IsAvailable => true;

        public void Save(string key, string secret)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.TrySetResult();
                Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            else
            {
                SecondEntered.TrySetResult();
            }

            _values[key] = secret;
        }

        public string? Read(string key) => _values.GetValueOrDefault(key);

        public void Delete(string key) => _values.TryRemove(key, out _);
    }
}
