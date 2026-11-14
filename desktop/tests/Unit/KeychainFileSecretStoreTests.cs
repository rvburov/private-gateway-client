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
        using var store = new KeychainFileSecretStore(file, _legacy, Unlock, Cleanup);
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
            store.Read("server:old").ShouldBe("old-secret", "отказ уборки не мешает использовать надежно перенесенный секрет");
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
            store.Save(name, "new-secret");
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
        using (var first = new KeychainFileSecretStore(file, _legacy, Unlock, Cleanup))
        {
            first.Save("server:a", "secret-a");
        }

        var requestedCreate = true;
        using var missing = new KeychainFileSecretStore(file, _legacy, create =>
        {
            requestedCreate = create;
            return null;
        }, Cleanup);
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

    [Fact]
    public void Thirty_migrated_servers_keep_one_unlock_after_each_upgrade_even_when_legacy_cleanup_is_denied()
    {
        for (var i = 0; i < 30; i++)
        {
            _legacy.Values["server:" + i] = "legacy-marker-" + i;
        }
        _legacy.DeleteFailure = new InvalidOperationException("новая подпись не получила право удаления старых записей");

        using (var oldBuild = Store())
        {
            for (var i = 0; i < 30; i++)
            {
                oldBuild.Read("server:" + i).ShouldBe("legacy-marker-" + i);
            }
        }
        _legacy.Reads.ShouldBe(30, "первый перенос требует разрешенного чтения прежних записей");
        _legacy.ReadFailure = new InvalidOperationException("после обновления нельзя снова открывать каждый старый секрет");
        var unlocksBeforeUpgrade = _unlocks;

        for (var upgrade = 0; upgrade < 3; upgrade++)
        {
            using var newBuild = Store();
            for (var refresh = 0; refresh < 2; refresh++)
            {
                for (var i = 0; i < 30; i++)
                {
                    newBuild.Read("server:" + i).ShouldBe("legacy-marker-" + i);
                }
            }
            _unlocks.ShouldBe(unlocksBeforeUpgrade + upgrade + 1, "один общий ключ на каждый новый процесс");
            _legacy.Reads.ShouldBe(30, "ни запуск, ни повторный замер не возвращаются к старым секретам");
        }
        _legacy.Values.Count.ShouldBe(30, "отказ очистки не обходит права и сохраняет защищенные исходники");
    }

    [Fact]
    public void Deleted_legacy_secret_cannot_return_after_denied_cleanup_restart_or_a_later_successful_cleanup()
    {
        const string key = "account:session";
        _legacy.Values[key] = "old-session-marker";
        _legacy.DeleteFailure = new InvalidOperationException("delete denied");
        using (var store = Store())
        {
            store.Delete(key);
            store.Read(key).ShouldBeNull();
        }
        _legacy.Values[key].ShouldBe("old-session-marker", "отказ не меняет права старой записи");
        _unlocks.ShouldBe(0, "выход не открывает общий ключ только ради удаления");

        using (var restarted = Store())
        {
            restarted.Read(key).ShouldBeNull();
            _legacy.DeleteFailure = null;
            restarted.Read(key).ShouldBeNull();
            _legacy.Values.ShouldNotContainKey(key);
        }
        _legacy.Reads.ShouldBe(0, "удаленный вход никогда не читается заново из старой Связки ключей");

        // Даже старая программа, снова записавшая старый элемент Keychain, не отменяет явный выход в новой программе.
        _legacy.Values[key] = "stale-session-marker";
        using var upgraded = Store();
        upgraded.Read(key).ShouldBeNull();
        upgraded.Save(key, "new-session-marker");
        upgraded.Read(key).ShouldBe("new-session-marker", "только явное сохранение нового входа снимает отметку удаления");
    }

    [Fact]
    public void Failed_tombstone_transaction_does_not_partially_delete_the_encrypted_copy()
    {
        using var store = Store();
        store.Save("server:keep", "kept-secret-marker");
        using var connection = new SqliteConnection("Data Source=" + _paths.Secrets);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_delete BEFORE DELETE ON secrets BEGIN SELECT RAISE(ABORT, 'delete refused'); END";
        command.ExecuteNonQuery();

        Should.Throw<SqliteException>(() => store.Delete("server:keep"));
        store.Read("server:keep").ShouldBe("kept-secret-marker");
        new SecretFile(new SqliteDatabase(_paths)).IsLegacyDeleted("server:keep").ShouldBeFalse();
    }

    [Fact]
    public void Diagnostics_report_only_counts_and_do_not_unlock_or_read_legacy_secrets()
    {
        _legacy.Values["server:old"] = "private-value-marker";
        _legacy.DeleteFailure = new InvalidOperationException("cleanup denied");
        using (var store = Store())
        {
            store.Save("server:old", "private-value-marker");
            store.Delete("account:session");
        }
        var unlocks = _unlocks;
        using var next = Store();
        var info = next.Diagnostics;
        info.Kind.ShouldBe("keychain-file-v1");
        info.EncryptedEntries.ShouldBe(1);
        info.PendingLegacyCleanup.ShouldBe(2);
        info.DeletedEntries.ShouldBe(1);
        info.KeyUnlocked.ShouldBeFalse();
        _unlocks.ShouldBe(unlocks);
        _legacy.Reads.ShouldBe(0);
        info.ToString().ShouldNotContain("private-value-marker");
        info.ToString().ShouldNotContain("server:old");
        info.ToString().ShouldNotContain("account:session");
        next.Read("server:old").ShouldBe("private-value-marker");
        next.Diagnostics.KeyUnlocked.ShouldBeTrue();
    }

    private KeychainFileSecretStore Store() => new(new SecretFile(new SqliteDatabase(_paths)), _legacy, Unlock, Cleanup);

    private bool Cleanup(string key)
    {
        if (_legacy.DeleteFailure is not null)
        {
            return false;
        }
        _legacy.Delete(key);
        return true;
    }

    private string Unlock(bool create)
    {
        _unlocks++;
        return _denied ? throw new InvalidOperationException("denied") : _masterKey;
    }
}
