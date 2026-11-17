using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>Удаление секретов снятой функции после фиксации миграции; отказ ОС повторяется при следующем запуске.</summary>
internal static class RetiredSecrets
{
    public static void Cleanup(SqliteDatabase database, ISecretStore secrets)
    {
        using var connection = database.Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT key FROM retired_secret_keys ORDER BY key";
        var keys = new List<string>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }
        }

        foreach (var key in keys)
        {
            try
            {
                secrets.Delete(key);
                using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM retired_secret_keys WHERE key = $key";
                remove.Parameters.AddWithValue("$key", key);
                remove.ExecuteNonQuery();
            }
            catch (Exception error) when (error is SecretStoreException or InvalidOperationException or IOException
                or UnauthorizedAccessException or SqliteException)
            {
                // Каталог уже не содержит цепочек. Не читать секреты и не повторять системный запрос для каждого ключа.
                break;
            }
        }
    }
}
