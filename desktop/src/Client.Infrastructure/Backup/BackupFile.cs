using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateGateway.Client.Infrastructure.Backup;

/// <summary>Почему копию не открыть.</summary>
public enum BackupError
{
    /// <summary>Это не файл резервной копии или он испорчен так, что не читается.</summary>
    NotBackup,

    /// <summary>Копия из более новой версии программы.</summary>
    NewerVersion,

    /// <summary>Пароль не подходит или содержимое изменено: AES-GCM эти случаи не различает.</summary>
    WrongPassword,
}

public sealed class BackupException : Exception
{
    public BackupException(BackupError error)
        : base(error.ToString()) => Error = error;

    public BackupException()
        : this(BackupError.NotBackup)
    {
    }

    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BackupError Error { get; }
}

/// <summary>
/// Файл .pgbackup: JSON-обертка с параметрами и зашифрованным содержимым. Шифр — AES-256-GCM, ключ
/// — из пароля через PBKDF2-SHA256 (600 000 повторов, соль 16 байт) или готовый ключ из хранилища
/// ОС (автокопия). Параметры обертки и время создания входят в проверку подлинности: подменить их
/// незаметно нельзя.
/// </summary>
internal static class BackupFile
{
    public const string Format = "private-gateway-backup";
    public const int Version = 1;

    /// <summary>Повторов PBKDF2 при создании — по рекомендации OWASP для SHA-256.</summary>
    public const int Iterations = 600_000;

    private const string PasswordKdf = "pbkdf2-sha256";
    private const string StoredKey = "os-key";

    // Меньше — слабая защита пароля, больше — файл заставит считать часами: такой не открываем.
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    public static string Seal(byte[] content, string password, DateTimeOffset created)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Seal(content, PasswordKdf, Iterations, salt, Derive(password, salt, Iterations), created);
    }

    public static string Seal(byte[] content, byte[] key, DateTimeOffset created) =>
        Seal(content, StoredKey, 0, [], key, created);

    public static byte[] Open(string text, string password) =>
        Open(text, PasswordKdf, (salt, iterations) => Derive(password, salt, iterations));

    public static byte[] Open(string text, byte[] key) => Open(text, StoredKey, (_, _) => key);

    /// <summary>Время создания из обертки — без пароля; null — не копия.</summary>
    public static DateTimeOffset? Created(string text) =>
        TryParse(text) is { } envelope && DateTimeOffset.TryParse(envelope.Created, CultureInfo.InvariantCulture, out var created)
            ? created
            : null;

    private static string Seal(byte[] content, string kdf, int iterations, byte[] salt, byte[] key, DateTimeOffset created)
    {
        var envelope = new Envelope(Format, Version, kdf, iterations, Convert.ToBase64String(salt),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)), created.ToString("O", CultureInfo.InvariantCulture), string.Empty);
        var sealedData = new byte[content.Length + 16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(Convert.FromBase64String(envelope.Nonce), content, sealedData.AsSpan(0, content.Length),
                sealedData.AsSpan(content.Length), envelope.AssociatedData());
        }

        return new JsonObject
        {
            ["format"] = envelope.Format,
            ["version"] = envelope.Version,
            ["kdf"] = envelope.Kdf,
            ["iterations"] = envelope.Iterations,
            ["salt"] = envelope.Salt,
            ["nonce"] = envelope.Nonce,
            ["created"] = envelope.Created,
            ["data"] = Convert.ToBase64String(sealedData),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] Open(string text, string kdf, Func<byte[], int, byte[]> key)
    {
        var envelope = TryParse(text) ?? throw new BackupException(BackupError.NotBackup);
        if (envelope.Version > Version)
        {
            throw new BackupException(BackupError.NewerVersion);
        }

        byte[] salt, nonce, data;
        try
        {
            (salt, nonce, data) = (Convert.FromBase64String(envelope.Salt), Convert.FromBase64String(envelope.Nonce), Convert.FromBase64String(envelope.Data));
        }
        catch (FormatException e)
        {
            throw new BackupException("Обертка копии испорчена", e);
        }

        var iterationsOk = kdf == StoredKey || envelope.Iterations is >= MinIterations and <= MaxIterations;
        if (envelope.Kdf != kdf || !iterationsOk || nonce.Length != 12 || data.Length < 16)
        {
            throw new BackupException(BackupError.NotBackup);
        }

        var content = new byte[data.Length - 16];
        try
        {
            using var aes = new AesGcm(key(salt, envelope.Iterations), 16);
            aes.Decrypt(nonce, data.AsSpan(0, content.Length), data.AsSpan(content.Length), content, envelope.AssociatedData());
        }
        catch (CryptographicException)
        {
            throw new BackupException(BackupError.WrongPassword);
        }

        return content;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static Envelope? TryParse(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        return node is JsonObject o && Text(o, "format") == Format
            && Number(o, "version") is { } version && Text(o, "kdf") is { } kdf && Number(o, "iterations") is { } iterations
            && Text(o, "salt") is { } salt && Text(o, "nonce") is { } nonce && Text(o, "created") is { } created
            && Text(o, "data") is { } data
                ? new Envelope(Format, version, kdf, iterations, salt, nonce, created, data)
                : null;

        static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        static int? Number(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    }

    private sealed record Envelope(string Format, int Version, string Kdf, int Iterations, string Salt, string Nonce, string Created, string Data)
    {
        /// <summary>Все, кроме самих данных, — под защитой тега GCM.</summary>
        public byte[] AssociatedData() =>
            Encoding.UTF8.GetBytes(string.Join('|', Format, Version, Kdf, Iterations, Salt, Nonce, Created));
    }
}
