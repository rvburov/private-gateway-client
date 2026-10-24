using System.Text;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Infrastructure.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Хранилище секретов текущей ОС — настоящее: DPAPI на Windows, Связка ключей на macOS, Secret
/// Service на Linux. Где хранилища нет (Linux без GNOME Keyring/KWallet), тесты пропускаются с
/// причиной — их проверяет CI на Windows и macOS.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N")));
    private readonly string _key = "test:" + Guid.NewGuid().ToString("N");
    private readonly ISecretStore _store;

    public SecretStoreTests() => _store = SecretStores.ForCurrentOs(new SqliteDatabase(_paths));

    public void Dispose()
    {
        if (_store.IsAvailable)
        {
            _store.Delete(_key);
            _store.Delete(_key + ":other");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Secret_round_trips_overwrites_and_deletes()
    {
        SkipWithoutStore();

        _store.Read(_key).ShouldBeNull();
        _store.Save(_key, "pw-первый");
        _store.Read(_key).ShouldBe("pw-первый");

        _store.Save(_key, "pw-второй");
        _store.Read(_key).ShouldBe("pw-второй", "повторное сохранение заменяет секрет");

        _store.Delete(_key);
        _store.Read(_key).ShouldBeNull();
        _store.Delete(_key);
    }

    [Fact]
    public void Keys_do_not_see_each_other()
    {
        SkipWithoutStore();

        _store.Save(_key, "pw-a");
        _store.Save(_key + ":other", "pw-b");

        _store.Read(_key).ShouldBe("pw-a");
        _store.Read(_key + ":other").ShouldBe("pw-b");
    }

    [Fact]
    public void Database_never_holds_the_secret_as_text()
    {
        SkipWithoutStore();

        _store.Save(_key, "pw-plain-text-marker");

        var database = _paths.Database;
        if (File.Exists(database))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Encoding.UTF8.GetString(File.ReadAllBytes(database)).ShouldNotContain("pw-plain-text-marker");
        }
    }

    private void SkipWithoutStore()
    {
        if (!_store.IsAvailable)
        {
            Assert.Skip($"{_store.Name} недоступно на этой машине — проверяется в CI на Windows и macOS");
        }
    }
}
