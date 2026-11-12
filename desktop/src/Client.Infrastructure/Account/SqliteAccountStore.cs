using System.Globalization;
using System.Reflection;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure.Account;

/// <summary>
/// uid устройства и сведения входа без токена — в таблице account базы программы (миграция 0008), мимо настроек
/// (<see cref="SqliteSettingsStore"/>): резервная копия берет только настройки и серверы, так что ни uid, ни вход на
/// другой компьютер не переносятся. uid — случайный GUID, создается при первом чтении и остается после выхода.
/// </summary>
public sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore, IDeviceIdentity
{
    private const string UidKey = "device.uid";
    private const string ExpiresKey = "account.expires_at";
    private const string EmailKey = "account.email";
    private const string DisplayNameKey = "account.display_name";
    private const string DeviceNameKey = "account.device_name";
    private const string CheckedKey = "account.checked_at";

    // Версия у всех сборок одна — та же, что в «О программе».
    private static readonly string Version =
        typeof(SqliteAccountStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private readonly Lock _gate = new();
    private string? _uid;

    public string Uid
    {
        get
        {
            lock (_gate)
            {
                return _uid ??= ReadOrCreateUid();
            }
        }
    }

    public string Name => Environment.MachineName;

    public string AppVersion => Version;

    public SavedAccount? Load()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM account WHERE key LIKE 'account.%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return Time(values, ExpiresKey) is { } expires && values.TryGetValue(EmailKey, out var email)
            ? new SavedAccount(expires, email, values.GetValueOrDefault(DisplayNameKey), values.GetValueOrDefault(DeviceNameKey) ?? "",
                Time(values, CheckedKey) ?? expires)
            : null;
    }

    public void Save(SavedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM account WHERE key LIKE 'account.%';";
            clear.ExecuteNonQuery();
        }

        foreach (var (key, value) in new (string, string?)[]
                 {
                     (ExpiresKey, account.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
                     (EmailKey, account.Email),
                     (DisplayNameKey, account.DisplayName),
                     (DeviceNameKey, account.DeviceName),
                     (CheckedKey, account.CheckedAt.ToString("O", CultureInfo.InvariantCulture)),
                 })
        {
            if (value is null)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO account (key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Clear()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM account WHERE key LIKE 'account.%';";
        command.ExecuteNonQuery();
    }

    private string ReadOrCreateUid()
    {
        using var connection = database.Open();
        using (var insert = connection.CreateCommand())
        {
            // Второе окно могло создать uid раньше: остается первый.
            insert.CommandText = "INSERT OR IGNORE INTO account (key, value) VALUES ($key, $value);";
            insert.Parameters.AddWithValue("$key", UidKey);
            insert.Parameters.AddWithValue("$value", Guid.NewGuid().ToString());
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM account WHERE key = $key;";
        select.Parameters.AddWithValue("$key", UidKey);
        return (string)select.ExecuteScalar()!;
    }

    private static DateTimeOffset? Time(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;
}
