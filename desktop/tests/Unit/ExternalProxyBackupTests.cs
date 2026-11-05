using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ExternalProxyBackupTests : IDisposable
{
    private const string Password = "example backup password";
    private readonly List<AppPaths> _paths = [];

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var paths in _paths)
        {
            if (Directory.Exists(paths.DataDirectory))
            {
                Directory.Delete(paths.DataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void A_full_encrypted_backup_restores_credentials_under_a_fresh_local_key()
    {
        var home = Home();
        home.Proxy.Save(Profile("proxy.example.com"), "external-backup-user", "external-backup-password");
        var originalKey = home.Settings.Current.ExternalProxy.CredentialsKey;

        var encrypted = home.Backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password);
        encrypted.ShouldNotContain("external-backup-user");
        encrypted.ShouldNotContain("external-backup-password");
        var exported = OpenPortablePayload(encrypted);
        exported.ShouldContain("external-backup-user");
        exported.ShouldContain("external-backup-password");
        exported.ShouldNotContain(originalKey!);
        var away = Home();
        away.Backup.Restore(BackupService.Read(encrypted, Password));

        var restored = away.Proxy.Definition().ShouldNotBeNull();
        restored.Host.ShouldBe("proxy.example.com");
        restored.Credentials.Username.ShouldBe("external-backup-user");
        restored.Credentials.Password.ShouldBe("external-backup-password");
        away.Settings.Current.ExternalProxy.Authentication.ShouldBe(ProxyAuthentication.UserPassword);
        away.Settings.Current.ExternalProxy.CredentialsKey.ShouldNotBe(originalKey);
        away.Secrets.Values.Keys.ShouldNotContain(originalKey!);
    }

    [Fact]
    public void Settings_only_restore_keeps_required_authentication_but_never_reuses_another_local_secret()
    {
        var home = Home();
        home.Proxy.Save(Profile("proxy.example.com"), "home-user", "home-password");
        var encrypted = home.Backup.Create(new BackupParts(Servers: false, Settings: true, Keys: false), Password);
        BackupService.Read(encrypted, Password).HasKeys.ShouldBeFalse();
        var exported = OpenPortablePayload(encrypted);
        exported.ShouldNotContain("home-user");
        exported.ShouldNotContain("home-password");
        exported.ShouldNotContain(home.Settings.Current.ExternalProxy.CredentialsKey!);
        var away = Home();
        away.Proxy.Save(Profile("other.example.com"), "away-user", "away-password");
        var oldKey = away.Settings.Current.ExternalProxy.CredentialsKey;

        away.Backup.Restore(BackupService.Read(encrypted, Password));

        away.Settings.Current.ExternalProxy.Host.ShouldBe("proxy.example.com");
        away.Settings.Current.ExternalProxy.Authentication.ShouldBe(ProxyAuthentication.UserPassword);
        away.Settings.Current.ExternalProxy.CredentialsKey.ShouldBeNull();
        away.Proxy.ReadCredentials().ShouldBeNull();
        away.Proxy.Definition().ShouldBeNull("нужен повторный ввод, а не анонимное подключение");
        away.Secrets.Values.Keys.ShouldNotContain(oldKey!);
    }

    [Fact]
    public void The_auto_copy_restores_credentials_even_after_the_original_key_was_removed()
    {
        var home = Home();
        home.Proxy.Save(Profile("home.example.com"), "home-user", "home-password");
        var encrypted = home.Backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password);
        var away = Home();
        away.Proxy.Save(Profile("away.example.com"), "away-user", "away-password");
        var old = away.Settings.Current.ExternalProxy;
        away.Backup.Restore(BackupService.Read(encrypted, Password));
        away.Secrets.Values.Keys.ShouldNotContain(old.CredentialsKey!);

        away.Backup.RestoreAutoCopy();

        var restored = away.Proxy.Definition().ShouldNotBeNull();
        restored.Host.ShouldBe("away.example.com");
        restored.Credentials.Username.ShouldBe("away-user");
        restored.Credentials.Password.ShouldBe("away-password");
        away.Settings.Current.ExternalProxy.CredentialsKey.ShouldNotBe(old.CredentialsKey);
        away.Settings.Current.ExternalProxy.Revision.ShouldBeGreaterThan(old.Revision);
    }

    [Fact]
    public async Task A_backup_captures_one_endpoint_and_its_credentials_while_another_save_waits()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "pg-external-snapshot-" + Guid.NewGuid().ToString("N")));
        _paths.Add(paths);
        var database = new SqliteDatabase(paths);
        var settings = new SettingsService(new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance));
        var secrets = new BlockingReadSecrets();
        var proxy = new ExternalProxyStore(settings, secrets);
        proxy.Save(Profile("first.example.com"), "first-user", "first-password");
        var time = new FakeTimeProvider();
        var backup = new BackupService(settings, new SqliteServerCatalog(database, secrets, time), secrets, paths, time);
        var ct = TestContext.Current.CancellationToken;
        var capturing = Task.Factory.StartNew(() => backup.Create(new BackupParts(Servers: true, Settings: true, Keys: true), Password),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? saving = null;
        try
        {
            await secrets.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            saving = Task.Factory.StartNew(() =>
            {
                started.TrySetResult();
                proxy.Save(Profile("second.example.com"), "second-user", "second-password");
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.WhenAny(secrets.SecondWriteEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(300), ct));
            if (secrets.SecondWriteEntered.Task.IsCompletedSuccessfully)
            {
                await saving.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally
        {
            secrets.ReleaseRead.TrySetResult();
        }

        var encrypted = await capturing.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await saving.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(5), ct);
        var payload = JsonNode.Parse(OpenPortablePayload(encrypted)).ShouldNotBeNull();
        payload["Settings"]!["external_proxy.host"]!.GetValue<string>().ShouldBe("first.example.com");
        payload["ExternalProxyCredentials"].ShouldNotBeNull()["Username"]!.GetValue<string>().ShouldBe("first-user");
        payload["ExternalProxyCredentials"]!["Password"]!.GetValue<string>().ShouldBe("first-password");
        proxy.Definition()!.Host.ShouldBe("second.example.com");
        proxy.Definition()!.Credentials.Username.ShouldBe("second-user");
    }

    private HomeState Home()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "pg-external-backup-" + Guid.NewGuid().ToString("N")));
        _paths.Add(paths);
        var secrets = new FakeSecrets();
        var database = new SqliteDatabase(paths);
        var settings = new SettingsService(new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance));
        var time = new FakeTimeProvider();
        var catalog = new SqliteServerCatalog(database, secrets, time);
        return new HomeState(settings, secrets, new ExternalProxyStore(settings, secrets), new BackupService(settings, catalog, secrets, paths, time));
    }

    private static ExternalProxySettings Profile(string host) => new(ExternalProxyProtocol.Https, host, 8443, ProxyAuthentication.UserPassword);

    /// <summary>Проверяет экспортные байты независимо от восстановления: отключенная галочка ключей не оставляет их даже внутри шифра.</summary>
    private static string OpenPortablePayload(string encrypted)
    {
        var envelope = JsonNode.Parse(encrypted).ShouldNotBeNull();
        var salt = Convert.FromBase64String(envelope["salt"]!.GetValue<string>());
        var nonce = Convert.FromBase64String(envelope["nonce"]!.GetValue<string>());
        var data = Convert.FromBase64String(envelope["data"]!.GetValue<string>());
        var iterations = envelope["iterations"]!.GetValue<int>();
        var aad = Encoding.UTF8.GetBytes(string.Join('|', envelope["format"]!.GetValue<string>(),
            envelope["version"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), envelope["kdf"]!.GetValue<string>(),
            iterations.ToString(CultureInfo.InvariantCulture), envelope["salt"]!.GetValue<string>(),
            envelope["nonce"]!.GetValue<string>(), envelope["created"]!.GetValue<string>()));
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var clear = new byte[data.Length - 16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, data.AsSpan(0, clear.Length), data.AsSpan(clear.Length), clear, aad);
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed record HomeState(SettingsService Settings, FakeSecrets Secrets, ExternalProxyStore Proxy, BackupService Backup);

    private sealed class BlockingReadSecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private int _writes;
        private int _reads;

        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Тестовое хранилище снимка";

        public bool IsAvailable => true;

        public void Save(string key, string secret)
        {
            if (Interlocked.Increment(ref _writes) > 1)
            {
                SecondWriteEntered.TrySetResult();
            }

            _values[key] = secret;
        }

        public string? Read(string key)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                ReadEntered.TrySetResult();
                ReleaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }

            return _values.GetValueOrDefault(key);
        }

        public void Delete(string key) => _values.TryRemove(key, out _);
    }
}
