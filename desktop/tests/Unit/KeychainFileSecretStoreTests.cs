using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Infrastructure.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Настоящее шифрование и SQLite; системное подтверждение Связки ключей заменено управляемым ответом.</summary>
public sealed class KeychainFileSecretStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-vault-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSecrets _legacy = new();
    private readonly string _masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private int _unlocks;
    private bool _denied;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Many_servers_use_one_keychain_unlock_and_a_restart_asks_for_the_key_once()
    {
        using (var store = Store())
        {
            for (var i = 0; i < 25; i++)
            {
                store.Save("server:" + i, "secret-marker-" + i);
                store.Read("server:" + i).ShouldBe("secret-marker-" + i);
            }

            store.Save("server:0", "changed-marker");
            store.Read("server:0").ShouldBe("changed-marker");
            _unlocks.ShouldBe(1, "один ключ на весь процесс, а не на каждую запись");
        }

        using var restarted = Store();
        restarted.Read("server:0").ShouldBe("changed-marker");
        restarted.Read("server:24").ShouldBe("secret-marker-24");
        _unlocks.ShouldBe(2, "новый процесс открывает ключ заново");
        Encoding.UTF8.GetString(File.ReadAllBytes(_paths.Secrets)).ShouldNotContain("secret-marker");
        Encoding.UTF8.GetString(File.ReadAllBytes(_paths.Secrets)).ShouldNotContain("changed-marker");
        Encoding.UTF8.GetString(File.ReadAllBytes(_paths.Secrets)).ShouldNotContain(_masterKey);
    }

    [Fact]
    public void Legacy_denial_preserves_the_original_and_a_later_attempt_migrates_it()
    {
        _legacy.Values["server:old"] = "old-secret-marker";
        _legacy.ReadFailure = new InvalidOperationException("denied");
        using var store = Store();

        Should.Throw<InvalidOperationException>(() => store.Read("server:old"));
        _legacy.Values["server:old"].ShouldBe("old-secret-marker");
        _unlocks.ShouldBe(0, "отказ старой записи не открывает новое хранилище и не запускает другие запросы");

        _legacy.ReadFailure = null;
        store.Read("server:old").ShouldBe("old-secret-marker");
        _legacy.Values.ShouldNotContainKey("server:old");
        _legacy.ReadFailure = new InvalidOperationException("старое хранилище больше не нужно");
        using var restarted = Store();
        restarted.Read("server:old").ShouldBe("old-secret-marker");
    }

    [Fact]
    public void Master_key_denial_is_not_cached_and_does_not_erase_a_legacy_secret()
    {
        _legacy.Values["server:old"] = "old-secret-marker";
        _denied = true;
        using var store = Store();

        Should.Throw<InvalidOperationException>(() => store.Read("server:old"));
        _legacy.Values["server:old"].ShouldBe("old-secret-marker");
        _denied = false;
        store.Read("server:old").ShouldBe("old-secret-marker");
        _unlocks.ShouldBe(2, "после отказа явная новая попытка снова обращается к Связке ключей");
    }

    [Fact]
    public void Failed_file_write_leaves_the_legacy_secret_for_restart()
    {
        _legacy.Values["server:old"] = "old-secret-marker";
        using (var store = Store())
        {
            using var connection = new SqliteConnection("Data Source=" + _paths.Secrets);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_write BEFORE INSERT ON secrets BEGIN SELECT RAISE(ABORT, 'write refused'); END";
            command.ExecuteNonQuery();

            Should.Throw<SqliteException>(() => store.Read("server:old"));
            _legacy.Values["server:old"].ShouldBe("old-secret-marker");
            command.CommandText = "DROP TRIGGER reject_write";
            command.ExecuteNonQuery();
        }

        using var restarted = Store();
        restarted.Read("server:old").ShouldBe("old-secret-marker");
        _legacy.Values.ShouldNotContainKey("server:old");
    }

    [Fact]
    public void Corruption_or_swapping_records_is_an_error_without_falling_back_to_an_old_copy()
    {
        var file = new SecretFile(new SqliteDatabase(_paths));
        using var store = new KeychainFileSecretStore(file, _legacy, Unlock);
        store.Save("server:a", "secret-a");
        store.Save("server:b", "secret-b");
        _legacy.Values["server:b"] = "outdated-secret";
        file.Write("server:b", file.Read("server:a")!);

        Should.Throw<InvalidOperationException>(() => store.Read("server:b"));
        var tampered = file.Read("server:a")!;
        tampered[^1] ^= 1;
        file.Write("server:a", tampered);
        Should.Throw<InvalidOperationException>(() => store.Read("server:a"));
        _legacy.Values["server:b"].ShouldBe("outdated-secret");
    }

    [Fact]
    public void Restart_completes_cleanup_after_the_encrypted_copy_was_committed()
    {
        _legacy.Values["server:old"] = "old-secret";
        _legacy.DeleteFailure = new InvalidOperationException("delete denied");
        using (var store = Store())
        {
            Should.Throw<InvalidOperationException>(() => store.Read("server:old"));
            _legacy.Values["server:old"].ShouldBe("old-secret");
        }

        _legacy.DeleteFailure = null;
        _legacy.ReadFailure = new InvalidOperationException("повторно читать старую запись не нужно");
        using var restarted = Store();
        restarted.Read("server:old").ShouldBe("old-secret");
        _legacy.Values.ShouldNotContainKey("server:old");
        new SecretFile(new SqliteDatabase(_paths)).NeedsLegacyCleanup("server:old").ShouldBeFalse();
    }

    [Fact]
    public void Saving_a_replacement_before_the_first_read_does_not_leave_a_stale_legacy_secret()
    {
        const string name = "server:old";
        _legacy.Values[name] = "old-secret";
        _legacy.ReadFailure = new InvalidOperationException("для нового значения старый секрет читать не нужно");
        _legacy.DeleteFailure = new InvalidOperationException("delete denied");
        using (var store = Store())
        {
            Should.Throw<InvalidOperationException>(() => store.Save(name, "new-secret"));
            _legacy.Values[name].ShouldBe("old-secret", "отказ удаления не теряет исходную запись");
        }

        _legacy.DeleteFailure = null;
        using var restarted = Store();
        restarted.Read(name).ShouldBe("new-secret", "после перезапуска приоритет у сохраненного нового значения");
        _legacy.Values.ShouldNotContainKey(name);
        new SecretFile(new SqliteDatabase(_paths)).NeedsLegacyCleanup(name).ShouldBeFalse();
        _legacy.Reads.ShouldBe(0);
    }

    [Fact]
    public void Missing_key_for_existing_ciphertext_is_not_replaced_and_deletion_cannot_resurrect_legacy_data()
    {
        var file = new SecretFile(new SqliteDatabase(_paths));
        using (var first = new KeychainFileSecretStore(file, _legacy, Unlock))
        {
            first.Save("server:a", "secret-a");
        }

        var requestedCreate = true;
        using var missing = new KeychainFileSecretStore(file, _legacy, create =>
        {
            requestedCreate = create;
            return null;
        });
        Should.Throw<InvalidOperationException>(() => missing.Read("server:a"));
        requestedCreate.ShouldBeFalse("расшифровка не создает новый ключ вместо потерянного");
        requestedCreate = true;
        Should.Throw<InvalidOperationException>(() => missing.Save("server:another", "another-secret"));
        requestedCreate.ShouldBeFalse("добавление не заменяет потерянный ключ непустого хранилища");
        file.Read("server:another").ShouldBeNull();
        file.Read("server:a").ShouldNotBeNull();

        _legacy.Values["server:a"] = "older-secret";
        missing.Delete("server:a");
        missing.Read("server:a").ShouldBeNull();
        _legacy.Values.ShouldNotContainKey("server:a");
    }

    private KeychainFileSecretStore Store() => new(new SecretFile(new SqliteDatabase(_paths)), _legacy, Unlock);

    private string Unlock(bool create)
    {
        _unlocks++;
        return _denied ? throw new InvalidOperationException("denied") : _masterKey;
    }
}
