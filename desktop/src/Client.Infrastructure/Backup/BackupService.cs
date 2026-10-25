using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure.Backup;

/// <summary>Что положить в копию: серверы и подписки, настройки, ключи доступа (только вместе с серверами).</summary>
public sealed record BackupParts(bool Servers, bool Settings, bool Keys);

/// <summary>Открытая копия: что в ней есть и когда она сделана.</summary>
public sealed class BackupContent
{
    internal BackupContent(BackupPayload payload) => Payload = payload;

    public DateTimeOffset CreatedAt => Payload.CreatedAt;

    public bool HasServers => Payload.Groups is not null;

    public bool HasSettings => Payload.Settings is not null;

    public bool HasKeys => Payload.Keys;

    public int ServerCount => Payload.Groups?.Sum(g => g.Servers.Count) ?? 0;

    internal BackupPayload Payload { get; }
}

/// <summary>Итог восстановления; серверов нет в копии — null.</summary>
public sealed record RestoreSummary(RestoreOutcome? Servers, bool Settings);

/// <summary>Содержимое копии до шифрования.</summary>
internal sealed record BackupPayload(
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Settings,
    List<SavedGroup>? Groups,
    bool Keys);

/// <summary>
/// Резервная копия (доска 14): файл .pgbackup под паролем и автокопия перед каждым восстановлением.
/// Автокопия — со всем, включая ключи: она зашифрована случайным ключом, который лежит в хранилище
/// ОС, а сам файл — в папке данных программы.
/// </summary>
public sealed class BackupService(SettingsService settings, IServerStore servers, ISecretStore secrets, AppPaths paths, TimeProvider time)
{
    /// <summary>Пароль короче — подбирается слишком быстро.</summary>
    public const int MinPasswordLength = 8;

    public const string Extension = ".pgbackup";

    private const string AutoCopyKey = "backup:autocopy";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string AutoCopyPath => Path.Combine(paths.DataDirectory, "autocopy" + Extension);

    /// <summary>Когда сделана последняя автокопия; null — ее нет.</summary>
    public DateTimeOffset? LastAutoCopy => File.Exists(AutoCopyPath) ? BackupFile.Created(File.ReadAllText(AutoCopyPath)) : null;

    /// <summary>Файл копии; долго (PBKDF2) — звать не из потока окна.</summary>
    public string Create(BackupParts parts, string password)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Пароль короче {MinPasswordLength} знаков", nameof(password));
        }

        var now = time.GetUtcNow();
        return BackupFile.Seal(Serialize(Collect(parts, now)), password, now);
    }

    /// <summary>Похоже ли на файл копии — до вопроса о пароле.</summary>
    public static bool IsBackup(string text) => BackupFile.Created(text) is not null;

    /// <summary>Открыть копию паролем; долго (PBKDF2) — звать не из потока окна.</summary>
    /// <exception cref="BackupException">Не копия, копия новее программы или пароль не подходит.</exception>
    public static BackupContent Read(string text, string password) => Deserialize(BackupFile.Open(text, password));

    /// <summary>
    /// Заменить серверы и настройки тем, что есть в копии. Сначала — автокопия текущего состояния:
    /// восстановление можно отменить.
    /// </summary>
    /// <exception cref="InvalidOperationException">Хранилище секретов ОС недоступно — автокопию и ключи сохранить негде.</exception>
    public RestoreSummary Restore(BackupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!secrets.IsAvailable)
        {
            throw new InvalidOperationException($"{secrets.Name} недоступно: автокопию и ключи сохранить негде");
        }

        SaveAutoCopy();
        var payload = content.Payload;
        var restored = payload.Groups is { } groups ? servers.Restore(groups) : null;
        if (payload.Settings is { } values)
        {
            var next = SqliteSettingsStore.Parse(values, _ => { });
            settings.Update(_ => next);
        }

        return new RestoreSummary(restored, payload.Settings is not null);
    }

    /// <summary>Вернуть состояние из автокопии; текущее перед этим само станет автокопией.</summary>
    /// <exception cref="BackupException">Автокопии нет или ее ключ потерян.</exception>
    public RestoreSummary RestoreAutoCopy()
    {
        if (!File.Exists(AutoCopyPath) || secrets.Read(AutoCopyKey) is not { } key)
        {
            throw new BackupException(BackupError.NotBackup);
        }

        return Restore(Deserialize(BackupFile.Open(File.ReadAllText(AutoCopyPath), Convert.FromBase64String(key))));
    }

    private void SaveAutoCopy()
    {
        var key = secrets.Read(AutoCopyKey) is { } stored ? Convert.FromBase64String(stored) : null;
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            secrets.Save(AutoCopyKey, Convert.ToBase64String(key));
        }

        var now = time.GetUtcNow();
        var text = BackupFile.Seal(Serialize(Collect(new BackupParts(Servers: true, Settings: true, Keys: true), now)), key, now);
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = AutoCopyPath + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, AutoCopyPath, overwrite: true);
    }

    private BackupPayload Collect(BackupParts parts, DateTimeOffset now)
    {
        List<SavedGroup>? groups = null;
        if (parts.Servers)
        {
            groups = [.. servers.Snapshot().Select(g => parts.Keys ? g : g with
            {
                Servers = [.. g.Servers.Select(s => s with { Definition = s.Definition with { Credentials = new ServerCredentials() } })],
            })];
        }

        return new BackupPayload(now, parts.Settings ? SqliteSettingsStore.Values(settings.Current) : null, groups, parts.Servers && parts.Keys);
    }

    private static byte[] Serialize(BackupPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload, Json);

    private static BackupContent Deserialize(byte[] content)
    {
        try
        {
            return new BackupContent(JsonSerializer.Deserialize<BackupPayload>(content, Json) ?? throw new BackupException(BackupError.NotBackup));
        }
        catch (JsonException e)
        {
            throw new BackupException("Содержимое копии не читается", e);
        }
    }
}
