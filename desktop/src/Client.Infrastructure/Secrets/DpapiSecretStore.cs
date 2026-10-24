using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Windows: секрет шифруется DPAPI текущего пользователя и лежит в таблице secrets. Расшифровать
/// его может только этот пользователь на этом компьютере. Дополнительная энтропия — ключ записи:
/// зашифрованное значение нельзя переставить под другой ключ.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(SqliteDatabase database) : ISecretStore
{
    public string Name => "Windows DPAPI";

    public bool IsAvailable => true;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy(key), DataProtectionScope.CurrentUser);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO secrets (key, value) VALUES ($key, $value) "
            + "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", blob);
        command.ExecuteNonQuery();
    }

    public string? Read(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM secrets WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        if (command.ExecuteScalar() is not byte[] blob)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy(key), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException e)
        {
            // Другой пользователь или переустановленная Windows: прочитать нельзя, и это ошибка, а не пустой секрет.
            throw new InvalidOperationException("DPAPI не расшифровал секрет", e);
        }
    }

    public void Delete(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM secrets WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static byte[] Entropy(string key) => Encoding.UTF8.GetBytes("PrivateGateway:" + key);
}
