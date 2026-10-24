using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Свои серверы пользователя в SQLite (таблицы servers и subscriptions). Описание без секретов
/// лежит в базе JSON-ом, секретная часть — в хранилище ОС под ключом server:&lt;id&gt;. Выбранный
/// сервер — ключ selected_server в таблице settings. Список держится в памяти и меняется вместе
/// с базой.
/// </summary>
public sealed class SqliteServerCatalog : IServerCatalog, IServerStore
{
    private const string SelectedKey = "selected_server";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteDatabase _database;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private List<Row>? _rows;
    private List<ServerSubscription>? _subscriptions;
    private string? _selectedId;

    public SqliteServerCatalog(SqliteDatabase database, ISecretStore secrets, TimeProvider time)
    {
        _database = database;
        _secrets = secrets;
        _time = time;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ServerProfile> Servers
    {
        get
        {
            lock (_gate)
            {
                return [.. Rows.Select(r => r.Profile)];
            }
        }
    }

    public IReadOnlyList<ServerSubscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return [.. _subscriptions!];
            }
        }
    }

    public ServerProfile? Selected
    {
        get
        {
            lock (_gate)
            {
                return Rows.Find(r => r.Profile.Id == _selectedId)?.Profile;
            }
        }
    }

    public bool CanStoreSecrets => _secrets.IsAvailable;

    public string SecretStoreName => _secrets.Name;

    // ── Выбор и правки ───────────────────────────────────────────────────────

    public void Choose(string serverId)
    {
        lock (_gate)
        {
            var row = Rows.Find(r => r.Profile.Id == serverId);
            if (row is null || _selectedId == serverId)
            {
                return;
            }

            var now = _time.GetUtcNow();
            Execute("UPDATE servers SET last_used_at = $at WHERE id = $id", ("$at", Text(now)), ("$id", serverId));
            SaveSelected(serverId);
            Replace(row with { Profile = row.Profile with { LastUsedAt = now } });
            _selectedId = serverId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetFavorite(string serverId, bool isFavorite)
    {
        lock (_gate)
        {
            var row = Rows.Find(r => r.Profile.Id == serverId);
            if (row is null || row.Profile.IsFavorite == isFavorite)
            {
                return;
            }

            Execute("UPDATE servers SET is_favorite = $value WHERE id = $id", ("$value", isFavorite ? 1 : 0), ("$id", serverId));
            Replace(row with { Profile = row.Profile with { IsFavorite = isFavorite } });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string serverId, string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        lock (_gate)
        {
            var row = Rows.Find(r => r.Profile.Id == serverId);
            if (row is null || trimmed.Length == 0 || row.Profile.Name == trimmed)
            {
                return;
            }

            Execute("UPDATE servers SET custom_name = $name WHERE id = $id", ("$name", trimmed), ("$id", serverId));
            Replace(row with { Profile = row.Profile with { Name = trimmed } });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string serverId)
    {
        lock (_gate)
        {
            var row = Rows.Find(r => r.Profile.Id == serverId);
            if (row is null)
            {
                return;
            }

            Execute("DELETE FROM servers WHERE id = $id", ("$id", serverId));
            _secrets.Delete(SecretKey(serverId));
            Rows.Remove(row);
            if (_selectedId == serverId)
            {
                _selectedId = null;
                SaveSelected(null);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Пинг меряет служба, а списки подписок по адресам загружаются отдельно — здесь только уведомление.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // ── Импорт ───────────────────────────────────────────────────────────────

    public int CountDuplicates(IReadOnlyList<ServerDefinition> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        lock (_gate)
        {
            var known = Known(servers);
            return servers.Count(s => !known.Add(Key(s)));
        }
    }

    public ImportOutcome Add(IReadOnlyList<ServerDefinition> servers, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(servers);
        if (!_secrets.IsAvailable)
        {
            throw new InvalidOperationException($"{_secrets.Name} недоступно: серверы с секретами сохранить нельзя");
        }

        var added = 0;
        var duplicates = 0;
        lock (_gate)
        {
            var known = Known(servers);
            var fresh = new List<ServerDefinition>();
            foreach (var server in servers)
            {
                if (known.Add(Key(server)))
                {
                    fresh.Add(server);
                }
                else
                {
                    duplicates++;
                }
            }

            if (fresh.Count > 0)
            {
                var now = _time.GetUtcNow();
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                string? groupId = null;
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    groupId = "sub-" + Guid.NewGuid().ToString("N");
                    Run(connection, transaction,
                        "INSERT INTO subscriptions (id, name, position, updated_at, created_at) "
                        + "VALUES ($id, $name, (SELECT COALESCE(MAX(position), 0) + 1 FROM subscriptions), $at, $at)",
                        ("$id", groupId), ("$name", groupName.Trim()), ("$at", Text(now)));
                }

                var position = Rows.Count == 0 ? 0 : Rows.Max(r => r.Position);
                var newRows = new List<Row>();
                foreach (var server in fresh)
                {
                    var id = "srv-" + Guid.NewGuid().ToString("N");
                    position++;
                    // Секрет — первым: если хранилище откажет, строки в базе не останется.
                    _secrets.Save(SecretKey(id), JsonSerializer.Serialize(server.Credentials, Json));
                    Run(connection, transaction,
                        "INSERT INTO servers (id, subscription_id, position, definition, created_at) "
                        + "VALUES ($id, $group, $position, $definition, $at)",
                        ("$id", id), ("$group", groupId), ("$position", position),
                        ("$definition", JsonSerializer.Serialize(Stored.From(server), Json)), ("$at", Text(now)));
                    newRows.Add(new Row(ToProfile(id, Stored.From(server), groupId, null, null, false, null), position, Endpoint(server)));
                    added++;
                }

                if (_selectedId is null)
                {
                    _selectedId = newRows[0].Profile.Id;
                    Run(connection, transaction,
                        "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                        ("$key", SelectedKey), ("$value", _selectedId));
                }

                transaction.Commit();
                Rows.AddRange(newRows);
                if (groupId is not null)
                {
                    _subscriptions!.Add(new ServerSubscription(groupId, groupName!.Trim(), now));
                }
            }
        }

        if (added > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return new ImportOutcome(added, duplicates);
    }

    public ServerDefinition? Definition(string serverId)
    {
        string definition;
        lock (_gate)
        {
            var row = Rows.Find(r => r.Profile.Id == serverId);
            if (row is null)
            {
                return null;
            }

            definition = Scalar("SELECT definition FROM servers WHERE id = $id", ("$id", serverId)) as string ?? string.Empty;
        }

        var stored = JsonSerializer.Deserialize<Stored>(definition, Json)!;
        var credentials = _secrets.Read(SecretKey(serverId)) is { } secret
            ? JsonSerializer.Deserialize<ServerCredentials>(secret, Json)!
            : new ServerCredentials();
        return stored.ToDefinition(credentials);
    }

    // ── Хранение ─────────────────────────────────────────────────────────────

    /// <summary>Сервер в памяти: то, что видит интерфейс, порядок и адрес для поиска дублей.</summary>
    private sealed record Row(ServerProfile Profile, long Position, string Endpoint);

    /// <summary>Описание сервера в базе — все, кроме секретной части.</summary>
    private sealed record Stored(
        string Name, string Country, ProxyProtocol Protocol, string Host, int Port,
        TransportOptions Transport, SecurityOptions Security, ProtocolOptions Options)
    {
        public static Stored From(ServerDefinition s) =>
            new(s.Name, s.CountryCode, s.Protocol, s.Host, s.Port, s.Transport, s.Security, s.Options);

        public ServerDefinition ToDefinition(ServerCredentials credentials) =>
            new(Name, Country, Protocol, Host, Port, Transport, Security, Options, credentials);
    }

    private List<Row> Rows
    {
        get
        {
            EnsureLoaded();
            return _rows!;
        }
    }

    private void EnsureLoaded()
    {
        if (_rows is not null)
        {
            return;
        }

        using var connection = _database.Open();
        _subscriptions = [];
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, updated_at FROM subscriptions ORDER BY position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _subscriptions.Add(new ServerSubscription(reader.GetString(0), reader.GetString(1), Time(reader, 2)));
            }
        }

        _rows = [];
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, subscription_id, position, definition, custom_name, latency_ms, is_favorite, last_used_at "
                + "FROM servers ORDER BY position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var stored = JsonSerializer.Deserialize<Stored>(reader.GetString(3), Json)!;
                var profile = ToProfile(
                    reader.GetString(0),
                    stored,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt64(6) != 0,
                    Time(reader, 7));
                _rows.Add(new Row(profile, reader.GetInt64(2), Endpoint(stored.ToDefinition(new ServerCredentials()))));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", SelectedKey);
            var selected = command.ExecuteScalar() as string;
            _selectedId = _rows.Exists(r => r.Profile.Id == selected) ? selected : null;
        }
    }

    /// <summary>
    /// Ключи уже сохраненных серверов для поиска дублей: «адрес + данные доступа». Секреты из
    /// хранилища ОС читаются только у серверов с тем же адресом, что у новых.
    /// </summary>
    private HashSet<string> Known(IReadOnlyList<ServerDefinition> candidates)
    {
        var endpoints = candidates.Select(Endpoint).ToHashSet(StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows.Where(r => endpoints.Contains(r.Endpoint)))
        {
            var secret = _secrets.IsAvailable ? _secrets.Read(SecretKey(row.Profile.Id)) : null;
            known.Add(row.Endpoint + "|" + secret);
        }

        return known;
    }

    /// <summary>Ключ сервера для поиска дублей: адрес и данные доступа.</summary>
    private static string Key(ServerDefinition s) => Endpoint(s) + "|" + JsonSerializer.Serialize(s.Credentials, Json);

    /// <summary>Куда подключаться: протокол, адрес, транспорт и защита — без секретов.</summary>
    private static string Endpoint(ServerDefinition s) =>
        string.Join('|', s.Protocol, s.Host.ToLowerInvariant(), s.Port, s.Transport.Kind, s.Transport.Path,
            s.Transport.Host, s.Transport.ServiceName, s.Security.Kind, s.Security.Sni, s.Security.RealityPublicKey);

    private static ServerProfile ToProfile(
        string id, Stored stored, string? groupId, string? customName, int? latencyMs, bool isFavorite, DateTimeOffset? lastUsedAt) =>
        new(id,
            customName ?? stored.Name,
            stored.Country,
            stored.Protocol,
            stored.Security.Kind,
            stored.Host,
            stored.Port,
            stored.Transport.Kind switch
            {
                TransportKind.WebSocket => "ws",
                var kind => kind.ToString().ToLowerInvariant(),
            },
            latencyMs,
            City: null,
            SubscriptionId: groupId,
            IsFavorite: isFavorite,
            LastUsedAt: lastUsedAt);

    private static string SecretKey(string serverId) => "server:" + serverId;

    private void Replace(Row row) => Rows[Rows.FindIndex(r => r.Profile.Id == row.Profile.Id)] = row;

    private void SaveSelected(string? serverId)
    {
        if (serverId is null)
        {
            Execute("DELETE FROM settings WHERE key = $key", ("$key", SelectedKey));
        }
        else
        {
            Execute("INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$key", SelectedKey), ("$value", serverId));
        }
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _database.Open();
        Run(connection, null, sql, parameters);
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _database.Open();
        using var command = Command(connection, null, sql, parameters);
        return command.ExecuteScalar();
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Text(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture);
}
