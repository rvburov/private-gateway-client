using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Import;
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
            Replace(row with { Profile = row.Profile with { Name = trimmed }, CustomName = trimmed });
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

    public ImportOutcome Add(IReadOnlyList<ServerDefinition> servers, string? groupName, Uri? subscriptionUrl = null)
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
                // Записанное в хранилище ОС в этой попытке: при сбое база откатится, а их — удалить.
                var saved = new List<string>();
                string? groupId = null;
                var newRows = new List<Row>();
                string? select = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(groupName))
                    {
                        groupId = "sub-" + Guid.NewGuid().ToString("N");
                        if (subscriptionUrl is not null)
                        {
                            Save(saved, SubscriptionKey(groupId), subscriptionUrl.ToString());
                        }

                        Run(connection, transaction,
                            "INSERT INTO subscriptions (id, name, position, updated_at, created_at, address) "
                            + "VALUES ($id, $name, (SELECT COALESCE(MAX(position), 0) + 1 FROM subscriptions), $at, $at, $address)",
                            ("$id", groupId), ("$name", groupName.Trim()), ("$at", Text(now)),
                            ("$address", subscriptionUrl is null ? null : SubscriptionAddress.Mask(subscriptionUrl)));
                    }

                    var position = Rows.Count == 0 ? 0 : Rows.Max(r => r.Position);
                    foreach (var server in fresh)
                    {
                        var id = "srv-" + Guid.NewGuid().ToString("N");
                        position++;
                        // Секрет — первым: если хранилище откажет, строки в базе не останется.
                        Save(saved, SecretKey(id), JsonSerializer.Serialize(server.Credentials, Json));
                        Run(connection, transaction,
                            "INSERT INTO servers (id, subscription_id, position, definition, created_at) "
                            + "VALUES ($id, $group, $position, $definition, $at)",
                            ("$id", id), ("$group", groupId), ("$position", position),
                            ("$definition", JsonSerializer.Serialize(Stored.From(server), Json)), ("$at", Text(now)));
                        newRows.Add(new Row(ToProfile(id, Stored.From(server), groupId, null, null, false, null), position, Endpoint(server), server.Name));
                        added++;
                    }

                    if (_selectedId is null)
                    {
                        select = newRows[0].Profile.Id;
                        Run(connection, transaction,
                            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                            ("$key", SelectedKey), ("$value", select));
                    }

                    transaction.Commit();
                }
                catch
                {
                    saved.ForEach(_secrets.Delete);
                    throw;
                }

                _selectedId ??= select;
                Rows.AddRange(newRows);
                if (groupId is not null)
                {
                    _subscriptions!.Add(new ServerSubscription(groupId, groupName!.Trim(), now,
                        subscriptionUrl is null ? null : SubscriptionAddress.Mask(subscriptionUrl)));
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

        return JsonSerializer.Deserialize<Stored>(definition, Json)!.ToDefinition(Credentials(serverId));
    }

    private ServerCredentials Credentials(string serverId) =>
        _secrets.IsAvailable && _secrets.Read(SecretKey(serverId)) is { } secret
            ? JsonSerializer.Deserialize<ServerCredentials>(secret, Json)!
            : new ServerCredentials();

    // ── Подписки ─────────────────────────────────────────────────────────────

    public Uri? SubscriptionUrl(string subscriptionId) =>
        _secrets.IsAvailable
        && _secrets.Read(SubscriptionKey(subscriptionId)) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var url)
            ? url
            : null;

    public SubscriptionUpdate ApplyUpdate(string subscriptionId, IReadOnlyList<ServerDefinition> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        if (servers.Count == 0)
        {
            throw new ArgumentException("Пустой список не заменяет подписку — прошлый остается", nameof(servers));
        }

        if (!_secrets.IsAvailable)
        {
            throw new InvalidOperationException($"{_secrets.Name} недоступно: серверы с секретами сохранить нельзя");
        }

        SubscriptionUpdate result;
        List<Row> removed;
        lock (_gate)
        {
            var index = Groups.FindIndex(s => s.Id == subscriptionId);
            if (index < 0)
            {
                throw new ArgumentException("Нет такой подписки", nameof(subscriptionId));
            }

            // Свой сервер узнается сначала точно (как дубль при импорте), потом по выходу: тот же
            // протокол, адрес, порт и данные доступа — поставщик мог поправить SNI или путь.
            var own = Rows.Where(r => r.Profile.SubscriptionId == subscriptionId).ToList();
            var ownByKey = new Dictionary<string, Row>(StringComparer.Ordinal);
            var ownByExit = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var row in own)
            {
                var secret = _secrets.Read(SecretKey(row.Profile.Id));
                ownByKey.TryAdd(row.Endpoint + "|" + secret, row);
                ownByExit.TryAdd(Exit(row.Profile.Protocol, row.Profile.Host, row.Profile.Port, secret), row);
            }

            // Сервер, который уже есть в другой группе, второй раз не добавляется — как при импорте.
            var elsewhere = Known(servers, r => r.Profile.SubscriptionId != subscriptionId);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<(Row Row, ServerDefinition Server)>();
            var keptIds = new HashSet<string>(StringComparer.Ordinal);
            var unmatched = new List<ServerDefinition>();
            foreach (var server in servers.Where(server => seen.Add(Key(server))))
            {
                if (ownByKey.TryGetValue(Key(server), out var row) && keptIds.Add(row.Profile.Id))
                {
                    kept.Add((row, server));
                }
                else
                {
                    unmatched.Add(server);
                }
            }

            var fresh = new List<ServerDefinition>();
            foreach (var server in unmatched)
            {
                var exit = Exit(server.Protocol, server.Host, server.Port, JsonSerializer.Serialize(server.Credentials, Json));
                if (ownByExit.TryGetValue(exit, out var row) && keptIds.Add(row.Profile.Id))
                {
                    kept.Add((row, server));
                }
                else if (!elsewhere.Contains(Key(server)))
                {
                    fresh.Add(server);
                }
            }
            var gone = own.Where(r => !keptIds.Contains(r.Profile.Id)).ToList();
            var selectedGone = gone.Find(r => r.Profile.Id == _selectedId);
            removed = [.. gone.Where(r => r != selectedGone)];

            var now = _time.GetUtcNow();
            var newRows = new List<Row>();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            var saved = new List<string>();
            try
            {
                foreach (var (row, server) in kept)
                {
                    Run(connection, transaction, "UPDATE servers SET definition = $definition, missing_since = NULL WHERE id = $id",
                        ("$definition", JsonSerializer.Serialize(Stored.From(server), Json)), ("$id", row.Profile.Id));
                }

                var position = Rows.Count == 0 ? 0 : Rows.Max(r => r.Position);
                foreach (var server in fresh)
                {
                    var id = "srv-" + Guid.NewGuid().ToString("N");
                    position++;
                    Save(saved, SecretKey(id), JsonSerializer.Serialize(server.Credentials, Json));
                    Run(connection, transaction,
                        "INSERT INTO servers (id, subscription_id, position, definition, created_at) "
                        + "VALUES ($id, $group, $position, $definition, $at)",
                        ("$id", id), ("$group", subscriptionId), ("$position", position),
                        ("$definition", JsonSerializer.Serialize(Stored.From(server), Json)), ("$at", Text(now)));
                    newRows.Add(new Row(ToProfile(id, Stored.From(server), subscriptionId, null, null, false, null), position, Endpoint(server), server.Name));
                }

                foreach (var row in removed)
                {
                    Run(connection, transaction, "DELETE FROM servers WHERE id = $id", ("$id", row.Profile.Id));
                }

                if (selectedGone is { Profile.MissingSince: null })
                {
                    Run(connection, transaction, "UPDATE servers SET missing_since = $at WHERE id = $id", ("$at", Text(now)), ("$id", selectedGone.Profile.Id));
                }

                Run(connection, transaction,
                    "UPDATE subscriptions SET updated_at = $at, last_attempt_at = $at, last_failure = NULL, last_status = NULL WHERE id = $id",
                    ("$at", Text(now)), ("$id", subscriptionId));
                transaction.Commit();
            }
            catch
            {
                saved.ForEach(_secrets.Delete);
                throw;
            }

            foreach (var (row, server) in kept)
            {
                var profile = ToProfile(row.Profile.Id, Stored.From(server), subscriptionId, row.CustomName, row.Profile.LatencyMs,
                    row.Profile.IsFavorite, row.Profile.LastUsedAt);
                Replace(row with { Profile = profile, Endpoint = Endpoint(server), LinkName = server.Name });
            }

            Rows.RemoveAll(removed.Contains);
            Rows.AddRange(newRows);
            if (selectedGone is not null)
            {
                Replace(selectedGone with { Profile = selectedGone.Profile with { MissingSince = selectedGone.Profile.MissingSince ?? now } });
            }

            Groups[index] = Groups[index] with { UpdatedAt = now, LastAttemptAt = now, LastFailure = null, LastStatusCode = null };
            var change = selectedGone is null ? SelectedServerChange.None
                : servers.Any(s => s.Name == selectedGone.LinkName) ? SelectedServerChange.Changed
                : SelectedServerChange.Missing;
            result = new SubscriptionUpdate(fresh.Count, removed.Count, kept.Count, change);
        }

        // Секреты удаленных — после записи в базу: сбой базы не оставит серверов без ключей.
        removed.ForEach(r => _secrets.Delete(SecretKey(r.Profile.Id)));
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void MarkUpdateFailed(string subscriptionId, DownloadFailure failure, int? statusCode = null)
    {
        lock (_gate)
        {
            var index = Groups.FindIndex(s => s.Id == subscriptionId);
            if (index < 0)
            {
                return;
            }

            var now = _time.GetUtcNow();
            Execute("UPDATE subscriptions SET last_attempt_at = $at, last_failure = $failure, last_status = $status WHERE id = $id",
                ("$at", Text(now)), ("$failure", failure.ToString()), ("$status", statusCode), ("$id", subscriptionId));
            Groups[index] = Groups[index] with { LastAttemptAt = now, LastFailure = failure, LastStatusCode = statusCode };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void EditSubscription(string subscriptionId, string name, Uri? newUrl = null)
    {
        lock (_gate)
        {
            var index = Groups.FindIndex(s => s.Id == subscriptionId);
            if (index < 0)
            {
                return;
            }

            var subscription = Groups[index];
            var trimmed = name?.Trim() is { Length: > 0 } n ? n : subscription.Name;
            if (newUrl is not null)
            {
                if (!_secrets.IsAvailable)
                {
                    throw new InvalidOperationException($"{_secrets.Name} недоступно: адрес подписки сохранить нельзя");
                }

                _secrets.Save(SubscriptionKey(subscriptionId), newUrl.ToString());
                subscription = subscription with
                {
                    Address = SubscriptionAddress.Mask(newUrl), LastAttemptAt = null, LastFailure = null, LastStatusCode = null,
                };
            }

            subscription = subscription with { Name = trimmed };
            Execute("UPDATE subscriptions SET name = $name, address = $address, last_attempt_at = $attempt, last_failure = $failure, "
                + "last_status = $status WHERE id = $id",
                ("$name", subscription.Name), ("$address", subscription.Address),
                ("$attempt", subscription.LastAttemptAt is { } at ? Text(at) : null),
                ("$failure", subscription.LastFailure?.ToString()), ("$status", subscription.LastStatusCode), ("$id", subscriptionId));
            Groups[index] = subscription;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSubscription(string? subscriptionId)
    {
        List<Row> rows;
        lock (_gate)
        {
            rows = [.. Rows.Where(r => r.Profile.SubscriptionId == subscriptionId)];
            var hasGroup = subscriptionId is not null && Groups.Exists(s => s.Id == subscriptionId);
            if (rows.Count == 0 && !hasGroup)
            {
                return;
            }

            var selectedInside = rows.Exists(r => r.Profile.Id == _selectedId);
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var row in rows)
            {
                Run(connection, transaction, "DELETE FROM servers WHERE id = $id", ("$id", row.Profile.Id));
            }

            if (hasGroup)
            {
                Run(connection, transaction, "DELETE FROM subscriptions WHERE id = $id", ("$id", subscriptionId));
            }

            if (selectedInside)
            {
                Run(connection, transaction, "DELETE FROM settings WHERE key = $key", ("$key", SelectedKey));
            }

            transaction.Commit();
            Rows.RemoveAll(rows.Contains);
            Groups.RemoveAll(s => s.Id == subscriptionId);
            if (selectedInside)
            {
                _selectedId = null;
            }
        }

        rows.ForEach(r => _secrets.Delete(SecretKey(r.Profile.Id)));
        if (subscriptionId is not null)
        {
            _secrets.Delete(SubscriptionKey(subscriptionId));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Резервная копия ──────────────────────────────────────────────────────

    public IReadOnlyList<SavedGroup> Snapshot()
    {
        List<Row> rows;
        List<ServerSubscription> groups;
        string? selected;
        var definitions = new Dictionary<string, Stored>(StringComparer.Ordinal);
        lock (_gate)
        {
            rows = [.. Rows];
            groups = [.. Groups];
            selected = _selectedId;
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, definition FROM servers";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                definitions[reader.GetString(0)] = JsonSerializer.Deserialize<Stored>(reader.GetString(1), Json)!;
            }
        }

        // «Мои серверы» — первыми, дальше группы в своем порядке.
        return [.. new string?[] { null }.Concat(groups.Select(g => g.Id))
            .Select(id => new SavedGroup(
                id is null ? null : groups.Single(g => g.Id == id).Name,
                id is null ? null : SubscriptionUrl(id),
                [.. rows.Where(r => r.Profile.SubscriptionId == id).Select(r => new SavedServer(
                    definitions[r.Profile.Id].ToDefinition(Credentials(r.Profile.Id)), r.CustomName, r.Profile.IsFavorite, r.Profile.Id == selected))]))
            .Where(g => g.Name is not null || g.Servers.Count > 0)];
    }

    public RestoreOutcome Restore(IReadOnlyList<SavedGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!_secrets.IsAvailable)
        {
            throw new InvalidOperationException($"{_secrets.Name} недоступно: серверы с секретами восстановить нельзя");
        }

        var restored = 0;
        var withoutKeys = 0;
        lock (_gate)
        {
            // Ключи здешних серверов — для серверов, сохраненных в копии без ключей.
            var local = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                if (_secrets.Read(SecretKey(row.Profile.Id)) is { } secret)
                {
                    local.TryAdd(row.Endpoint, secret);
                }
            }

            var oldKeys = Rows.Select(r => SecretKey(r.Profile.Id)).Concat(Groups.Select(g => SubscriptionKey(g.Id))).ToList();
            var now = _time.GetUtcNow();
            var newRows = new List<Row>();
            var newGroups = new List<ServerSubscription>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? select = null;
            var saved = new List<string>();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                Run(connection, transaction, "DELETE FROM servers");
                Run(connection, transaction, "DELETE FROM subscriptions");
                Run(connection, transaction, "DELETE FROM settings WHERE key = $key", ("$key", SelectedKey));
                foreach (var group in groups)
                {
                    string? groupId = null;
                    if (!string.IsNullOrWhiteSpace(group.Name))
                    {
                        groupId = "sub-" + Guid.NewGuid().ToString("N");
                        var address = group.SubscriptionUrl is null ? null : SubscriptionAddress.Mask(group.SubscriptionUrl);
                        if (group.SubscriptionUrl is not null)
                        {
                            Save(saved, SubscriptionKey(groupId), group.SubscriptionUrl.ToString());
                        }

                        Run(connection, transaction,
                            "INSERT INTO subscriptions (id, name, position, updated_at, created_at, address) VALUES ($id, $name, $position, $updated, $at, $address)",
                            ("$id", groupId), ("$name", group.Name.Trim()), ("$position", newGroups.Count + 1),
                            ("$updated", address is null ? Text(now) : null), ("$at", Text(now)), ("$address", address));
                        newGroups.Add(new ServerSubscription(groupId, group.Name.Trim(), address is null ? now : null, address));
                    }

                    foreach (var server in group.Servers)
                    {
                        var definition = server.Definition;
                        var endpoint = Endpoint(definition);
                        var secret = definition.Credentials != new ServerCredentials()
                            ? JsonSerializer.Serialize(definition.Credentials, Json)
                            : local.GetValueOrDefault(endpoint);
                        if (secret is null)
                        {
                            withoutKeys++;
                            continue;
                        }

                        if (!seen.Add(endpoint + "|" + secret))
                        {
                            continue;
                        }

                        var id = "srv-" + Guid.NewGuid().ToString("N");
                        var position = newRows.Count + 1;
                        var customName = server.CustomName?.Trim() is { Length: > 0 } name ? name : null;
                        Save(saved, SecretKey(id), secret);
                        Run(connection, transaction,
                            "INSERT INTO servers (id, subscription_id, position, definition, custom_name, is_favorite, created_at) "
                            + "VALUES ($id, $group, $position, $definition, $custom, $favorite, $at)",
                            ("$id", id), ("$group", groupId), ("$position", position),
                            ("$definition", JsonSerializer.Serialize(Stored.From(definition), Json)), ("$custom", customName),
                            ("$favorite", server.IsFavorite ? 1 : 0), ("$at", Text(now)));
                        newRows.Add(new Row(ToProfile(id, Stored.From(definition), groupId, customName, null, server.IsFavorite, null),
                            position, endpoint, definition.Name, customName));
                        if (server.IsSelected)
                        {
                            select = id;
                        }

                        restored++;
                    }
                }

                if (select is not null)
                {
                    Run(connection, transaction, "INSERT INTO settings (key, value) VALUES ($key, $value)", ("$key", SelectedKey), ("$value", select));
                }

                transaction.Commit();
            }
            catch
            {
                saved.ForEach(_secrets.Delete);
                throw;
            }

            oldKeys.ForEach(_secrets.Delete);
            _rows = newRows;
            _subscriptions = newGroups;
            _selectedId = select;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new RestoreOutcome(restored, withoutKeys);
    }

    // ── Хранение ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Сервер в памяти: то, что видит интерфейс, порядок, адрес для поиска дублей, название из
    /// ссылки и свое название пользователя (оно переживает обновление подписки).
    /// </summary>
    private sealed record Row(ServerProfile Profile, long Position, string Endpoint, string LinkName, string? CustomName = null);

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

    private List<ServerSubscription> Groups
    {
        get
        {
            EnsureLoaded();
            return _subscriptions!;
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
            command.CommandText = "SELECT id, name, updated_at, address, last_attempt_at, last_failure, last_status "
                + "FROM subscriptions ORDER BY position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _subscriptions.Add(new ServerSubscription(
                    reader.GetString(0),
                    reader.GetString(1),
                    Time(reader, 2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    Time(reader, 4),
                    reader.IsDBNull(5) ? null : Enum.TryParse<DownloadFailure>(reader.GetString(5), out var failure) ? failure : DownloadFailure.Network,
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }
        }

        _rows = [];
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, subscription_id, position, definition, custom_name, latency_ms, is_favorite, last_used_at, "
                + "missing_since FROM servers ORDER BY position";
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
                    Time(reader, 7),
                    Time(reader, 8));
                _rows.Add(new Row(profile, reader.GetInt64(2), Endpoint(stored.ToDefinition(new ServerCredentials())), stored.Name,
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", SelectedKey);
            var selected = command.ExecuteScalar() as string;
            _selectedId = _rows.Exists(r => r.Profile.Id == selected) ? selected : null;
        }

        BackfillAddresses(connection);
    }

    /// <summary>
    /// Подписки, сохраненные до миграции 0004, знают адрес только в хранилище ОС: подпись адреса
    /// берется оттуда один раз, иначе такие подписки выглядели бы как группы без адреса.
    /// </summary>
    private void BackfillAddresses(SqliteConnection connection)
    {
        if (!_secrets.IsAvailable)
        {
            return;
        }

        for (var i = 0; i < _subscriptions!.Count; i++)
        {
            var subscription = _subscriptions[i];
            if (subscription.Address is not null
                || _secrets.Read(SubscriptionKey(subscription.Id)) is not { } text
                || !Uri.TryCreate(text, UriKind.Absolute, out var url))
            {
                continue;
            }

            var address = SubscriptionAddress.Mask(url);
            Run(connection, null, "UPDATE subscriptions SET address = $address WHERE id = $id", ("$address", address), ("$id", subscription.Id));
            _subscriptions[i] = subscription with { Address = address };
        }
    }

    /// <summary>
    /// Ключи уже сохраненных серверов для поиска дублей: «адрес + данные доступа». Секреты из
    /// хранилища ОС читаются только у серверов с тем же адресом, что у новых.
    /// </summary>
    private HashSet<string> Known(IReadOnlyList<ServerDefinition> candidates, Func<Row, bool>? among = null)
    {
        var endpoints = candidates.Select(Endpoint).ToHashSet(StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows.Where(r => endpoints.Contains(r.Endpoint) && (among is null || among(r))))
        {
            var secret = _secrets.IsAvailable ? _secrets.Read(SecretKey(row.Profile.Id)) : null;
            known.Add(row.Endpoint + "|" + secret);
        }

        return known;
    }

    /// <summary>Ключ сервера для поиска дублей: адрес и данные доступа.</summary>
    private static string Key(ServerDefinition s) => Endpoint(s) + "|" + JsonSerializer.Serialize(s.Credentials, Json);

    /// <summary>Выход сервера: протокол, адрес, порт и данные доступа — без транспорта и защиты.</summary>
    private static string Exit(ProxyProtocol protocol, string host, int port, string? secret) =>
        string.Join('|', protocol, host.ToLowerInvariant(), port, secret);

    /// <summary>Куда подключаться: протокол, адрес, транспорт и защита — без секретов.</summary>
    private static string Endpoint(ServerDefinition s) =>
        string.Join('|', s.Protocol, s.Host.ToLowerInvariant(), s.Port, s.Transport.Kind, s.Transport.Path,
            s.Transport.Host, s.Transport.ServiceName, s.Security.Kind, s.Security.Sni, s.Security.RealityPublicKey);

    private static ServerProfile ToProfile(
        string id, Stored stored, string? groupId, string? customName, int? latencyMs, bool isFavorite, DateTimeOffset? lastUsedAt,
        DateTimeOffset? missingSince = null) =>
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
            LastUsedAt: lastUsedAt,
            MissingSince: missingSince);

    private static string SecretKey(string serverId) => "server:" + serverId;

    private static string SubscriptionKey(string groupId) => "subscription:" + groupId;

    private void Save(List<string> saved, string key, string secret)
    {
        _secrets.Save(key, secret);
        saved.Add(key);
    }

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
