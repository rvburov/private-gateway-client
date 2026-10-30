using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Файл зашифрованных секретов Windows — <c>secrets.db</c> рядом с базой, а не таблица в ней. Каталог серверов пишет
/// секрет посреди своей транзакции в app.db (сначала секрет, потом строка); в той же базе запись другим подключением
/// ждала бы конца этой транзакции, а та — записи: «database is locked» через 30 с и падение окна при «Добавить»
/// (найдено у пользователя 2026-10-29). Отдельный файл не ждет ничьих транзакций, как Связка ключей и Secret Service.
/// Секреты из прежней таблицы app.db переносятся при каждом запуске, пока она не пуста, и оттуда удаляются: прерванный
/// перенос доделывается в следующий раз, а копия удаленного сервера не остается в app.db.
/// </summary>
public sealed class SecretFile
{
    private readonly string _path;

    public SecretFile(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _path = database.Paths.Secrets;
        database.Paths.EnsureCreated();
        using var connection = Open();
        Run(connection, "CREATE TABLE IF NOT EXISTS secrets (key TEXT PRIMARY KEY NOT NULL, value BLOB NOT NULL)");
        MoveFrom(database, connection);
    }

    public void Write(string key, byte[] value)
    {
        using var connection = Open();
        Run(connection, "INSERT INTO secrets (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
    }

    public byte[]? Read(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM secrets WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as byte[];
    }

    public void Delete(string key)
    {
        using var connection = Open();
        Run(connection, "DELETE FROM secrets WHERE key = $key", ("$key", key));
    }

    /// <summary>
    /// Секреты, записанные прежними версиями в таблицу secrets самой базы (миграция 0002), — сюда, затем удалить там.
    /// Уже перенесенный ключ не перезаписывается: в файле он новее. Сбой до удаления — повтор при следующем запуске.
    /// </summary>
    private static void MoveFrom(SqliteDatabase database, SqliteConnection target)
    {
        using var source = database.Open();
        var rows = new List<(string Key, byte[] Value)>();
        using (var read = source.CreateCommand())
        {
            read.CommandText = "SELECT key, value FROM secrets";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), (byte[])reader.GetValue(1)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using (var transaction = target.BeginTransaction())
        {
            foreach (var (key, value) in rows)
            {
                using var write = target.CreateCommand();
                write.Transaction = transaction;
                write.CommandText = "INSERT OR IGNORE INTO secrets (key, value) VALUES ($key, $value)";
                write.Parameters.AddWithValue("$key", key);
                write.Parameters.AddWithValue("$value", value);
                write.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        using var delete = source.CreateCommand();
        delete.CommandText = "DELETE FROM secrets";
        delete.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
