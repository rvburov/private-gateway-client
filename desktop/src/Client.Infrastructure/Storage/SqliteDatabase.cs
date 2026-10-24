using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// База SQLite в папке данных. Миграции — SQL-файлы Storage/Migrations/NNNN_*.sql по порядку;
/// примененная версия хранится в PRAGMA user_version. Миграции только добавляют и переносят
/// данные, без удаления.
/// </summary>
public sealed class SqliteDatabase(AppPaths paths)
{
    private readonly Lock _gate = new();
    private bool _migrated;

    public SqliteConnection Open()
    {
        EnsureMigrated();
        return OpenRaw();
    }

    /// <summary>Номер последней примененной миграции.</summary>
    public long Version()
    {
        using var connection = Open();
        return UserVersion(connection);
    }

    private void EnsureMigrated()
    {
        lock (_gate)
        {
            if (_migrated)
            {
                return;
            }

            paths.EnsureCreated();
            using var connection = OpenRaw();
            var current = UserVersion(connection);
            foreach (var (number, sql) in Migrations().Where(m => m.Number > current))
            {
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql + $"\nPRAGMA user_version = {number};";
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            _migrated = true;
        }
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static IEnumerable<(int Number, string Sql)> Migrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("migrations.", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var number = int.Parse(name.AsSpan("migrations.".Length, 4), provider: System.Globalization.CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (number, reader.ReadToEnd());
        }
    }
}
